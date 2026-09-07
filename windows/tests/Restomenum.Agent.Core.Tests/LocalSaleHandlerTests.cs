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
        public int? Rate;   // null = §30.12 doğrulaması atlanır (mevcut testler etkilenmez)
        public string? LastProductCode;
        public string? LastCategoryId;
        public DepartmentMatch? Resolve(string? productCode, string? categoryId)
        {
            LastProductCode = productCode;
            LastCategoryId = categoryId;
            return Dept is int d ? new DepartmentMatch(d, Rate) : (DepartmentMatch?)null;
        }
    }

    private sealed class FakePaymentMethods : IPaymentMethodResolver
    {
        public int? Type = GmpPaymentTypes.Card;   // varsayılan geçerli — mevcut testler etkilenmez
        public string? LastMethodId;
        public int? Resolve(string paymentMethodId) { LastMethodId = paymentMethodId; return Type; }
    }

    private sealed class FakeNotifier : IResultNotifier
    {
        public List<string> Bodies { get; } = new();
        public NotifyResult Result = new(NotifyOutcome.Recorded, "APPROVED", null, 200, "");
        public Task<NotifyResult> NotifyAsync(string p, string body, CancellationToken ct = default)
        { Bodies.Add(body); return Task.FromResult(Result); }
        public Task<NotifyResult> NotifyTicketCancelAsync(string body, CancellationToken ct = default)
        { Bodies.Add(body); return Task.FromResult(Result); }
    }

    private const string Pay = "pay_0123456789abcdef0123456789abcdef01234567";

    private static SaleToPoiRequest Req(string serviceId = "svc1") =>
        new(serviceId, "kasa-1", "term-01", Pay, "1042", DateTimeOffset.UtcNow);

    private static PaymentDetail Detail(long amount = 24000, string pmId = "11-cash") =>
        new(Pay, "1042", "TRY", 2, amount, amount, "TR", "ACCEPTED",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000, "fullSale",
            new List<SaleLine> { new(0, "p1", "Adana", 2, amount, "10", "c1", "l1") },
            pmId);

    private (LocalSaleHandler, SimulatorTransport, FakeNotifier) Kur(
        PaymentDetailResult amounts, int? dept = 3, TransportResult? terminal = null, int? rate = null,
        int? paymentType = GmpPaymentTypes.Card)
    {
        var sim = new SimulatorTransport();
        if (terminal is not null) sim.Expect(terminal);
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var notifier = new FakeNotifier();
        var h = new LocalSaleHandler(new FakeAmounts { Result = amounts }, orch, _store,
            new FakeResolver { Dept = dept, Rate = rate }, new FakePaymentMethods { Type = paymentType },
            notifier, _outbox, sim);
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

        var govde = await h.HandleAsync(Req());
        var resp = Resp(govde);
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        // ⚠️ Eskiden "Aborted"/"UnreachableHost" idi. Tutar alınamadığında `FP3_Payment` HİÇ
        // çağrılmaz, yani sonuç KESİNDİR; belirsiz sınıfına sokmak denemeyi gereksiz yere çözüm
        // döngüsünde bırakıyordu. Sebep artık makine-okunur alanda.
        Assert.Equal("PaymentRestriction", resp.GetProperty("ErrorCondition").GetString());
        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
        Assert.Equal("AMOUNT_FETCH_FAILED", ek.GetProperty("reason").GetString());
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
    public async Task Departman_KDVsi_TaxCode_ile_celisirse_terminale_GITMEZ_mali_sapma_onlenir()
    {
        // Detail() SaleLine TaxCode="10" (%10). Departman oranı 2000 (%20) → 10*100=1000 ≠ 2000 → ret.
        // Fişte %20, defterde %10 olur ve hiçbir kapı yakalamaz; §30.12 doğrulaması burada durdurur.
        var (h, sim, notifier) = Kur(new PaymentDetailResult.Ok(Detail()), dept: 0, rate: 2000);

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("PaymentRestriction", resp.GetProperty("ErrorCondition").GetString());
        Assert.Contains("PROVIDER_CONFIG_INCOMPLETE", resp.GetProperty("AdditionalResponse").GetString());
        Assert.Contains("p1", resp.GetProperty("AdditionalResponse").GetString());   // HANGİ ürün
        Assert.Empty(sim.SaleCalls);         // terminale GİTMEDİ (mali sapma önlendi)
        Assert.Single(notifier.Bodies);      // GET ACCEPTED'dı → takılı kalmasın diye bildir
    }

    [Fact]
    public async Task Departman_KDVsi_TaxCode_ile_uyusuyorsa_normal_gecer()
    {
        // TaxCode="10" ↔ departman oranı 1000 (%10): 10*100=1000 → uyumlu, yanlış-ret YOK, terminale gider.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()), dept: 10, rate: 1000,
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1", CardLast4: "4242"));

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Success", resp.GetProperty("Result").GetString());
        Assert.Single(sim.SaleCalls);        // doğrulama uyumluysa akış aynen sürer
    }

    [Fact]
    public async Task Eslenmemis_odeme_yontemi_terminale_GITMEZ_ama_bildirir_ve_yontemi_soyler()
    {
        // §20-I: PaymentMethodId payment-methods.json'da yoksa (resolver null) → terminale gitme.
        var (h, sim, notifier) = Kur(new PaymentDetailResult.Ok(Detail(pmId: "11-uydurma")), paymentType: null);

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("PaymentRestriction", resp.GetProperty("ErrorCondition").GetString());
        Assert.Contains("PAYMENT_METHOD_UNMAPPED", resp.GetProperty("AdditionalResponse").GetString());
        Assert.Contains("11-uydurma", resp.GetProperty("AdditionalResponse").GetString());   // HANGİ yöntem
        Assert.Empty(sim.SaleCalls);         // terminale GİTMEDİ
        Assert.Single(notifier.Bodies);      // GET ACCEPTED'dı → takılı kalmasın diye bildir
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

    // ── W5: AYNI ServiceID ile İKİNCİ POST (kasa köprüsünün "yeniden dene" düğmesi) ──────

    [Fact]
    public async Task AyniServiceId_ikinci_POST_ikinci_odeme_BASLATMAZ()
    {
        // Kasa köprüsü belirsiz denemede AYNI ServiceID ile zarfı yeniden POST ediyor ve panel bu
        // düğmeyi kasiyere gösteriyor. Tekilleme olmasaydı ikinci POST ikinci FP3_Payment başlatırdı
        // → müşteri iki kez öderdi.
        // ← ÇİVİ: ikinci POST terminale GİTMEZ; saklanan sonuç döner.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1"));

        var ilk = Resp(await h.HandleAsync(Req("svc-tekil")));
        var ikinci = Resp(await h.HandleAsync(Req("svc-tekil")));

        Assert.Equal("Success", ilk.GetProperty("Result").GetString());
        Assert.Single(sim.SaleCalls);   // ← ÇİVİ: ikinci satış çağrısı = çift tahsilat
        Assert.False(ikinci.TryGetProperty("ErrorCondition", out var ec) && ec.GetString() == "Refusal");
    }

    [Fact]
    public async Task AyniServiceId_ilk_islem_BELIRSIZKEN_ikinci_POST_yalniz_SORAR()
    {
        // En tehlikeli an: ilk işlem UNKNOWN'da asılı (para hareket etmiş OLABİLİR) ve kasiyer
        // "yeniden dene"ye basıyor. Burada ikinci FP3_Payment ASLA çağrılmaz — yalnız terminale
        // sorulur. Aksi hâlde çekilmiş bir kart ikinci kez çekilir.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Unknown, ProviderResultCode: "Payment:0x0826",
                ErrorCondition: "UnreachableHost"));
        sim.ProbeResult = new PaymentProbe(ProbeVerdict.Indeterminate, Note: "fiş okunamadı");

        var ilk = Resp(await h.HandleAsync(Req("svc-ucus")));
        var ikinci = Resp(await h.HandleAsync(Req("svc-ucus")));

        Assert.Equal("Failure", ilk.GetProperty("Result").GetString());
        Assert.Equal("Failure", ikinci.GetProperty("Result").GetString());
        Assert.Single(sim.SaleCalls);   // ← ÇİVİ: belirsizlik sürerken TEKRAR GÖNDERİM YOK
        Assert.True(sim.ProbeCalls >= 2);   // her POST yalnız SORDU
    }

    [Fact]
    public async Task Tekilleme_anahtari_ServiceId_FARKLI_ServiceId_yeni_islemdir()
    {
        // Anahtar ServiceID'dir (LocalSaleHandler: CommandId = req.ServiceId); SaleID ya da
        // PaymentID DEĞİL. Bu test o sınırı dürüstçe kayda geçirir: aynı ödeme için FARKLI
        // ServiceID ile gelen ikinci POST, tekilleme tarafından YAKALANMAZ ve yeni bir satıştır.
        // ⚠️ Yani çift-tahsilat koruması kasanın aynı ServiceID'yi kullanmasına BAĞLIDIR.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1"));
        sim.Expect(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN2"));

        await h.HandleAsync(Req("svc-A"));
        await h.HandleAsync(Req("svc-B"));

        Assert.Equal(2, sim.SaleCalls.Count);
    }
}
