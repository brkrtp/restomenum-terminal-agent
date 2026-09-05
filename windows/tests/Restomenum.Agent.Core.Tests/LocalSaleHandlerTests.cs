using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Yerel ödeme akışı — kasadan SaleToPOI → GET tutar → departman → orkestratör → ÖNCE bildir SONRA dön.
/// Karar mantığı (dedupe/durum/UNKNOWN) <see cref="AgentOrchestrator"/>'da; burada AKIŞ çivilenir.
/// </summary>
public class LocalSaleHandlerTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"lsh_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;
    private readonly Outbox _outbox;
    private readonly ClockOffset _clock = new();

    public LocalSaleHandlerTests()
    {
        _store = CommandStore.Open(_db);
        _outbox = Outbox.Open(_db);
        _clock.Sync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());   // saat senkron (offset≈0)
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    // ── Fakes ──
    private sealed class FakeAmounts : IPaymentDetailClient
    {
        public required PaymentDetailResult Result;
        public Task<PaymentDetailResult> FetchAsync(string p, CancellationToken ct = default) => Task.FromResult(Result);
    }

    private sealed class FakeResolver : ILineDepartmentResolver
    {
        public int? Dept = 3;
        public int? Resolve(string? categoryId) => Dept;
    }

    private sealed class FakeNotifier : IResultNotifier
    {
        public List<string> Bodies { get; } = new();
        public NotifyResult Result = new(NotifyOutcome.Recorded, "APPROVED", null, 200, "");
        public Task<NotifyResult> NotifyAsync(string p, string body, CancellationToken ct = default)
        { Bodies.Add(body); return Task.FromResult(Result); }
    }

    private const string Pay = "pay_0123456789abcdef0123456789abcdef01234567";

    private static SaleToPoiRequest Req(string serviceId = "svc1") =>
        new(serviceId, "kasa-1", "term-01", Pay, "1042", DateTimeOffset.UtcNow);

    private static PaymentDetail Detail(long amount = 24000) =>
        new(Pay, "1042", "TRY", 2, amount, amount, "TR", "ACCEPTED",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000, "fullSale",
            new List<SaleLine> { new(0, "p1", "Adana", 2, amount, "10", "c1", "l1") });

    private (LocalSaleHandler, SimulatorTransport, FakeNotifier) Kur(
        PaymentDetailResult amounts, int? dept = 3, TransportResult? terminal = null)
    {
        var sim = new SimulatorTransport();
        if (terminal is not null) sim.Expect(terminal);
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var notifier = new FakeNotifier();
        var h = new LocalSaleHandler(new FakeAmounts { Result = amounts }, orch, _store, new FakeResolver { Dept = dept }, notifier, _outbox);
        return (h, sim, notifier);
    }

    private static JsonElement Resp(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("SaleToPOIResponse").GetProperty("PaymentResponse").GetProperty("Response");

    [Fact]
    public async Task Onaylanan_odeme_Success_doner_ve_platforma_bildirilir()
    {
        var (h, sim, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1", CardLast4: "4242"));

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Success", resp.GetProperty("Result").GetString());
        Assert.Single(sim.SaleCalls);        // terminale bir kez gitti
        Assert.Single(notifier.Bodies);      // platforma bildirildi
    }

    [Fact]
    public async Task GET_reddi_terminale_GITMEZ_ve_bildirmez()
    {
        var (h, sim, notifier) = Kur(
            new PaymentDetailResult.Rejected(PaymentRejectReason.AmountWindowClosed, "plugin.payment.amountWindowClosed", 409));

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("Aborted", resp.GetProperty("ErrorCondition").GetString());
        Assert.Empty(sim.SaleCalls);         // terminale GİTMEDİ (tutar bilinmiyor)
        Assert.Empty(notifier.Bodies);       // platform GET reddini zaten biliyor
    }

    [Fact]
    public async Task Eslenmemis_urun_terminale_GITMEZ_ama_bildirir_ve_urunu_soyler()
    {
        var (h, sim, notifier) = Kur(new PaymentDetailResult.Ok(Detail()), dept: null);

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("PaymentRestriction", resp.GetProperty("ErrorCondition").GetString());
        Assert.Contains("PRODUCT_UNMAPPED", resp.GetProperty("AdditionalResponse").GetString());
        Assert.Contains("p1", resp.GetProperty("AdditionalResponse").GetString());   // HANGİ ürün
        Assert.Empty(sim.SaleCalls);
        Assert.Single(notifier.Bodies);      // GET başarılıydı (ACCEPTED) → takılı kalmasın diye bildir
    }

    [Fact]
    public async Task Ayni_ServiceID_ikinci_istek_karti_TEKRAR_CEKMEZ()
    {
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000));

        await h.HandleAsync(Req("dup1"));
        await h.HandleAsync(Req("dup1"));    // kasa ağ-hatası retry'ı: AYNI ServiceID
        Assert.Single(sim.SaleCalls);        // ← ÇİVİ: terminal YALNIZ bir kez sürüldü
    }
}
