using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <c>SaleToPOIRequest</c> ayrıştırma/doğrulama — kurallar backend'in (üretici) garantisi.
/// Tutar bu zarfta YOK; ServiceID dedupe anahtarı; kategori yalnız Payment; bilinmeyen alan yok sayılır.
/// </summary>
public class SaleToPoiRequestParserTests
{
    private const string Gecerli =
        """
        {"SaleToPOIRequest":{"MessageHeader":{"ProtocolVersion":"3.0","MessageClass":"Service","MessageCategory":"Payment","MessageType":"Request","ServiceID":"a1b2c3d4e5","SaleID":"kasa-3","POIID":"term-01"},"PaymentRequest":{"SaleData":{"SaleTransactionID":{"TransactionID":"pay_0123456789abcdef0123456789abcdef01234567","TimeStamp":"2026-09-05T15:40:12Z"},"SaleReferenceID":"1042"}}}}
        """;

    [Fact]
    public void Gecerli_istek_tam_ayristirilir()
    {
        var ok = Assert.IsType<SaleToPoiParseResult.Ok>(SaleToPoiRequestParser.Parse(Gecerli));
        var r = ok.Request;
        Assert.Equal("a1b2c3d4e5", r.ServiceId);
        Assert.Equal("kasa-3", r.SaleId);
        Assert.Equal("term-01", r.PoiId);
        Assert.Equal("pay_0123456789abcdef0123456789abcdef01234567", r.PaymentId);
        Assert.Equal("1042", r.SaleReferenceId);
        Assert.Equal(2026, r.TimeStamp.Year);
        Assert.Equal(TimeSpan.Zero, r.TimeStamp.Offset);   // UTC
    }

    [Fact]
    public void Bilinmeyen_alanlar_yok_sayilir()
    {
        // İleri-uyum: fazladan alan eski ajanı KIRMAMALI.
        var body = Gecerli.Replace("\"SaleID\":\"kasa-3\"", "\"SaleID\":\"kasa-3\",\"YeniAlan\":{\"x\":1}");
        Assert.IsType<SaleToPoiParseResult.Ok>(SaleToPoiRequestParser.Parse(body));
    }

    [Fact]
    public void Bos_SaleID_kabul()
    {
        var body = Gecerli.Replace("\"SaleID\":\"kasa-3\"", "\"SaleID\":\"\"");
        var ok = Assert.IsType<SaleToPoiParseResult.Ok>(SaleToPoiRequestParser.Parse(body));
        Assert.Equal("", ok.Request.SaleId);
    }

    [Fact]
    public void Tanimsiz_kategori_CAPABILITY_reddi()
    {
        var body = Gecerli.Replace("\"MessageCategory\":\"Payment\"", "\"MessageCategory\":\"Reversal\"");
        var inv = Assert.IsType<SaleToPoiParseResult.Invalid>(SaleToPoiRequestParser.Parse(body));
        Assert.Equal(SaleToPoiRejectReason.UnsupportedCategory, inv.Reason);
    }

    [Fact]
    public void PaymentTransaction_varsa_reddedilir()
    {
        // Tutar bu zarfta taşınamaz → sahte/bozuk.
        var body = Gecerli.Replace(
            "\"SaleData\":{",
            "\"PaymentTransaction\":{\"AmountsReq\":{\"RequestedAmount\":240}},\"SaleData\":{");
        var inv = Assert.IsType<SaleToPoiParseResult.Invalid>(SaleToPoiRequestParser.Parse(body));
        Assert.Equal(SaleToPoiRejectReason.AmountNotAllowed, inv.Reason);
    }

    [Theory]
    [InlineData("pay_kisahex")]                                            // çok kısa
    [InlineData("0123456789abcdef0123456789abcdef01234567")]               // pay_ öneki yok
    [InlineData("pay_0123456789abcdef0123456789abcdef0123456G")]           // hex olmayan karakter
    public void Gecersiz_paymentId_reddedilir(string kotu)
    {
        var body = Gecerli.Replace("pay_0123456789abcdef0123456789abcdef01234567", kotu);
        var inv = Assert.IsType<SaleToPoiParseResult.Invalid>(SaleToPoiRequestParser.Parse(body));
        Assert.Equal(SaleToPoiRejectReason.InvalidPaymentId, inv.Reason);
    }

    [Theory]
    [InlineData("\"ServiceID\":\"\"")]                    // boş
    [InlineData("\"ServiceID\":\"onbirkarakter\"")]       // >10
    public void Gecersiz_ServiceID_reddedilir(string degisim)
    {
        var body = Gecerli.Replace("\"ServiceID\":\"a1b2c3d4e5\"", degisim);
        var inv = Assert.IsType<SaleToPoiParseResult.Invalid>(SaleToPoiRequestParser.Parse(body));
        Assert.Equal(SaleToPoiRejectReason.InvalidServiceId, inv.Reason);
    }

    [Theory]
    [InlineData("<html/>")]                                                          // JSON değil
    [InlineData("{\"foo\":1}")]                                                       // zarf yok
    [InlineData("{\"SaleToPOIRequest\":{\"MessageHeader\":{\"MessageCategory\":\"Payment\"}}}")]  // eksik
    public void Bozuk_govde_Malformed(string body)
    {
        var inv = Assert.IsType<SaleToPoiParseResult.Invalid>(SaleToPoiRequestParser.Parse(body));
        Assert.Equal(SaleToPoiRejectReason.Malformed, inv.Reason);
    }

    [Fact]
    public void Bozuk_TimeStamp_Malformed()
    {
        var body = Gecerli.Replace("\"TimeStamp\":\"2026-09-05T15:40:12Z\"", "\"TimeStamp\":\"dün\"");
        var inv = Assert.IsType<SaleToPoiParseResult.Invalid>(SaleToPoiRequestParser.Parse(body));
        Assert.Equal(SaleToPoiRejectReason.Malformed, inv.Reason);
    }
}
