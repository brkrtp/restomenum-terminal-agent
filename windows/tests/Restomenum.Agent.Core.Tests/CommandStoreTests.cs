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
}
