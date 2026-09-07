using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Fiş/ödeme iptali (W2) — kasadaki "Fiş İptal" düğmesinin ucu.
///
/// <para>Bu yolun var olma sebebi ölçüldü: 2026-09-07'de başarısız bir kart denemesinin bıraktığı
/// açık fiş 14 saat boyunca her satışı bloke etti ve kasiyerin onu temizleyecek hiçbir yolu yoktu —
/// terminal elle kurcalanmak zorunda kalındı.</para>
/// </summary>
public class ReversalTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rev_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;
    private readonly Outbox _outbox;
    private readonly ClockOffset _clock = new();

    public ReversalTests()
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

    private const string Pay = "pay_0123456789abcdef0123456789abcdef01234567";

    private static string Zarf(string? origServiceId = "svc-orig", string? poiTxId = null,
        string category = "Reversal", string paymentId = Pay) =>
        $$"""
        {
          "SaleToPOIRequest": {
            "MessageHeader": {
              "MessageClass": "Service", "MessageCategory": "{{category}}", "MessageType": "Request",
              "ServiceID": "void12345", "SaleID": "kasa-1", "POIID": "term-01"
            },
            "ReversalRequest": {
              "ReversalReason": "MerchantCancel",
              {{(poiTxId is null ? "" : $"\"OriginalPOITransaction\": {{ \"POITransactionID\": \"{poiTxId}\" }},")}}
              {{(origServiceId is null ? "" : $"\"MessageReference\": {{ \"MessageCategory\": \"Payment\", \"ServiceID\": \"{origServiceId}\" }},")}}
              "SaleData": { "SaleTransactionID": { "TransactionID": "{{paymentId}}" } }
            }
          }
        }
        """;

    // ── AYRIŞTIRMA ──────────────────────────────────────────────────────────────

    [Fact]
    public void Acik_fis_iptali_ayristirilir()
    {
        var r = Assert.IsType<ReversalParseResult.Ok>(ReversalRequestParser.Parse(Zarf()));
        Assert.Equal("void12345", r.Request.ServiceId);
        Assert.Equal("svc-orig", r.Request.OriginalServiceId);
        Assert.Null(r.Request.OriginalPoiTransactionId);   // ← vaka: ödemesiz açık fiş
        Assert.Equal("MerchantCancel", r.Request.ReversalReason);
    }

    [Fact]
    public void Tamamlanmis_odeme_iptali_POITransactionID_ile_ayristirilir()
    {
        var r = Assert.IsType<ReversalParseResult.Ok>(
            ReversalRequestParser.Parse(Zarf(origServiceId: null, poiTxId: "POI-77")));
        Assert.Equal("POI-77", r.Request.OriginalPoiTransactionId);
    }

    [Fact]
    public void REFERANSSIZ_iptal_REDDEDILIR()
    {
        // ← ÇİVİ: referanssız iptal "cihazda ne varsa iptal et" demektir. Yanlış fişi iptal etmek
        // geri alınamaz; başka bir kasanın açık fişini silebilirdik.
        var r = Assert.IsType<ReversalParseResult.Invalid>(
            ReversalRequestParser.Parse(Zarf(origServiceId: null)));
        Assert.Equal(SaleToPoiRejectReason.Malformed, r.Reason);
    }

    [Fact]
    public void PeekCategory_yolu_secer()
    {
        Assert.Equal("Reversal", ReversalRequestParser.PeekCategory(Zarf()));
        Assert.Null(ReversalRequestParser.PeekCategory("bu json degil"));
    }

    // ── AKIŞ ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Defterde_olmayan_referans_TERMINALE_GITMEDEN_NotFound()
    {
        // ← ÇİVİ: tanımadığımız bir referans için cihaza dokunmayız.
        var (h, sim) = Kur();
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var resp = Yanit(await h.HandleReversalAsync(req));

        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("NotFound", resp.GetProperty("ErrorCondition").GetString());
        Assert.Equal(0, sim.VoidCalls);   // terminale HİÇ gidilmedi
    }

    [Fact]
    public async Task Odemesiz_acik_fis_iptal_edilir_TICKET_CANCELLED()
    {
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var govde = await h.HandleReversalAsync(req);
        var resp = Yanit(govde);

        Assert.Equal("Success", resp.GetProperty("Result").GetString());
        Assert.Equal(1, sim.VoidCalls);
        Assert.Equal(RestomenumReasons.TicketCancelled, Ek(govde).GetProperty("info").GetString());
        // Ödemesiz fişte iade TUTARI bildirilmez — olmayan bir iadeyi deftere yazdırırdı.
        Assert.False(Gövde(govde).TryGetProperty("ReversedAmount", out _));
    }

    [Fact]
    public async Task Yarim_kalan_iptal_VOID_INCOMPLETE_ve_TEKRAR_DENE_DEMEZ()
    {
        // ← ÇİVİ: yarım kalmış bir ters işlemin üstüne ikincisini bindirmek, geri alınmış bir
        // ödemeyi ikinci kez geri almaya çalışmaktır. Sonuç "olmadı" değil "BELİRSİZ".
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.VoidResult = new TransportResult(TransportOutcome.Unknown,
            ProviderResultCode: "REVERSAL_FAILED:0x0826", ErrorCondition: "InProgress",
            Reason: RestomenumReasons.VoidIncomplete);
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var govde = await h.HandleReversalAsync(req);
        var resp = Yanit(govde);

        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("InProgress", resp.GetProperty("ErrorCondition").GetString());
        Assert.Equal(RestomenumReasons.VoidIncomplete, Ek(govde).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Iptalde_paymentInvoked_HER_ZAMAN_false()
    {
        // İptal yolunda `FP3_Payment` çağrılmaz. Alan yine de ATLANMAZ.
        var (h, _) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var ek = Ek(await h.HandleReversalAsync(req));

        Assert.Equal(1, ek.GetProperty("v").GetInt32());
        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
    }

    // ── W9: TÜR AYRIMI (kind) ───────────────────────────────────────────────────

    [Fact]
    public async Task Iptal_kaydi_ACILIS_KURTARMASINA_GIRMEZ()
    {
        // ← ÇİVİ: iptal kayıtları satışla aynı tabloda yaşıyor. Tür ayrımı olmasaydı açılış
        // kurtarması bir iptali "yarım kalmış satış" sanıp terminale "bu ödeme işlendi mi" diye
        // sorardı — olmayan bir ödemeyi kovalamak.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        await h.HandleReversalAsync(req);

        var bekleyen = _store.Pending();
        Assert.DoesNotContain(bekleyen, k => k.CommandId == "void12345");
    }

    [Fact]
    public async Task Ayni_iptal_ikinci_kez_gelirse_CIHAZA_GITMEZ()
    {
        // Kasa ağ hatasında aynı zarfı yeniden POST edebiliyor. İkinci `VoidAll` ya boşa çalışır
        // ya da ARAYA GİREN YENİ bir fişi iptal ederdi — ikincisi gerçek zarar.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var ilk = await h.HandleReversalAsync(req);
        var ikinci = await h.HandleReversalAsync(req);

        Assert.Equal(1, sim.VoidCalls);   // ← ÇİVİ
        Assert.Equal(ilk, ikinci);        // saklanan sonuç birebir replay edildi
    }

    [Fact]
    public async Task Yarim_kalan_iptal_UNKNOWNda_BIRAKILIR_tekrar_sorulabilsin()
    {
        // Belirsiz sonucu "kesin" diye saklamak, tekrar geldiğinde cihaza sormayı engellerdi.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.VoidResult = new TransportResult(TransportOutcome.Unknown,
            ProviderResultCode: "REVERSAL_FAILED", ErrorCondition: "InProgress",
            Reason: RestomenumReasons.VoidIncomplete);
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        await h.HandleReversalAsync(req);
        await h.HandleReversalAsync(req);

        Assert.Equal(2, sim.VoidCalls);   // belirsizlik sürüyor → yeniden soruldu
    }

    // ── yardımcılar ─────────────────────────────────────────────────────────────

    private sealed class FakeAmounts : IPaymentDetailClient
    {
        public Task<PaymentDetailResult> FetchAsync(string p, CancellationToken ct = default) =>
            Task.FromResult<PaymentDetailResult>(new PaymentDetailResult.Rejected(
                PaymentRejectReason.NotFound, "yok", 404));
    }

    private sealed class FakeResolver : ILineDepartmentResolver
    {
        public DepartmentMatch? Resolve(string? productCode, string? categoryId) => new(3, null);
    }

    private sealed class FakePaymentMethods : IPaymentMethodResolver
    {
        public int? Resolve(string paymentMethodId) => GmpPaymentTypes.Cash;
    }

    private sealed class FakeNotifier : IResultNotifier
    {
        public Task<NotifyResult> NotifyAsync(string p, string body, CancellationToken ct = default) =>
            Task.FromResult(new NotifyResult(NotifyOutcome.Recorded, "OK", null, 200, ""));
    }

    private (LocalSaleHandler, SimulatorTransport) Kur()
    {
        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var h = new LocalSaleHandler(new FakeAmounts(), orch, _store, new FakeResolver(),
            new FakePaymentMethods(), new FakeNotifier(), _outbox, sim);
        return (h, sim);
    }

    private static JsonElement Gövde(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("SaleToPOIResponse").GetProperty("ReversalResponse");

    private static JsonElement Yanit(string json) => Gövde(json).GetProperty("Response");

    private static JsonElement Ek(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
}
