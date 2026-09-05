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

    private static JsonElement Build(TransportResult r)
    {
        var json = SaleToPoiResponseBuilder.BuildResult(Req, r, DateTimeOffset.UtcNow);
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
    public void Declined_kesin_ret_tutarsiz()
    {
        var pr = Build(new TransportResult(TransportOutcome.Declined, ProviderResultCode: "51"))
            .GetProperty("PaymentResponse");
        Assert.Equal("Failure", pr.GetProperty("Response").GetProperty("Result").GetString());
        Assert.Equal("Refusal", pr.GetProperty("Response").GetProperty("ErrorCondition").GetString());
        Assert.False(pr.GetProperty("PaymentResult").TryGetProperty("AmountsResp", out _));  // tutar bildirilmez
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
        Assert.Equal((false, "Refusal"), SaleToPoiResponseBuilder.MapOutcome(TransportOutcome.Declined));
        Assert.Equal((false, "Busy"), SaleToPoiResponseBuilder.MapOutcome(TransportOutcome.Busy));
        Assert.Equal((false, "InProgress"), SaleToPoiResponseBuilder.MapOutcome(TransportOutcome.Unknown));
    }
}
