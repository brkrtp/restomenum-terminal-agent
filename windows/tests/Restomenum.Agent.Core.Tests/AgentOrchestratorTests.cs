using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Orkestratör testleri — <b>hepsi paranın hareket ettiği anları</b> hedefler.
///
/// Bu dosyadaki her test, gerçekten yaşanmış ya da yaşanabilecek bir çift-tahsilat/kayıp senaryosunun
/// çivisidir. Sahadan gelen ölçümlerle (GMPDLL_2026_04_17_103039.TXT) hizalıdır: özellikle
/// <see cref="Busy_ParaHareketEtmisOlabilir_TerminaleSorulur"/> ve
/// <see cref="Timeout_KismiOdeme_CiftTahsilatUretmez"/> doğrudan ölçülmüş vakaların karşılığıdır.
/// </summary>
public class AgentOrchestratorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"orch_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;
    private readonly ClockOffset _clock;

    public AgentOrchestratorTests()
    {
        _store = CommandStore.Open(_dbPath);
        _clock = new ClockOffset();
        _clock.Sync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { /* geçici dosya */ }
    }

    private AgentOrchestrator Orch(ITerminalTransport t) =>
        new(_store, t, _clock, RecoveryPolicy.Immediate);

    private static SaleRequest Req(string id = "cmd1") =>
        new(CommandId: id, PaymentId: "pay1", TerminalId: "t1", AmountMinor: 24000,
            Currency: "TRY", Exponent: 2, ProviderPluginId: "prov");

    private long Gelecek => _clock.ServerNow() + 60_000;

    // ────────────────────────────────────────────────────────────────────────
    // MUTLU YOL
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Onay_KesinSonucOlarakYazilir()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(
            TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1", CardLast4: "4242"));

        var sonuc = await Orch(sim).HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.Approved, sonuc.Decision);
        Assert.Equal(CommandState.COMPLETED, sonuc.State);
        Assert.Equal("RRN1", _store.Read("cmd1")!.TerminalReference);
    }

    [Fact]
    public async Task Red_KesinSonuctur_BelirsizlikDegil()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(TransportOutcome.Declined));

        var sonuc = await Orch(sim).HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.Declined, sonuc.Decision);
        Assert.Equal(CommandState.COMPLETED, sonuc.State);
        // Red kesin sonuçtur: terminale SORULMAZ, boşuna tur atılmaz.
        Assert.Equal(0, sim.ReadTicketCalls);
    }

    // ────────────────────────────────────────────────────────────────────────
    // ÇİFT TAHSİLATIN ÖNLENMESİ — bu dosyanın var oluş sebebi
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AyniKomutIkiKez_TerminaleSADECEBirKezGider()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(
            TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1"));
        var orch = Orch(sim);

        await orch.HandleAsync(Req(), Gelecek);
        var ikinci = await orch.HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.Replayed, ikinci.Decision);
        Assert.Single(sim.SaleCalls);   // ← ÇİVİ: ikinci satış çağrısı = çift tahsilat
    }

    [Fact]
    public async Task Busy_ParaHareketEtmisOlabilir_TerminaleSorulur()
    {
        // SAHA VAKASI: RECV_BUSY, kart ödemesi BAŞARIYLA tamamlandıktan sonra geldi.
        // "Meşgul = güvenli tekrar" varsayımı burada çift tahsilat üretirdi.
        var sim = new SimulatorTransport()
            .Expect(new TransportResult(TransportOutcome.Busy))
            .WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 24000,
                PaidAmountMinor: 24000, Rrn: "RRN-GERCEK"));

        var sonuc = await Orch(sim).HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.Approved, sonuc.Decision);   // para gerçekten hareket etmişti
        Assert.Single(sim.SaleCalls);                           // ← ÇİVİ: tekrar GÖNDERİLMEDİ
        Assert.Equal(1, sim.ReadTicketCalls);                   // varsaymak yerine soruldu
    }

    [Fact]
    public async Task Unknown_AslaYenidenGonderilmez()
    {
        var sim = new SimulatorTransport()
            .Expect(new TransportResult(TransportOutcome.Unknown))
            .WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 24000,
                PaidAmountMinor: 24000, Rrn: "RRN-X"));

        var sonuc = await Orch(sim).HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.Approved, sonuc.Decision);
        Assert.Single(sim.SaleCalls);   // ← §12.2/6: UNKNOWN sonrası SALE tekrarı YASAK
    }

    [Fact]
    public async Task Timeout_KismiOdeme_CiftTahsilatUretmez()
    {
        // SAHA VAKASI 1: fiş 3000, tahsil 1000 (kart). Kalan ikinci bir ödemeyle kapandı.
        // Agent kendi başına ikinci SALE göndermemeli — o mükerrer tahsilat olurdu.
        var sim = new SimulatorTransport()
            .Expect(new TransportResult(TransportOutcome.Unknown))
            .WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 3000, PaidAmountMinor: 1000));

        var sonuc = await Orch(sim).HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.Unresolved, sonuc.Decision);
        Assert.Single(sim.SaleCalls);
        Assert.Contains("1000/3000", sonuc.Note);   // tahsil edilen tutar operatöre bildirilir
    }

    [Fact]
    public async Task AcikFisYok_GuvenliTekrarDOGRULANIR_varsayilmaz()
    {
        var sim = new SimulatorTransport()
            .Expect(new TransportResult(TransportOutcome.Busy))
            .WithTicket(new TicketState(HasOpenTicket: false, TotalAmountMinor: 0, PaidAmountMinor: 0));

        var sonuc = await Orch(sim).HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.RetryLater, sonuc.Decision);
        Assert.Equal(1, sim.ReadTicketCalls);   // "güvenli" sonucu bile SORARAK elde edildi
    }

    // ────────────────────────────────────────────────────────────────────────
    // GERİ ÇEKİLME — sahada ölçülen "ilk sorgu HER ZAMAN meşgul" deseni
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IlkSorgularMesgul_GeriCekilerekTekrarSorulur()
    {
        var sim = new SimulatorTransport()
            .Expect(new TransportResult(TransportOutcome.Unknown))
            .WithBusyReads(2)   // sahada ilk sorgu hep RECV_BUSY aldı
            .WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 24000,
                PaidAmountMinor: 24000, Rrn: "RRN-Z"));

        var sonuc = await Orch(sim).HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.Approved, sonuc.Decision);
        Assert.Equal(3, sim.ReadTicketCalls);   // 2 meşgul + 1 başarılı
        Assert.Single(sim.SaleCalls);           // meşguliyet boyunca ASLA tekrar gönderilmedi
    }

    [Fact]
    public async Task SorguButcesiTukenirse_TahminEdilmez_InsanaGider()
    {
        var sim = new SimulatorTransport()
            .Expect(new TransportResult(TransportOutcome.Unknown))
            .WithBusyReads(99);   // terminal hiç cevap vermiyor

        var sonuc = await Orch(sim).HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.Unresolved, sonuc.Decision);
        Assert.Single(sim.SaleCalls);
    }

    [Fact]
    public async Task TerminaleUlasilamiyor_Unresolved_SaleTekrarlanmaz()
    {
        var sim = new SimulatorTransport()
            .Expect(new TransportResult(TransportOutcome.Unknown))
            .WithUnreachableReads();

        var sonuc = await Orch(sim).HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.Unresolved, sonuc.Decision);
        Assert.Single(sim.SaleCalls);
    }

    [Fact]
    public void GeriCekilme_OlculenDegerlerdenSapmaz()
    {
        var p = new RecoveryPolicy();
        // İlk sorgu ~30 sn geciktirilir: sahada 25.3–26.3 sn meşguliyet ölçüldü.
        Assert.Equal(TimeSpan.FromSeconds(30), p.DelayFor(0));
        Assert.Equal(TimeSpan.FromSeconds(5), p.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(10), p.DelayFor(2));
        Assert.Equal(p.MaxDelay, p.DelayFor(9));   // üstel büyüme sınırlanır
    }

    // ────────────────────────────────────────────────────────────────────────
    // SAAT & SÜRE — §5.3
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaatSenkronuYok_KomutCALISTIRILMAZ()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(TransportOutcome.Approved));
        var senkronsuz = new AgentOrchestrator(_store, sim, new ClockOffset(), RecoveryPolicy.Immediate);

        var sonuc = await senkronsuz.HandleAsync(Req(), Gelecek);

        Assert.Equal(AgentDecision.ClockUnsynced, sonuc.Decision);
        Assert.Empty(sim.SaleCalls);   // ← ÇİVİ: tahmin edilen saatle kart çekilmez
    }

    [Fact]
    public async Task SuresiGecmis_TerminaleHicGonderilmez()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(TransportOutcome.Approved));

        var sonuc = await Orch(sim).HandleAsync(Req(), _clock.ServerNow() - 1);

        Assert.Equal(AgentDecision.Expired, sonuc.Decision);
        Assert.Empty(sim.SaleCalls);
    }

    [Fact]
    public async Task SuresiGecmisKomut_TekrarGelirseYineGonderilmez()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(TransportOutcome.Approved));
        var orch = Orch(sim);

        await orch.HandleAsync(Req(), _clock.ServerNow() - 1);
        var ikinci = await orch.HandleAsync(Req(), _clock.ServerNow() - 1);

        Assert.Equal(AgentDecision.Expired, ikinci.Decision);
        Assert.Empty(sim.SaleCalls);
    }

    // ────────────────────────────────────────────────────────────────────────
    // EŞZAMANLILIK — oturum devrinde iki soket aynı komutu alabilir
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EszamanliOnCagri_TerminaleSADECEBirSatisGider()
    {
        var sim = new SimulatorTransport();
        sim.Expect(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1"));
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 24000, PaidAmountMinor: 24000, Rrn: "RRN1"));
        var orch = Orch(sim);

        var hepsi = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => orch.HandleAsync(Req(), Gelecek))));

        // ← ÇİVİ: dedupe atomik değilse burada 10 satış gider = 10 kez kart çekilir.
        Assert.Single(sim.SaleCalls);
        Assert.All(hepsi, h => Assert.NotEqual(AgentDecision.Declined, h.Decision));
    }

    // ────────────────────────────────────────────────────────────────────────
    // GÜVENLİK — §12.3: kart verisi saklanmaz
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaklananSonuc_KartVerisiTASIMAZ()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(
            TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1", CardLast4: "4242"));

        await Orch(sim).HandleAsync(Req(), Gelecek);
        var json = _store.Read("cmd1")!.ResultJson!;

        Assert.Contains("4242", json);              // son 4 hane saklanabilir
        Assert.DoesNotContain("pan", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("track", json, StringComparison.OrdinalIgnoreCase);
    }
}
