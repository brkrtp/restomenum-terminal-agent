using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <c>SaleToPOIResponse</c> üretimi — tek kanonik gövde (kasa+platform). Güvenlik değişmezi:
/// para hareket etmiş OLABİLECEK sonuç ASLA kesin-ret olarak bildirilmez.
/// </summary>
public class SaleToPoiResponseBuilderTests
{
    private static readonly SaleToPoiRequest Req = new(
        ServiceId: "svc12345", SaleId: "kasa-1", PoiId: "term-01",
        PaymentId: "pay_0123456789abcdef0123456789abcdef01234567",
        SaleReferenceId: "1042", TimeStamp: DateTimeOffset.UtcNow);

    private static JsonElement Build(TransportResult r, int exponent = 2)
    {
        var json = SaleToPoiResponseBuilder.BuildResult(Req, r, exponent, DateTimeOffset.UtcNow);
        return JsonDocument.Parse(json).RootElement.GetProperty("SaleToPOIResponse");
    }

    [Fact]
    public void Approved_tam_govde()
    {
        var root = Build(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000,
            Rrn: "RRN1", ApprovalCode: "AUTH9", CardLast4: "1234", Scheme: "VISA", ProviderResultCode: "00"));

        var hdr = root.GetProperty("MessageHeader");
        Assert.Equal("Response", hdr.GetProperty("MessageType").GetString());
        Assert.Equal("svc12345", hdr.GetProperty("ServiceID").GetString());   // istekten yankı
        Assert.Equal("term-01", hdr.GetProperty("POIID").GetString());

        var pr = root.GetProperty("PaymentResponse");
        Assert.Equal(Req.PaymentId,
            pr.GetProperty("SaleData").GetProperty("SaleTransactionID").GetProperty("TransactionID").GetString());

        var resp = pr.GetProperty("Response");
        Assert.Equal("Success", resp.GetProperty("Result").GetString());
        Assert.Equal(JsonValueKind.Null, resp.GetProperty("ErrorCondition").ValueKind);
        Assert.Equal("00", resp.GetProperty("AdditionalResponse").GetString());

        var res = pr.GetProperty("PaymentResult");
        Assert.Equal(240m, res.GetProperty("AmountsResp").GetProperty("AuthorizedAmount").GetDecimal());  // 24000→240
        var acq = res.GetProperty("PaymentAcquirerData");
        Assert.Equal("AUTH9", acq.GetProperty("ApprovalCode").GetString());
        Assert.Equal("RRN1", acq.GetProperty("AcquirerTransactionID").GetProperty("TransactionID").GetString());
        var card = res.GetProperty("PaymentInstrumentData").GetProperty("CardData");
        Assert.Equal("1234", card.GetProperty("MaskedPan").GetString());    // ≤4 hane — ham PAN YOK
        Assert.Equal("VISA", card.GetProperty("PaymentBrand").GetString());
    }

    [Fact]
    public void W104_baski_patladiginda_govde_SEBEBI_ve_ADIMI_tasir()
    {
        // Saha (2026-09-09 23:07): `PrintBeforeMF` zaman aşımı; fiş 990 kuruş tahsilatla açık
        // kaldı. Kasanın gördüğü gövde `Result:"Success"` + `reason:null` idi ve arıza ancak
        // "kalan 0 ama fiş açık" ÇELİŞKİSİNDEN çıkarılabiliyordu.
        var root = Build(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 990,
            ProviderResultCode: "0x0000", TicketState: "OPEN",
            Reason: RestomenumReasons.PrintTimeout,
            PrintFailedStep: "PrintBeforeMF", PrintFailedCode: "0xF003"));

        var resp = root.GetProperty("PaymentResponse").GetProperty("Response");
        // ← ÇİVİ: sonuç `Success` KALIR — para ALINDI. Sebep taşımak "reddedildi" demek değildir.
        Assert.Equal("Success", resp.GetProperty("Result").GetString());

        var ek = root.GetProperty("Restomenum");
        Assert.Equal("PRINT_TIMEOUT", ek.GetProperty("reason").GetString());
        // ← ÇİVİ: kasiyere gösterilecek cümleyi kuran iki alan. Yoklarsa kasa yalnız "bir şey
        // oldu" der; hangi adımda kalındığını ve cihazın ham kodunu söyleyemez.
        Assert.Equal("PrintBeforeMF", ek.GetProperty("printFailedStep").GetString());
        Assert.Equal("0xF003", ek.GetProperty("printFailedCode").GetString());
        Assert.Equal("OPEN", ek.GetProperty("ticketState").GetString());
    }

    [Fact]
    public void W104_baski_sorunsuzsa_govdede_baski_alanlari_YOKTUR()
    {
        var root = Build(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 990,
            ProviderResultCode: "0x0000", TicketState: "CLOSED"));

        var ek = root.GetProperty("Restomenum");
        // ← ÇİVİ: her gövdeye boş alan koymak, alanı "bazen anlamlı bazen gürültü" bir veriye
        // çevirirdi. Alanın YOKLUĞU "sorun yok" demektir ve bu da bir beyandır.
        Assert.False(ek.TryGetProperty("printFailedStep", out _));
        Assert.False(ek.TryGetProperty("printFailedCode", out _));
        Assert.False(ek.TryGetProperty("reason", out _));
    }

    [Fact]
    public void Declined_kosuluyla_bildirilir_tutarsiz()
    {
        // Koşul artık kaynağında belirlenip gövdeye TAŞINIR; kovadan türetilmez.
        var pr = Build(new TransportResult(TransportOutcome.Declined,
                ProviderResultCode: "PRODUCT_UNMAPPED:x", ErrorCondition: "PaymentRestriction"))
            .GetProperty("PaymentResponse");
        Assert.Equal("Failure", pr.GetProperty("Response").GetProperty("Result").GetString());
        Assert.Equal("PaymentRestriction", pr.GetProperty("Response").GetProperty("ErrorCondition").GetString());
        Assert.False(pr.GetProperty("PaymentResult").TryGetProperty("AmountsResp", out _));  // tutar bildirilmez
    }

    [Theory]
    [InlineData(0L)]     // sayaç arttı ama tutar 0 (canlı bulunan kusur): Success DEME
    [InlineData(null)]   // Approved ama tutar hiç yok: yine Success DEME
    public void Approved_ama_tutar_yoksa_veya_sifirsa_Success_DEGIL(long? amount)
    {
        // §30.5 değişmezi: Result:"Success" = para hareket etti → AuthorizedAmount zorunlu+pozitif.
        var pr = Build(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: amount,
            ProviderResultCode: "someRc")).GetProperty("PaymentResponse");
        Assert.Equal("Failure", pr.GetProperty("Response").GetProperty("Result").GetString());   // Success DEĞİL
        Assert.NotEqual("Refusal", pr.GetProperty("Response").GetProperty("ErrorCondition").GetString());  // kesin-ret DEĞİL
        Assert.Contains("AUTHORIZED_AMOUNT_INVALID", pr.GetProperty("Response").GetProperty("AdditionalResponse").GetString());
        Assert.False(pr.GetProperty("PaymentResult").TryGetProperty("AmountsResp", out _));  // sahte tutar bildirilmez
    }

    [Theory]
    [InlineData(TransportOutcome.Busy, "Busy")]
    [InlineData(TransportOutcome.Unknown, "InProgress")]
    [InlineData(TransportOutcome.TicketAlreadyOpen, "InProgress")]
    public void Belirsiz_sonuclar_kesin_ret_DEGIL(TransportOutcome o, string beklenenEC)
    {
        var resp = Build(new TransportResult(o)).GetProperty("PaymentResponse").GetProperty("Response");
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        // Refusal/Cancel/… kesin listede DEĞİL → platform 'unknown'a düşürür (ikinci çekim engellenir).
        Assert.Equal(beklenenEC, resp.GetProperty("ErrorCondition").GetString());
        Assert.NotEqual("Refusal", resp.GetProperty("ErrorCondition").GetString());
    }

    [Fact]
    public void Progress_top_level_EventNotification_platforma()
    {
        var json = SaleToPoiResponseBuilder.BuildProgress(Req, ProgressEvent.WaitingForCard, DateTimeOffset.UtcNow);
        var root = JsonDocument.Parse(json).RootElement;
        var evt = root.GetProperty("EventNotification");
        Assert.Equal("WaitingForCard", evt.GetProperty("EventToNotify").GetString());
        Assert.Equal(Req.PaymentId,
            evt.GetProperty("SaleData").GetProperty("SaleTransactionID").GetProperty("TransactionID").GetString());
        // top-level: SaleToPOIResponse/SaleToPOIRequest sarmalı YOK.
        Assert.False(root.TryGetProperty("SaleToPOIResponse", out _));
    }

    [Fact]
    public void MapOutcome_yalniz_Declined_kesin_ret()
    {
        Assert.Equal((true, (string?)null), SaleToPoiResponseBuilder.MapOutcome(TransportOutcome.Approved));
        // ⚠️ Eskiden `Declined => "Refusal"` idi. Kova bazında kesin-ret üretmek, terminale hiç
        // gidilmemiş bir reddi bile kasiyere "kart reddedildi, başka kart isteyin" diye gösteriyordu
        // (2026-09-06 21:33 UTC'de ölçüldü). Kesin-ret artık yalnız açık issuer yanıtıyla üretilir;
        // koşulu `TransportResult.ErrorCondition` taşır, bu kova eşlemesi yalnız güvenli yedektir.
        Assert.Equal((false, "InProgress"), SaleToPoiResponseBuilder.MapOutcome(TransportOutcome.Declined));
        Assert.Equal((false, "Busy"), SaleToPoiResponseBuilder.MapOutcome(TransportOutcome.Busy));
        Assert.Equal((false, "InProgress"), SaleToPoiResponseBuilder.MapOutcome(TransportOutcome.Unknown));
    }
}
