using Microsoft.Data.Sqlite;

namespace Restomenum.Agent.Core;

/// <summary>Gönderilmeyi bekleyen bir sonuç.</summary>
public sealed record OutboxEntry(
    string EventId,
    string PaymentId,
    string Status,
    string PayloadJson,
    string ProviderPluginId,
    long CreatedAt,
    int Attempts);

/// <summary>
/// **Değişmez #7** — bulut koparsa terminal işlemi devam eder, sonuç burada bekler.
///
/// <para>İhlalin bedeli net: <b>tahsilat gerçekleşir ama deftere hiç yazılmaz.</b> Müşterinin
/// kartından para çekilmiştir, restoranın kaydında o para yoktur. Bu, kaybolan bir ağ paketinin
/// muhasebe hatasına dönüşmesidir ve sonradan ancak elle mutabakatla bulunur.</para>
///
/// <para><b>Sıra pazarlık konusu değil:</b> sonuç önce buraya yazılır, sonra gönderilmeye çalışılır,
/// ve <b>yalnız gateway "yazdım" dedikten sonra</b> silinir. Ters sıra — gönder, sonra yaz — ağ
/// koptuğu anda sonucu kaybeder. Gönderdikten hemen sonra silmek de yetmez: gateway'in
/// <c>status.ack</c>'i "JetStream'e dayanıklı olarak yazdım" demektir, ondan öncesi havada.</para>
///
/// <para><b>Neden ayrı bağlantı:</b> aynı SQLite dosyasına ikinci bir bağlantı açılır. WAL modu
/// bunu destekler ve iki sorumluluğu (komut durumu / gönderim kuyruğu) ayrı tutmak, tek bir sınıfın
/// iki işi birden yapmasından iyidir. Eşzamanlılık her iki tarafta da kendi kilidiyle korunur.</para>
///
/// <para><b>Retention YOK.</b> <see cref="CommandStore.Purge"/>'ün aksine burada süreye bağlı silme
/// yoktur: gönderilmemiş bir sonuç ne kadar eskirse eskisin <b>çözülmemiş bir tahsilattır</b>.
/// Silmek, parayı kaybetmektir. Kuyruk büyüyorsa bu bir alarm sebebidir, temizlik sebebi değil.</para>
/// </summary>
public sealed class Outbox : IDisposable
{
    private readonly SqliteConnection _conn;

    /// <summary><see cref="CommandStore"/> ile aynı gerekçe: bağlantı thread-safe değildir.</summary>
    private readonly object _gate = new();

    private Outbox(SqliteConnection conn) => _conn = conn;

    public static Outbox Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL";
            cmd.ExecuteNonQuery();
            // Para sonucunun gerçekten diskte olması gerekiyor — çökme anında kaybolamaz.
            cmd.CommandText = "PRAGMA synchronous=FULL";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "PRAGMA busy_timeout=5000";
            cmd.ExecuteNonQuery();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS outbox (
                    event_id           TEXT PRIMARY KEY,
                    payment_id         TEXT NOT NULL,
                    status             TEXT NOT NULL,
                    payload_json       TEXT NOT NULL,
                    provider_plugin_id TEXT NOT NULL,
                    created_at         INTEGER NOT NULL,
                    attempts           INTEGER NOT NULL DEFAULT 0
                )
                """;
            cmd.ExecuteNonQuery();
            // ── W20: KAYIT BAŞINA GERİ ÇEKİLME ────────────────────────────────────────
            // Sütun SONRADAN eklendi ve varsayılanı 0 — eski kayıtlar (ve eski şemayla açılan
            // dosyalar) HEMEN uygun sayılır, yani geçiş bir tek bildirimi bile geciktirmez.
            var kolonlar = new List<string>();
            cmd.CommandText = "PRAGMA table_info(outbox)";
            using (var r = cmd.ExecuteReader())
                while (r.Read()) kolonlar.Add(r.GetString(1));
            if (!kolonlar.Contains("next_attempt_at"))
            {
                cmd.CommandText = "ALTER TABLE outbox ADD COLUMN next_attempt_at INTEGER NOT NULL DEFAULT 0";
                cmd.ExecuteNonQuery();
            }

            // Gönderim sırası kuyruk sırasıdır: en eski sonuç en önce yazılmalı.
            cmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_outbox_created ON outbox(created_at)";
            cmd.ExecuteNonQuery();
        }
        return new Outbox(conn);
    }

    /// <summary>
    /// Sonucu kuyruğa yazar. <c>eventId</c> birincil anahtardır: aynı sonuç iki kez kuyruğa
    /// girmez — <b>uygulama içi "önce bak sonra yaz" ile değil, veritabanı kısıtıyla.</b>
    /// </summary>
    /// <returns><c>true</c> yeni yazıldı, <c>false</c> zaten vardı.</returns>
    public bool Enqueue(
        string eventId, string paymentId, string status, string payloadJson,
        string providerPluginId, long? now = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO outbox
                    (event_id, payment_id, status, payload_json, provider_plugin_id, created_at)
                VALUES ($eid, $pid, $st, $pl, $prov, $now)
                """;
            cmd.Parameters.AddWithValue("$eid", eventId);
            cmd.Parameters.AddWithValue("$pid", paymentId);
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$pl", payloadJson);
            cmd.Parameters.AddWithValue("$prov", providerPluginId);
            cmd.Parameters.AddWithValue("$now", now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>Gönderilmeyi bekleyenler — <b>en eski önce</b>.</summary>
    /// <param name="ignoreBackoff">
    /// <c>true</c> ise geri çekilme BİR TURLUK atlanır. Bağlantı yeniden kurulduğunda kullanılır:
    /// bekleme sebebi ağ kesintisiyse, ağ geri geldiği anda beklemenin bir anlamı kalmaz.
    /// </param>
    /// <param name="now">Şimdiki zaman (unix ms) — testlerde sabitlenir.</param>
    public IReadOnlyList<OutboxEntry> Pending(int limit = 50, bool ignoreBackoff = false, long? now = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            // Sıra DEĞİŞMEDİ: en eski önce. Süzgeç yalnız "vakti geldi mi" sorusunu ekliyor.
            cmd.CommandText = ignoreBackoff
                ? "SELECT * FROM outbox ORDER BY created_at ASC LIMIT $n"
                : "SELECT * FROM outbox WHERE next_attempt_at <= $now ORDER BY created_at ASC LIMIT $n";
            if (!ignoreBackoff)
                cmd.Parameters.AddWithValue("$now", now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            var liste = new List<OutboxEntry>();
            while (r.Read())
            {
                liste.Add(new OutboxEntry(
                    r.GetString(r.GetOrdinal("event_id")),
                    r.GetString(r.GetOrdinal("payment_id")),
                    r.GetString(r.GetOrdinal("status")),
                    r.GetString(r.GetOrdinal("payload_json")),
                    r.GetString(r.GetOrdinal("provider_plugin_id")),
                    r.GetInt64(r.GetOrdinal("created_at")),
                    r.GetInt32(r.GetOrdinal("attempts"))));
            }
            return liste;
        }
    }

    /// <summary>
    /// Gateway sonucu <b>dayanıklı olarak yazdığını</b> onayladı — ancak şimdi silinebilir.
    /// Gönderdikten hemen sonra silmek, onay gelmezse sonucu kaybetmek olurdu.
    /// </summary>
    public bool Confirm(string eventId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM outbox WHERE event_id = $eid";
            cmd.Parameters.AddWithValue("$eid", eventId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>İlk yeniden deneme aralığı.</summary>
    public static readonly TimeSpan IlkAralik = TimeSpan.FromSeconds(30);

    /// <summary>Geri çekilmenin ÜST SINIRI. Bunun ötesine çıkmak kaydı fiilen unutmak olurdu.</summary>
    public static readonly TimeSpan AzamiAralik = TimeSpan.FromMinutes(5);

    /// <summary>
    /// <paramref name="attempts"/> denemeden sonraki bekleme: 30 sn × 2^(n−1), 5 dakikada sabitlenir.
    /// 1→30 sn · 2→60 · 3→120 · 4→240 · 5+→300.
    /// </summary>
    public static TimeSpan Bekleme(int attempts)
    {
        if (attempts <= 1) return IlkAralik;
        // Üs 30'u aşarsa taşma olur; sınır zaten 5 dk olduğu için erken kırpıyoruz.
        var us = Math.Min(attempts - 1, 20);
        var sn = IlkAralik.TotalSeconds * Math.Pow(2, us);
        return sn >= AzamiAralik.TotalSeconds ? AzamiAralik : TimeSpan.FromSeconds(sn);
    }

    /// <summary>
    /// Deneme sayacını artırır ve bir sonraki denemeyi <b>üstel olarak</b> erteler (W20).
    ///
    /// <para><b>Vazgeçme YOK, silme YOK.</b> Bu kayıtlar para satırı taşıyor; ertelemek başka,
    /// unutmak başka. Üst sınır 5 dakika — kalıcı bir arızada dakikada 2 istek yerine 5 dakikada
    /// 1 istek olur, ama kayıt sonsuza kadar kuyrukta kalır ve uç düzelince gider.</para>
    /// </summary>
    /// <returns>Artırımdan SONRAKİ deneme sayısı — çağıran alarm eşiğini buna göre kurar.</returns>
    public int MarkAttempt(string eventId, long? now = null)
    {
        lock (_gate)
        {
            var simdi = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE outbox
                   SET attempts = attempts + 1,
                       next_attempt_at = $now + $bekleme
                 WHERE event_id = $eid
                """;
            // Yeni sayaç için önce oku: SQLite'ta UPDATE ... RETURNING her sürümde yok.
            using var oku = _conn.CreateCommand();
            oku.CommandText = "SELECT attempts FROM outbox WHERE event_id = $eid";
            oku.Parameters.AddWithValue("$eid", eventId);
            var mevcut = oku.ExecuteScalar();
            if (mevcut is null) return 0;                      // kayıt yok (Confirm edilmiş olabilir)
            var yeni = Convert.ToInt32(mevcut) + 1;

            cmd.Parameters.AddWithValue("$eid", eventId);
            cmd.Parameters.AddWithValue("$now", simdi);
            cmd.Parameters.AddWithValue("$bekleme", (long)Bekleme(yeni).TotalMilliseconds);
            cmd.ExecuteNonQuery();
            return yeni;
        }
    }

    /// <summary>Kuyruk derinliği. Sıfırdan büyük kalması <b>çözülmemiş tahsilat</b> demektir.</summary>
    public int Depth()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM outbox";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Dispose() => _conn.Dispose();
}

/// <summary>
/// Outbox kayıt türü (<c>Status</c> sütunu). Replay hangi uca gideceğini buradan bilir; hepsini
/// ödeme ucuna göndermek, replay edilen her fiş iptalini 404/409'a düşürürdü.
/// </summary>
public static class OutboxKinds
{
    public const string Result = "result";
    public const string TicketCancel = "ticket-cancel";

    /// <summary>
    /// Fiş KAPANDI bildirimi (K-26): ödemeler deftere fiş kapanınca TEK seferde yazılıyor.
    /// Uç adresi P27 ile kesinleşecek; tür şimdiden ayrı çünkü replay doğru uca gitmeli.
    /// </summary>
    public const string TicketClosed = "ticket-closed";
}
