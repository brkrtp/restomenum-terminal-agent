using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>W105 — CİHAZDA YARIM KALMIŞ FİŞİ OTOMATİK TAMAMLAMA TURU.</b>
///
/// <para>Var olma sebebi tek bir olay: 2026-09-09 23:07'de baskı patladı, fiş 990 kuruş gerçek
/// tahsilatla açık kaldı ve <b>83 dakika</b> öyle durdu — ajan bütün o süre boyunca sorunsuz
/// çalışıyordu, çünkü o fişi tekrar deneyecek hiçbir mekanizma yoktu.</para>
///
/// <para>Bu tur gözetimsiz çalışacak ve para taşıyan bir cihaza dokunacak; üç güvenlik şartının
/// her biri burada ayrı ayrı çivilenmiştir.</para>
/// </summary>
public class TicketSweepTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"sweep_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;
    private readonly Outbox _outbox;
    private readonly ClockOffset _clock = new();
    private readonly List<(string Mesaj, object? Veri)> _loglar = new();
    private long _saatMs = 1_700_000_000_000;

    public TicketSweepTests()
    {
        _store = CommandStore.Open(_db);
        _outbox = Outbox.Open(_db);
        _clock.Sync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    /// <summary>
    /// Tamamlama yeteneği olan sahte taşıma — kaç kez ve NASIL çağrıldığını sayar.
    /// <c>SimulatorTransport</c> <c>sealed</c> olduğu için türetilmiyor, SARMALANIYOR.
    /// </summary>
    private sealed class SahteTamamlayici : ITerminalTransport, IStuckTicketFinisher
    {
        private readonly SimulatorTransport _ic = new();
        public List<bool> Cagrilar { get; } = new();   // her çağrının `uygula` değeri
        public StuckTicketResult Sonuc = new(true, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            TicketFlags.TotalsAndPaymentsPrinted, ToplamMinor: 990, TahsilMinor: 990);

        public StuckTicketResult FinishStuckTicket(bool uygula)
        {
            Cagrilar.Add(uygula);
            return Sonuc;
        }

        public Task<TransportResult> SaleAsync(SaleRequest r, CancellationToken ct = default) => _ic.SaleAsync(r, ct);
        public Task<TicketState> ReadTicketAsync(CancellationToken ct = default) => _ic.ReadTicketAsync(ct);
        public Task<PaymentProbe> ProbeAsync(SaleRequest r, CancellationToken ct = default) => _ic.ProbeAsync(r, ct);
        public Task<bool> EchoAsync(CancellationToken ct = default) => _ic.EchoAsync(ct);
        public Task<TransportResult> VoidAsync(CancellationToken ct = default) => _ic.VoidAsync(ct);
        public Task<TicketVoidResult> VoidTicketAsync(string t, CancellationToken ct = default)
            => _ic.VoidTicketAsync(t, ct);
    }

    private (LocalSaleHandler, SahteTamamlayici) Kur()
    {
        var tasima = new SahteTamamlayici();
        var orch = new AgentOrchestrator(_store, tasima, _clock, RecoveryPolicy.Immediate);
        var h = new LocalSaleHandler(new SahteTutar(), orch, _store, new SahteBolum(),
            new SahteYontem(), new SahteBildirici(), _outbox, tasima,
            now: () => DateTimeOffset.FromUnixTimeMilliseconds(_saatMs),
            log: (m, o) => _loglar.Add((m, o)));
        return (h, tasima);
    }

    private object? LogVeri(string parca) => _loglar.LastOrDefault(l => l.Mesaj.Contains(parca)).Veri;
    private bool LogVar(string parca) => _loglar.Any(l => l.Mesaj.Contains(parca));
    private static T? Alan<T>(object? veri, string ad) =>
        veri is null ? default : (T?)veri.GetType().GetProperty(ad)?.GetValue(veri);

    // ── ZAMANLAMA: BEKLEME ŞARTI ────────────────────────────────────────────────

    [Fact]
    public async Task Isaretlenen_fise_HEMEN_gidilmez_once_beklenir()
    {
        var (h, tasima) = Kur();
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", "PrintBeforeMF", "0xF003",
            ilkBeklemeSn: 15, now: _saatMs);

        await h.DrainOutboxAsync();

        // ← ÇİVİ (şart ①): cihaz baskı adımından hemen sonra hâlâ meşgul olabilir; saniyesinde
        // üstüne gitmek aynı duvara toslamaktır. Fiş zaten açık ve bekliyor — acele kazanç yok.
        Assert.Empty(tasima.Cagrilar);
    }

    [Fact]
    public async Task Bekleme_dolunca_fis_TAMAMLANIR_ve_kayit_duser()
    {
        var (h, tasima) = Kur();
        _store.BindOpenTicket("t1", "oturum-A");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", "PrintBeforeMF", "0xF003",
            ilkBeklemeSn: 15, now: _saatMs);
        _saatMs += 16_000;

        await h.DrainOutboxAsync();

        Assert.Equal(new[] { true }, tasima.Cagrilar);   // ← KURU PROVA DEĞİL: gerçekten uygular
        Assert.Empty(_store.PendingFinishes(_saatMs));
        Assert.True(LogVar("yarım kalmış fiş TAMAMLANDI"));
    }

    [Fact]
    public async Task Zamani_gelmemis_kayit_TURU_TETIKLEMEZ()
    {
        var (h, tasima) = Kur();
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null,
            ilkBeklemeSn: 300, now: _saatMs);
        _saatMs += 60_000;   // 5 dakikanın dolmasına daha var

        await h.DrainOutboxAsync();

        // ← ÇİVİ: geri çekilme sorgunun İÇİNDE. Zamanı gelmemiş kaydı döndürseydik üstel
        // erteleme hiçbir işe yaramaz, her turda cihaza gidilirdi.
        Assert.Empty(tasima.Cagrilar);
    }

    [Fact]
    public async Task SATIS_SURERKEN_cihaza_GIDILMEZ_tur_atlanir()
    {
        var (h, tasima) = Kur();
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        // Cihaz kilidini bir SATIŞ tutuyormuş gibi al (bakım işleri aynı kilidi kullanır).
        using var satis = h.TryAcquire();
        Assert.NotNull(satis);

        await h.DrainOutboxAsync();

        // ← ÇİVİ (şart ①): SATIŞ HER ZAMAN ÖNCELİKLİ. Beklemek de yanlış olurdu — kuyruğa
        // girip kasiyerin ödemesini geciktirirdi. Atlamak güvenli: kayıt duruyor, tur 30 sn
        // sonra yine gelecek.
        Assert.Empty(tasima.Cagrilar);
        Assert.True(LogVar("cihaz satışta — tur atlandı"));
        Assert.Single(_store.PendingFinishes(_saatMs));   // kayıt DURUYOR, deneme SAYILMADI
    }

    // ── GERİ ÇEKİLME VE ALARM ───────────────────────────────────────────────────

    [Fact]
    public async Task Basarisiz_deneme_USTEL_olarak_ertelenir_ve_kayit_DURUR()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            TicketFlags.TotalsAndPaymentsPrinted, "PrintMF", "0xF003");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        await h.DrainOutboxAsync();

        // ← ÇİVİ: VAZGEÇME YOK, SİLME YOK. Bu fişte gerçek para var; ertelemek başka, unutmak başka.
        var kalan = Assert.Single(_store.PendingFinishes(_saatMs + 10_000_000));
        Assert.Equal(1, kalan.Attempts);
        Assert.Equal("PrintMF", kalan.LastStep);
        // Bir sonraki deneme ERTELENDİ: aynı anda tekrar sorgulanınca listede YOK.
        Assert.Empty(_store.PendingFinishes(_saatMs));
    }

    [Fact]
    public async Task Esik_asilinca_ALARM_yukselir()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintMF", "0xF003");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        // Beş tur: her turda zamanı ilerlet ki geri çekilme dolsun.
        for (var i = 0; i < 5; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }

        // ← ÇİVİ: sessizce sonsuza kadar denemek, arızayı gizlemek olurdu. Ses yükselir —
        // ama kayıt yine DURUR (vazgeçme eşiği değil, alarm eşiği).
        Assert.True(LogVar("denemedir kapanmıyor"));
        Assert.Single(_store.PendingFinishes(_saatMs));
    }

    [Fact]
    public async Task Cihaz_MESGULSE_alarm_yukselmez_yalnizca_ertelenir()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, Meshgul: true,
            new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "DEVICE_BUSY", 0), 0, "Start", "0xF01C");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        for (var i = 0; i < 6; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }

        // ← ÇİVİ: "cevap gelmedi" ile "denendi ve olmadı" AYRI olgular. İlkini alarma çevirmek,
        // hiç yapılmamış bir denemeyi başarısızlık diye raporlamak olurdu.
        Assert.True(LogVar("cihaz meşgul/cevapsız"));
        Assert.False(LogVar("denemedir kapanmıyor"));
        Assert.Single(_store.PendingFinishes(_saatMs));
    }

    [Fact]
    public async Task Odemesi_EKSIK_fis_alarm_degil_INSAN_KARARI_olarak_bildirilir()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.Dokunma, TicketFinish.OdemeTamamlanmamis, 7), 0);
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        await h.DrainOutboxAsync();

        // ← ÇİVİ (şart ③): kalanı tahsil etmek KASANIN kararı, bir bakım turunun değil.
        // Otomatik yol burada durur ve durduğunu söyler.
        Assert.True(LogVar("TAMAMLANAMAZ — dokunulmadı, insan kararı"));
        Assert.Equal(7, Alan<int>(LogVeri("TAMAMLANAMAZ"), "ureticiAdimi"));
    }

    [Fact]
    public async Task Cihazda_fis_KALMAMISSA_kayit_duser_ama_KAPANDI_denmez()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, FisAcikDegil: true, false,
            new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "TICKET_NOT_OPEN", 0), 0);
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        await h.DrainOutboxAsync();

        // ← ÇİVİ: fiş başka bir yoldan kapanmış olabilir (kasiyer, önceki tur). Kaydı düşürmek
        // doğru; "kapandı" diye deftere yazmak ise ÖLÇMEDİĞİMİZ bir kapanışı iddia etmek olurdu.
        Assert.Empty(_store.PendingFinishes(_saatMs));
        Assert.False(LogVar("TAMAMLANDI"));
        Assert.Empty(_store.PendingClosedTickets());
    }

    // ── İKİNCİ İŞARETLEME ───────────────────────────────────────────────────────

    [Fact]
    public void Ikinci_isaretleme_deneme_sayacini_SIFIRLAMAZ()
    {
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", "PrintMF", "0xF003", 0, _saatMs);
        _store.MarkFinishAttempt("tkt_1", "PrintMF", "0xF003", _saatMs);
        _store.MarkFinishAttempt("tkt_1", "PrintMF", "0xF003", _saatMs);

        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", "PrintBeforeMF", "0xF004", 0, _saatMs);

        // ← ÇİVİ: sayaç sıfırlansaydı, tekrar tekrar patlayan bir fişte geri çekilme hiç
        // büyümez ve cihaza sonsuza kadar 15 saniyede bir gidilirdi.
        var r = Assert.Single(_store.PendingFinishes(_saatMs + 10_000_000));
        Assert.Equal(2, r.Attempts);
        Assert.Equal("PrintBeforeMF", r.LastStep);   // hata alanları YİNE DE tazelenir
    }

    // ── SAHTELER (yalnız derlenmek için; bu turda kullanılmıyorlar) ──────────────

    private sealed class SahteTutar : IPaymentDetailClient
    {
        public Task<PaymentDetailResult> FetchAsync(string paymentId, CancellationToken ct = default)
            => Task.FromResult<PaymentDetailResult>(
                new PaymentDetailResult.Rejected(PaymentRejectReason.NotFound, "yok", 404));
    }

    private sealed class SahteBolum : ILineDepartmentResolver
    {
        public DepartmentMatch? Resolve(string? productCode, string? categoryId) => new(1, 2000);
    }

    private sealed class SahteYontem : IPaymentMethodResolver
    {
        public int? Resolve(string paymentMethodId) => GmpPaymentTypes.Cash;
    }

    private sealed class SahteBildirici : IResultNotifier
    {
        private static NotifyResult Ok() => new(NotifyOutcome.Recorded, "COMPLETED", null, 200, "ok");
        public Task<NotifyResult> NotifyAsync(string paymentId, string body, CancellationToken ct = default)
            => Task.FromResult(Ok());
        public Task<NotifyResult> NotifyTicketCancelAsync(string body, CancellationToken ct = default)
            => Task.FromResult(Ok());
        public Task<NotifyResult> NotifyTicketClosedAsync(string body, CancellationToken ct = default)
            => Task.FromResult(Ok());
    }
}
