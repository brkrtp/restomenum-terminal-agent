using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <c>GET /plugin-api/payments/{id}</c> ayrıştırması — peer'in dev'de ÖLÇTÜĞÜ gerçek gövdelerle
/// (e2e 33/33, b7a416387). nexo gövdesi <c>data</c> içinde; tutar decimal→kuruş; SaleItem yalnız TR.
/// </summary>
public class PaymentDetailParserTests
{
    // Peer'in birebir yolladığı 200 gövdesi (paymentId ellipsis'i geçerli bir id ile değiştirildi).
    private const string TrBody =
        """
        {"success":true,"data":{"SaleData":{"SaleReferenceID":"9001"},"PaymentTransaction":{"AmountsReq":{"Currency":"TRY","RequestedAmount":240},"SaleItem":[{"ItemID":0,"ProductCode":"e2e-prod-1","ProductLabel":"E2E Adana","Quantity":2,"UnitPrice":120,"ItemAmount":240,"TaxCode":"10","RestomenumExt":{"CategoryId":"e2e-cat","LineId":"l1"}}]},"RestomenumExt":{"PaymentId":"pay_f462abcd","State":"ACCEPTED","Market":"TR","ExpiresAt":1788626365044,"Exponent":2,"ItemsScope":"fullSale","SaleTotalAmount":240}}}
        """;

    [Fact]
    public void TR_govdesi_tam_ayristirilir()
    {
        var r = Assert.IsType<PaymentDetailResult.Ok>(PaymentDetailParser.Parse(200, TrBody));
        var d = r.Detail;
        Assert.Equal("pay_f462abcd", d.PaymentId);
        Assert.Equal("9001", d.SaleReferenceId);
        Assert.Equal("TRY", d.Currency);
        Assert.Equal(24000, d.RequestedAmountMinor);      // 240 → 24000 kuruş
        Assert.Equal(24000, d.SaleTotalAmountMinor);
        Assert.Equal("TR", d.Market);
        Assert.Equal(2, d.Exponent);
        Assert.Equal("ACCEPTED", d.State);
        Assert.Equal(1788626365044, d.ExpiresAtMs);
        Assert.Equal("fullSale", d.ItemsScope);

        var item = Assert.Single(d.Items);
        Assert.Equal(0, item.ItemId);
        Assert.Equal("e2e-prod-1", item.ProductCode);
        Assert.Equal("E2E Adana", item.ProductLabel);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(24000, item.ItemAmountMinor);        // ItemAmount OTORİTE (UnitPrice×Qty değil)
        Assert.Equal("10", item.TaxCode);
        Assert.Equal("e2e-cat", item.CategoryId);
        Assert.Equal("l1", item.LineId);
    }

    [Fact]
    public void Ondalik_tutar_kurusa_yuvarlanir()
    {
        var body = TrBody.Replace("\"RequestedAmount\":240", "\"RequestedAmount\":99.99");
        var r = Assert.IsType<PaymentDetailResult.Ok>(PaymentDetailParser.Parse(200, body));
        Assert.Equal(9999, r.Detail.RequestedAmountMinor);   // 99.99 → 9999, kesme YOK
    }

    [Fact]
    public void EU_SaleItem_yoksa_liste_bos()
    {
        // Avrupa: SaleItem alanı HİÇ yok (boş dizi değil).
        var body =
            """
            {"success":true,"data":{"SaleData":{"SaleReferenceID":"55"},"PaymentTransaction":{"AmountsReq":{"Currency":"EUR","RequestedAmount":12.5}},"RestomenumExt":{"PaymentId":"pay_eu","State":"ACCEPTED","Market":"EU","ExpiresAt":1788626365044,"Exponent":2,"ItemsScope":"fullSale","SaleTotalAmount":12.5}}}
            """;
        var r = Assert.IsType<PaymentDetailResult.Ok>(PaymentDetailParser.Parse(200, body));
        Assert.Empty(r.Detail.Items);
        Assert.Equal(1250, r.Detail.RequestedAmountMinor);
        Assert.Equal("EUR", r.Detail.Currency);
    }

    [Fact]
    public void Exponent0_para_birimi_carpani_100_DEGIL()
    {
        // Kuruşsuz para birimi (exp=0): 240 → 240 minor, 24000 DEĞİL. Sabit-100 hatasının çivisi.
        var body =
            """
            {"success":true,"data":{"SaleData":{"SaleReferenceID":"77"},"PaymentTransaction":{"AmountsReq":{"Currency":"XYZ","RequestedAmount":240}},"RestomenumExt":{"PaymentId":"pay_z0","State":"ACCEPTED","Market":"EU","ExpiresAt":1788626365044,"Exponent":0,"ItemsScope":"fullSale","SaleTotalAmount":240}}}
            """;
        var r = Assert.IsType<PaymentDetailResult.Ok>(PaymentDetailParser.Parse(200, body));
        Assert.Equal(0, r.Detail.Exponent);
        Assert.Equal(240, r.Detail.RequestedAmountMinor);   // exp=0 → çarpan 1
    }

    [Theory]
    [InlineData(401, "plugin.connector.unauthorized", PaymentRejectReason.Unauthorized)]
    [InlineData(404, "plugin.payment.notFound", PaymentRejectReason.NotFound)]
    [InlineData(409, "plugin.payment.expired", PaymentRejectReason.Expired)]
    [InlineData(409, "plugin.payment.notActionable", PaymentRejectReason.NotActionable)]
    [InlineData(409, "plugin.payment.amountWindowClosed", PaymentRejectReason.AmountWindowClosed)]
    [InlineData(409, "plugin.payment.saleItemsUnavailable", PaymentRejectReason.SaleItemsUnavailable)]
    [InlineData(429, "plugin.rateLimited", PaymentRejectReason.RateLimited)]
    public void Hatalar_dogru_sebebe_eslenir(int status, string message, PaymentRejectReason beklenen)
    {
        var body = $$"""{"success":false,"message":"{{message}}","status":{{status}}}""";
        var r = Assert.IsType<PaymentDetailResult.Rejected>(PaymentDetailParser.Parse(status, body));
        Assert.Equal(beklenen, r.Reason);
        Assert.Equal(message, r.Message);
    }

    [Fact]
    public void Beklenmedik_200_sekli_Unknown_reddi()
    {
        // 200 + success:true ama data şekli bozuk → sessizce SÜRME, Unknown.
        var r = Assert.IsType<PaymentDetailResult.Rejected>(
            PaymentDetailParser.Parse(200, """{"success":true,"data":{"foo":1}}"""));
        Assert.Equal(PaymentRejectReason.Unknown, r.Reason);
    }

    [Fact]
    public void JSON_olmayan_govde_Unknown()
    {
        var r = Assert.IsType<PaymentDetailResult.Rejected>(PaymentDetailParser.Parse(502, "<html>bad gateway</html>"));
        Assert.Equal(PaymentRejectReason.Unknown, r.Reason);
    }
}
