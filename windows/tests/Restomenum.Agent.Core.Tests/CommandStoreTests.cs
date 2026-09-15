using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// §12.2 değişmezleri — Android'deki <c>AgentCoreTest.kt</c>'nin .NET karşılığı.
/// Aynı senaryolar, aynı beklentiler: iki agent ayrışamaz.
/// </summary>
public class CommandStoreTests
{
    private static string TempDb() =>
        Path.Combine(Directory.CreateTempSubdirectory("agent-test").FullName, "agent.db");

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void Invariant_12_2_2_SameCommandIdCannotBeSavedTwice()
    {
        using var store = CommandStore.Open(TempDb());
        var a = store.Save("cmd-1", "pay-1", "t1", Now + 60_000);
        var b = store.Save("cmd-1", "pay-1", "t1", Now + 60_000);
        Assert.IsType<SaveResult.New>(a);
        Assert.IsType<SaveResult.Duplicate>(b);
    }

    [Fact]
    public void Invariant_12_2_2_ConcurrentSaveYieldsExactlyOneNew()
    {
        // Oturum devri anında iki soket aynı komutu alabilir. "Oku-sonra-yaz" olsaydı ikisi de
        // "yok" görür, ikisi de yazar ve komut İKİ KEZ terminale giderdi — çift tahsilat.
        var path = TempDb();
        using (CommandStore.Open(path)) { }   // şemayı kur

        var results = new SaveResult[8];
        Parallel.For(0, 8, i =>
        {
            // Her iş parçacığı KENDİ bağlantısını açar — gerçek eşzamanlılık.
            using var s = CommandStore.Open(path);
            results[i] = s.Save("cmd-race", "pay-1", "t1", Now + 60_000);
        });

        Assert.Equal(1, results.Count(r => r is SaveResult.New));
    }

    [Fact]
    public void Invariant_12_2_3_DuplicateReplaysStoredResult()
    {
        using var store = CommandStore.Open(TempDb());
        store.Save("cmd-2", "pay-2", "t1", Now + 60_000);
        store.Advance("cmd-2", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL);
        store.Advance("cmd-2", CommandState.SENT_TO_TERMINAL, CommandState.COMPLETED,
            resultJson: """{"rrn":"418512345678"}""");

        var again = store.Save("cmd-2", "pay-2", "t1", Now + 60_000);
        var dup = Assert.IsType<SaveResult.Duplicate>(again);
        // Replay, terminale yeniden gitmek DEĞİL saklanan sonucu döndürmektir.
        Assert.Equal(CommandState.COMPLETED, dup.Command.State);
        Assert.NotNull(dup.Command.ResultJson);
    }

    [Fact]
    public void CommandCannotCompleteWithoutReachingTerminal()
    {
        // Doğrudan RECEIVED → COMPLETED, "terminale hiç gitmeden başarılı oldu" demek olurdu.
        Assert.False(AgentStateRules.CanTransition(CommandState.RECEIVED, CommandState.COMPLETED));
    }

    [Fact]
    public void Advance_RejectsWrongExpectedState()
    {
        using var store = CommandStore.Open(TempDb());
        store.Save("cmd-3", "pay-3", "t1", Now + 60_000);
        store.Advance("cmd-3", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL);
        // Beklenen durum artık RECEIVED değil → yazma REDDEDİLİR (yarış koruması).
        Assert.False(store.Advance("cmd-3", CommandState.RECEIVED, CommandState.EXPIRED));
        Assert.Equal(CommandState.SENT_TO_TERMINAL, store.Read("cmd-3")!.State);
    }

    [Fact]
    public void Invariant_12_3_PurgeNeverDeletesInFlight()
    {
        using var store = CommandStore.Open(TempDb());
        var old = Now - 40L * 86_400_000;
        store.Save("cmd-done", "p", "t1", 0, old);
        store.Advance("cmd-done", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL, now: old);
        store.Advance("cmd-done", CommandState.SENT_TO_TERMINAL, CommandState.COMPLETED, now: old);
        store.Save("cmd-inflight", "p", "t1", 0, old);
        store.Advance("cmd-inflight", CommandState.RECEIVED, CommandState.UNKNOWN, now: old);

        Assert.Equal(1, store.Purge(Now - 30L * 86_400_000));
        Assert.Null(store.Read("cmd-done"));
        // UNKNOWN = sonuç belirsiz. Silmek, çözülmemiş bir tahsilatı kaybetmek olurdu.
        Assert.NotNull(store.Read("cmd-inflight"));
    }

    // ── RETENTION (7 gün) ───────────────────────────────────────────────────────

    [Fact]
    public void Purge_eski_kesin_kayitlari_siler_yenileri_BIRAKIR()
    {
        // Pencere iki yönlü bir kısıt: KISA olursa kasanın geç gelen tekrarı "yeni komut" sanılır ve
        // aynı tahsilat ikinci kez yapılır; SINIRSIZ olursa (bugüne kadarki hâli — `Purge` hiçbir
        // yerden çağrılmıyordu) 48 saat sonra yeniden kullanılan bir ServiceID eski kayda çarpar ve
        // satış hiç yapılmadan "saklanan sonuç" olarak replay edilir; kasa başarılı sanır, para
        // alınmaz. 7 gün ikisinin arasında.
        using var store = CommandStore.Open(TempDb());
        var sekizGunOnce = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeMilliseconds();
        var altiGunOnce  = DateTimeOffset.UtcNow.AddDays(-6).ToUnixTimeMilliseconds();

        foreach (var (id, pay, an) in new[] { ("eski", "pay-e", sekizGunOnce), ("yeni", "pay-y", altiGunOnce) })
        {
            store.Save(id, pay, "t1", Now + 60_000, an);
            Assert.True(store.Advance(id, CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL, now: an));
            Assert.True(store.Advance(id, CommandState.SENT_TO_TERMINAL, CommandState.COMPLETED, now: an));
        }

        var sinir = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
        var silinen = store.Purge(sinir);

        Assert.Equal(1, silinen);
        Assert.Null(store.Read("eski"));
        Assert.NotNull(store.Read("yeni"));   // ← ÇİVİ: 6 günlük kayıt SİLİNMEZ
    }

    [Fact]
    public void Purge_UCUSTAKI_kaydi_ASLA_silmez()
    {
        // ← ÇİVİ: `UNKNOWN` bir kaydı silmek, çözülmemiş bir tahsilatı kaybetmektir. Kayıt ne kadar
        // eski olursa olsun, akıbeti belirsizse durur.
        using var store = CommandStore.Open(TempDb());
        var cokEski = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeMilliseconds();

        store.Save("belirsiz", "pay-b", "t1", Now + 60_000, cokEski);
        store.Advance("belirsiz", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL, now: cokEski);
        store.Advance("belirsiz", CommandState.SENT_TO_TERMINAL, CommandState.UNKNOWN, now: cokEski);

        var silinen = store.Purge(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.Equal(0, silinen);
        Assert.NotNull(store.Read("belirsiz"));
    }

    [Fact]
    public void Kind_varsayilani_sale_ve_eski_kayitlar_kurtarmaya_girer()
    {
        // Şema geçişi "genişlet" adımı: sütun sonradan eklendi, varsayılanı 'sale'. Eski kayıtlar
        // bozulmadan satış sayılmaya devam etmeli — aksi hâlde yeniden başlatmada çözülmemiş
        // tahsilatlar kurtarma listesinden DÜŞERDİ.
        using var store = CommandStore.Open(TempDb());
        store.Save("satis", "pay-s", "t1", Now + 60_000);                              // kind yazılmadı
        store.Save("iptal", "pay-i", "t1", Now + 60_000, kind: CommandKinds.Void);

        var bekleyen = store.Pending();

        Assert.Contains(bekleyen, k => k.CommandId == "satis");
        Assert.DoesNotContain(bekleyen, k => k.CommandId == "iptal");
    }

    [Fact]
    public void ESKI_VERITABANINDA_parked_until_ve_fiscal_receipt_no_SUTUNLARI_EKLENIR()
    {
        // ← ÇİVİ: BAĞIMSIZ DENETİM BULDU. Şema geçişleri testsizdi ve testler HER ZAMAN taze
        // veritabanı açtığı için geçiş yolu hiç koşmuyordu — oysa SAHADAKİ veritabanı eski ve
        // üretimde koşan yol TAM OLARAK bu.
        //
        // Bozulsaydı: `PendingFinishes` SELECT'i her turda "no such column" atardı ve
        // `YarimFisleriTamamla`'daki try yalnız `FinishStuckTicket`'i sardığı için KURTARMA
        // TAMAMEN ÖLÜRDÜ — üzerinde para olan fişler sessizce açık kalırdı.
        var yol = TempDb();

        // ESKİ şema: iki sütun da YOK (W105/W38 dönemindeki hâl)
        using (var eski = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={yol}"))
        {
            eski.Open();
            using var c = eski.CreateCommand();
            c.CommandText = """
                CREATE TABLE pending_finish (
                    ticket_id TEXT PRIMARY KEY, terminal_id TEXT NOT NULL, sale_session_id TEXT,
                    marked_at INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at INTEGER NOT NULL, last_step TEXT, last_code TEXT);
                CREATE TABLE closed_tickets (
                    ticket_id TEXT PRIMARY KEY, terminal_id TEXT NOT NULL, sale_session_id TEXT,
                    total_minor INTEGER, paid_minor INTEGER, closed_at INTEGER NOT NULL,
                    notified_at INTEGER);
                INSERT INTO pending_finish VALUES ('tkt_eski','t1','o',1,0,1,NULL,NULL);
                """;
            c.ExecuteNonQuery();
        }

        using var store = CommandStore.Open(yol);

        // Geçiş koştu: eski KAYIT duruyor ve yeni sütunlar okunabiliyor.
        var satir = Assert.Single(store.PendingFinishes(long.MaxValue));
        Assert.Equal("tkt_eski", satir.TicketId);
        Assert.Null(satir.ParkedUntil);

        // Ve yeni sütunlar gerçekten YAZILABİLİR.
        store.DeferFinish("tkt_eski", TimeSpan.FromMinutes(30), 1_000);
        Assert.Equal(1_000 + 1_800_000, store.PendingFinishes(long.MaxValue).Single().ParkedUntil);

        store.MarkTicketClosed("tkt_kapali", "t1", "o", 990, 990);
        store.SetFiscalReceipt("tkt_kapali", new FiscalReceiptId(7, 93, 1));   // fırlatmamalı (W151: üç sütun)
        Assert.Equal(new FiscalReceiptId(7, 93, 1), store.ReadClosedTicket("tkt_kapali")!.Value.Row.FiscalReceipt);
    }

    [Fact]
    public void W145_SONRASI_ama_W151_ONCESI_veritabaninda_Z_ve_EKU_sutunlari_EKLENIR()
    {
        // ← ÇİVİ: sahadaki veritabanı 09-12'de fiscal_receipt_no'yu aldı ama Z/EKÜ'yü almadı.
        // Geçiş sütun sütun bakmalı; "fiscal_receipt_no var → geçiş bitti" sanmak, yeni SELECT'i
        // "no such column" ile düşürür ve yarım kalmış kapanışların kurtarılması ölürdü.
        var yol = TempDb();
        using (var eski = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={yol}"))
        {
            eski.Open();
            using var c = eski.CreateCommand();
            c.CommandText = """
                CREATE TABLE closed_tickets (
                    ticket_id TEXT PRIMARY KEY, terminal_id TEXT NOT NULL, sale_session_id TEXT,
                    total_minor INTEGER, paid_minor INTEGER, closed_at INTEGER NOT NULL,
                    notified_at INTEGER, fiscal_receipt_no INTEGER);
                INSERT INTO closed_tickets VALUES ('tkt_yarim','t1','o',990,990,1,NULL,5);
                """;
            c.ExecuteNonQuery();
        }

        using var store = CommandStore.Open(yol);

        // Eski kayıt duruyor; numarası var ama Z/EKÜ yok → kimlik YARIM → null (yarım kimlik taşınmaz).
        var satir = Assert.Single(store.PendingClosedTickets(long.MaxValue));
        Assert.Equal("tkt_yarim", satir.TicketId);
        Assert.Null(satir.FiscalReceipt);

        // Yeni sütunlar yazılıp okunabiliyor.
        store.SetFiscalReceipt("tkt_yarim", new FiscalReceiptId(5, 93, 1));
        Assert.Equal(new FiscalReceiptId(5, 93, 1), store.PendingClosedTickets(long.MaxValue).Single().FiscalReceipt);
    }

    // ── W11: KURTARMA YAŞ SINIRI ────────────────────────────────────────────────

    [Fact]
    public void Kurtarma_24_saatten_ESKI_UNKNOWN_kaydi_YOKLAMAZ()
    {
        // Ölçülmüş maliyet: çözülmüş ama UNKNOWN'da bırakılan kayıtlar HER açılışta yeniden
        // yoklanıyordu; 4 kayıt açılışı dakikalarca uzattı ve kasa o denemeleri bir daha
        // göndermezse bu maliyet SONSUZA KADAR sürecekti.
        // ← ÇİVİ: 25 saatlik yoklanmaz, 23 saatlik yoklanır. Kayıt SİLİNMEZ, yalnız atlanır.
        using var store = CommandStore.Open(TempDb());
        var yirmiUc = DateTimeOffset.UtcNow.AddHours(-23).ToUnixTimeMilliseconds();
        var yirmiBes = DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeMilliseconds();

        foreach (var (id, an) in new[] { ("taze", yirmiUc), ("bayat", yirmiBes) })
        {
            store.Save(id, "pay-" + id, "t1", Now + 60_000, an);
            store.Advance(id, CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL, now: an);
            store.Advance(id, CommandState.SENT_TO_TERMINAL, CommandState.UNKNOWN, now: an);
        }

        var esik = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();
        var bekleyen = store.Pending(unknownEsigi: esik);

        Assert.Contains(bekleyen, k => k.CommandId == "taze");
        Assert.DoesNotContain(bekleyen, k => k.CommandId == "bayat");
        Assert.Equal(1, store.CountSkippedUnknown(esik));
        Assert.NotNull(store.Read("bayat"));   // ← ÇİVİ: kayıt DURUYOR, silinmedi
    }

    [Fact]
    public void KAPATILMIS_kayit_yas_siniri_sayisina_GIRMEZ()
    {
        // ← ÇİVİ: bu sayı doğrudan bir günlük satırına dönüşüyor ("yaş sınırı — atlanan = N") ve o
        // satır "N kayıt SIRF YAŞI yüzünden yoklanmıyor" diye okunuyor. Kapatılmış (W34) kayıtlar
        // zaten hiçbir koşulda yoklanmayacak; onları saymak sayıyı ŞİŞİRİR.
        //
        // Sahada ölçüldü (2026-09-09 01:16): satır `atlanan = 6` diyordu ama yalnız 2 kayıt yaş
        // yüzünden atlanmıştı — kalan 4'ü kapatılmıştı. Yanındaki `Pending()` satırı "2 komut yarım
        // kalmış" diyordu; ikisi yan yana çelişkili görünüyor, oysa AYNI 2 kaydı anlatıyorlardı.
        using var store = CommandStore.Open(TempDb());
        var eski = DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeMilliseconds();

        foreach (var id in new[] { "acik", "kapali" })
        {
            store.Save(id, "pay-" + id, "t1", Now + 60_000, eski);
            store.Advance(id, CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL, now: eski);
            store.Advance(id, CommandState.SENT_TO_TERMINAL, CommandState.UNKNOWN, now: eski);
        }
        store.CloseRecovery("kapali");

        var esik = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();

        Assert.Equal(1, store.CountSkippedUnknown(esik));   // ← ÇİVİ: 2 değil, 1
        Assert.NotNull(store.Read("kapali"));               // kayıt DURUYOR, yalnız sayılmıyor
    }

    [Fact]
    public void Yas_siniri_YALNIZ_UNKNOWNa_uygulanir()
    {
        // `SENT_TO_TERMINAL` bir çökme penceresini gösterir ve nadirdir; yaşına bakmadan
        // yoklanması daha güvenli — orada para gerçekten uçuşta olabilir.
        using var store = CommandStore.Open(TempDb());
        var cokEski = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();

        store.Save("ucusta", "pay-u", "t1", Now + 60_000, cokEski);
        store.Advance("ucusta", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL, now: cokEski);

        var esik = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeMilliseconds();

        Assert.Contains(store.Pending(unknownEsigi: esik), k => k.CommandId == "ucusta");
    }
}
