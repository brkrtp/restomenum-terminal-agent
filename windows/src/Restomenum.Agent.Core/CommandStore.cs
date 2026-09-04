using Microsoft.Data.Sqlite;

namespace Restomenum.Agent.Core;

/// <summary>Store'daki bir komut.</summary>
public sealed record StoredCommand(
    string CommandId,
    string PaymentId,
    string TerminalId,
    long ReceivedAt,
    long ExpiresAt,
    CommandState State,
    string? TerminalReference,
    string? ResultJson);

/// <summary><see cref="CommandStore.Save"/> sonucu — çağıran buna göre dallanır.</summary>
public abstract record SaveResult
{
    /// <summary>İlk kez görüldü; terminale gönderilebilir.</summary>
    public sealed record New(StoredCommand Command) : SaveResult;

    /// <summary>
    /// Aynı <c>commandId</c> daha önce görüldü. <b>Terminal ÇAĞRILMAZ</b>, önceki durum replay
    /// edilir (§12.2/3).
    /// </summary>
    public sealed record Duplicate(StoredCommand Command) : SaveResult;
}

/// <summary>
/// Agent'ın yerel dayanıklı komut deposu (§12.3) — SQLite WAL.
///
/// <para><b>§12.2/2: dedupe ATOMİK olmak ZORUNDA.</b> "Önce SELECT, yoksa INSERT" YASAKTIR ve sebebi
/// somut: oturum devri (session takeover) anında iki soket aynı komutu aynı anda alabilir. İkisi de
/// SELECT'te "yok" görür, ikisi de INSERT eder ve komut <b>iki kez</b> terminale gider — yani
/// müşterinin kartından iki kez çekilir. <c>INSERT OR IGNORE</c> + <c>changes()</c> bu yarışı
/// veritabanı seviyesinde kapatır: tam olarak biri 1 satır etkiler.</para>
///
/// <para><b>§12.2/1: yazılmadan onay YOK.</b> <c>command.ack</c> yalnız <see cref="Save"/> başarıyla
/// döndükten sonra gönderilir. Önce onaylayıp sonra yazmak, crash anında komutu hem kaybetmek hem
/// "aldım" demiş olmak demektir.</para>
///
/// <para><b>Kart verisi TUTULMAZ</b> (§12.3): <c>ResultJson</c> yalnız maskeli/referans alanları taşır.</para>
///
/// <para>Android karşılığı: <c>android/core/.../CommandStore.kt</c> — aynı şema, aynı davranış.</para>
/// </summary>
public sealed class CommandStore : IDisposable
{
    private readonly SqliteConnection _conn;

    /// <summary>
    /// <b>Tek bağlantı paylaşıldığı için ZORUNLU.</b> <c>Microsoft.Data.Sqlite</c>'ın
    /// <see cref="SqliteConnection"/>'ı thread-safe DEĞİLDİR; <c>PRAGMA busy_timeout</c> yalnız
    /// <i>ayrı</i> bağlantılar arasındaki dosya kilidini bekletir, aynı nesneye eşzamanlı erişimi
    /// korumaz.
    ///
    /// <para>Bu sınıfın bütün varlık sebebi eşzamanlı çift-tahsilatı önlemek olduğu için burası
    /// kritik: oturum devrinde (§12.2/2) iki soket aynı komutu aynı anda işleyebilir ve tam o anda
    /// iki iş parçacığı aynı bağlantıya girer. Kilitsiz bırakmak, yarışı kapatmak için yazılmış
    /// kodun kendisini yarışa açık bırakmak olurdu.</para>
    ///
    /// <para>İşlemler kısa ve senkron olduğu için düz bir <c>lock</c> yeterli; ölçülen maliyeti
    /// bir kart işleminin (20–32 sn) yanında ihmal edilebilir.</para>
    /// </summary>
    private readonly object _gate = new();

    private CommandStore(SqliteConnection conn) => _conn = conn;

    public static CommandStore Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            // WAL: finansal sonuç yazımı okuma trafiğine takılmasın; crash sonrası tutarlı.
            cmd.CommandText = "PRAGMA journal_mode=WAL";
            cmd.ExecuteNonQuery();
            // Fiş öncesi commit'in gerçekten diskte olması gerekiyor (§12.3).
            cmd.CommandText = "PRAGMA synchronous=FULL";
            cmd.ExecuteNonQuery();
            // Eşzamanlı yazıcılarda "database is locked" yerine bekle.
            cmd.CommandText = "PRAGMA busy_timeout=5000";
            cmd.ExecuteNonQuery();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS commands (
                    command_id TEXT PRIMARY KEY,
                    payment_id TEXT NOT NULL,
                    terminal_id TEXT NOT NULL,
                    received_at INTEGER NOT NULL,
                    expires_at INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    terminal_reference TEXT,
                    result_json TEXT,
                    updated_at INTEGER NOT NULL
                )
                """;
            cmd.ExecuteNonQuery();
        }
        return new CommandStore(conn);
    }

    /// <summary>Komutu <b>atomik</b> kaydeder.</summary>
    public SaveResult Save(
        string commandId, string paymentId, string terminalId, long expiresAt, long? now = null)
    {
        lock (_gate)
        {
            var ts = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int affected;
            using (var cmd = _conn.CreateCommand())
            {
                // TEK ifade: oku-sonra-yaz DEĞİL. Çakışmada sessizce 0 satır etkiler.
                cmd.CommandText = """
                    INSERT OR IGNORE INTO commands
                        (command_id, payment_id, terminal_id, received_at, expires_at, state, updated_at)
                    VALUES ($cid, $pid, $tid, $now, $exp, $state, $now)
                    """;
                cmd.Parameters.AddWithValue("$cid", commandId);
                cmd.Parameters.AddWithValue("$pid", paymentId);
                cmd.Parameters.AddWithValue("$tid", terminalId);
                cmd.Parameters.AddWithValue("$now", ts);
                cmd.Parameters.AddWithValue("$exp", expiresAt);
                cmd.Parameters.AddWithValue("$state", CommandState.RECEIVED.ToString());
                affected = cmd.ExecuteNonQuery();
            }
            var stored = Read(commandId) ?? throw new InvalidOperationException($"kayıt sonrası okunamadı: {commandId}");
            return affected == 1 ? new SaveResult.New(stored) : new SaveResult.Duplicate(stored);
        }
    }

    public StoredCommand? Read(string commandId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM commands WHERE command_id = $cid";
            cmd.Parameters.AddWithValue("$cid", commandId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new StoredCommand(
                r.GetString(r.GetOrdinal("command_id")),
                r.GetString(r.GetOrdinal("payment_id")),
                r.GetString(r.GetOrdinal("terminal_id")),
                r.GetInt64(r.GetOrdinal("received_at")),
                r.GetInt64(r.GetOrdinal("expires_at")),
                Enum.Parse<CommandState>(r.GetString(r.GetOrdinal("state"))),
                r.IsDBNull(r.GetOrdinal("terminal_reference")) ? null : r.GetString(r.GetOrdinal("terminal_reference")),
                r.IsDBNull(r.GetOrdinal("result_json")) ? null : r.GetString(r.GetOrdinal("result_json")));
        }
    }

    /// <summary>
    /// Durumu ilerletir — <b>yalnız izinli geçişler</b> ve <b>yalnız beklenen mevcut durumdan</b>.
    ///
    /// <c>WHERE state = $expected</c> şartı yarışı kapatır: iki iş parçacığı aynı anda ilerletmeye
    /// çalışırsa tam olarak biri 1 satır etkiler. Kontrolü koda taşımak (oku, karşılaştır, yaz)
    /// <see cref="Save"/>'deki hatayı tekrarlamak olurdu.
    /// </summary>
    public bool Advance(
        string commandId, CommandState expected, CommandState next,
        string? terminalReference = null, string? resultJson = null, long? now = null)
    {
        lock (_gate)
        {
            if (!AgentStateRules.CanTransition(expected, next)) return false;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE commands
                   SET state = $next, updated_at = $now,
                       terminal_reference = COALESCE($ref, terminal_reference),
                       result_json = COALESCE($res, result_json)
                 WHERE command_id = $cid AND state = $expected
                """;
            cmd.Parameters.AddWithValue("$next", next.ToString());
            cmd.Parameters.AddWithValue("$now", now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$ref", (object?)terminalReference ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$res", (object?)resultJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cid", commandId);
            cmd.Parameters.AddWithValue("$expected", expected.ToString());
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// Retention (§12.3: 7–30 gün). Kesin sonuca ulaşmış ESKİ kayıtlar silinir; <b>uçuştakiler ASLA</b> —
    /// <c>UNKNOWN</c> bir kaydı silmek, çözülmemiş bir tahsilatı kaybetmektir.
    /// </summary>
    public int Purge(long olderThan)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "DELETE FROM commands WHERE updated_at < $t AND state IN ('COMPLETED','EXPIRED','REJECTED')";
            cmd.Parameters.AddWithValue("$t", olderThan);
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();
}
