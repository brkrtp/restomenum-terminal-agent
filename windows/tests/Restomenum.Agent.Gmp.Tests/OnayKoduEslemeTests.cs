using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Restomenum.Agent.Core;
using Restomenum.Agent.Gmp;
using Restomenum.Agent.Gmp.Interop;
using Xunit;

namespace Restomenum.Agent.Gmp.Tests;

/// <summary>
/// <b>RM-0396 — cihazın fiş yanıtından onay kodu, rrn ve kart son 4 hanesinin okunması.</b>
///
/// <para>⚠️ Buradaki JSON <b>uydurma değil</b>: 2026-10-14 16:18:42'de (mali fiş 32, Z 94, Garanti
/// BBVA kart ödemesi) cihazın DLL izinden birebir alındı. Yalnız gerçek değerler TEST SABİTLERİYLE
/// değiştirildi: onay kodu <c>TST123</c>, rrn <c>TEST00000001</c>, PAN <c>999999******8012</c>
/// (cihazın maskeli biçimi), kart sahibi/işyeri/terminal kimlikleri <c>TEST</c>, stan/batch sabit.
/// Varyantlar bu gövdeden türetilir — sahte bir gövdeye karşı test yazmak alan adı kusurlarını
/// gizlerdi (bkz. EchoEslemeTests).</para>
/// </summary>
public class OnayKoduEslemeTests
{
    /// <summary>Cihazın 16:18:42 yanıtı (ST_TICKET), test sabitleriyle arındırılmış.</summary>
    private const string Fis32 = """
{"TransactionFlags":131074,"OptionFlags":7,"ZNo":94,"FNo":32,"EJNo":1,"TotalReceiptAmount":1881,"TotalReceiptTax":86,"TotalReceiptDiscount":99,"TotalReceiptIncrement":0,"CashBackAmount":0,"TotalReceiptItemCancel":0,"TotalReceiptPayment":1881,"TotalReceiptReversedPayment":0,"KasaAvansAmount":0,"KasaPaymentAmount":0,"invoiceAmount":0,"invoiceAmountCurrency":0,"KatkiPayiAmount":0,"TaxFreeRefund":0,"TaxFreeCalculated":0,"szTicketDate":"261014","szTicketTime":"161653","SourceVasAppID":0,"PaymentVasAppID":62,"BankVasAppID":0,"ticketType":1,"totalNumberOfItems":2,"numberOfItemsInThis":0,"totalNumberOfPayments":1,"numberOfPaymentsInThis":1,"numberOfLoyaltyInThis":0,"TckNo":"","invoiceNo":"","invoiceType":0,"invoiceDate":0,"totalNumberOfPrinterLines":5,"numberOfPrinterLinesInThis":3,"uniqueId":"AAAAAAAAAAAAAAAA9arPal4AIAABAAAA","rawData":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","rawDataLen":0,"UserData":"dGVzdGRhdGEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==","CurrencyProfileIndex":255,"LastPaymentErrorCode":"","LastPaymentErrorMsg":"","BankPaymentUniqueId":"","InvoiceETTN":"","GiderPusulasiBelgeSeri":"","GiderPusulasiBelgeSira":"","SaleInfo":null,"stPayment":[{"flags":0,"dateOfPayment":1791994655,"typeOfPayment":4,"subtypeOfPayment":1,"orgAmount":1881,"orgAmountCurrencyCode":949,"payAmount":1881,"payAmountCurrencyCode":949,"cashBackAmountInTL":0,"cashBackAmountInDoviz":0,"paymentName":"","paymentInfo":"","tipAmount":0,"tipItemIndex":0,"stBankPayment":{"batchNo":1,"stan":111,"balance":0,"bankBkmId":62,"numberOfdiscount":0,"numberOfbonus":0,"authorizeCode":"TST123","transFlag":"AUg=","terminalId":"TEST","rrn":"TEST00000001","referenceCodeOfTransaction":"","merchantId":"TEST","bankName":"GARANTÝ BBVA","numberOfInstallments":0,"numberOfsubPayment":1,"numberOferrorMessage":0,"LoyaltyCustomerId":"","PaymentProvisionId":"","LoyaltyServiceId":0,"BankPaymentUniqueId":"","stBankSubPaymentInfo":[{"type":1,"amount":99,"name":"ÝÞYERÝ ÝND."}],"stCard":{"inputType":3,"pan":"999999******8012","holderName":"","type":"AGIB"},"stPaymentErrMessage":{"ErrorCode":"","ErrorMsg":"","AppErrorCode":"","AppErrorMsg":""},"stCombinedPayment":{"Type":0,"SubType":0,"countOfSubPayment":0,"Amount":0,"szTransCode":""}}}],"stTaxDetails":[{"u32VAT":0,"u32Amount":0,"u16VATPercentage":2000},{"u32VAT":0,"u32Amount":940,"u16VATPercentage":0},{"u32VAT":86,"u32Amount":941,"u16VATPercentage":1000},{"u32VAT":0,"u32Amount":0,"u16VATPercentage":100},{"u32VAT":0,"u32Amount":0,"u16VATPercentage":2400},{"u32VAT":0,"u32Amount":0,"u16VATPercentage":0},{"u32VAT":0,"u32Amount":0,"u16VATPercentage":0},{"u32VAT":0,"u32Amount":0,"u16VATPercentage":0}],"stPrinterCopy":[{"Flag":196610,"lineLen":32,"line":"ARATOPLAM                *19,80 "},{"Flag":196610,"lineLen":12,"line":"GARANTÝ BBVA"},{"Flag":196610,"lineLen":32,"line":"ÝÞYERÝ ÝND.               *0,99-"},null,null],"stLoyaltyService":null}
""";

    private static JObject Govde() => JObject.Parse(Fis32);
    private static GmpTicket Oku(JObject j) => GmpWrapper.Map(j.ToObject<ST_TICKET>()!);
    private static JObject KartOdemesi(JObject j) => (JObject)Enumerable.Last((JArray)j["stPayment"]!, x => x.Type != JTokenType.Null);

    private static JObject Nakit(JObject kart, long tutar)
    {
        var n = (JObject)kart.DeepClone();
        n["typeOfPayment"] = 1;
        n["payAmount"] = tutar;
        n["stBankPayment"] = null;
        return n;
    }

    private static void Odemeler(JObject j, int toplam, int buYanitta, params JToken[] kayitlar)
    {
        j["totalNumberOfPayments"] = toplam;
        j["numberOfPaymentsInThis"] = buYanitta;
        j["stPayment"] = new JArray(kayitlar);
    }

    [Fact]
    public void Tek_kart_odemesi_onay_kodu_rrn_ve_son4_DOLU()
    {
        var t = Oku(Govde());
        Assert.Equal(4, t.LastPaymentType);
        Assert.Equal("TST123", t.ApprovalCode);
        Assert.Equal("TEST00000001", t.Rrn);
        Assert.Equal("8012", t.CardLast4);
    }

    [Fact]
    public void Kart_ve_SONDA_nakit_varsa_onay_kodu_ve_rrn_NULL()
    {
        // Son dolu kayıt nakit: banka kaydı yok → kart bacağının kodu taşınmaz (rrn/last4 ile aynı kural).
        var j = Govde();
        var kart = KartOdemesi(j);
        Odemeler(j, 2, 2, kart.DeepClone(), Nakit(kart, 100));
        var t = Oku(j);
        Assert.Equal(1, t.LastPaymentType);
        Assert.Null(t.ApprovalCode);
        Assert.Null(t.Rrn);
    }

    [Fact]
    public void SONDAKI_pencere_bicimi_ilk_kayitlar_bos_son_dolu_kartin_kodu_okunur()
    {
        // Ölçülen biçim (2026-09-07): dizi count uzunluğunda, yalnız sondaki `dolu` kayıt dolu.
        var j = Govde();
        var kart = KartOdemesi(j);
        var bos = new JObject { ["typeOfPayment"] = 0, ["payAmount"] = 0 };
        Odemeler(j, 3, 1, bos, bos.DeepClone(), kart.DeepClone());
        var t = Oku(j);
        Assert.Equal("TST123", t.ApprovalCode);
        Assert.Equal("8012", t.CardLast4);
    }

    [Fact]
    public void Farkli_kodlu_iki_banka_satirinda_SON_kaydin_kodu_rrn_ve_son4_ayni_kayittan()
    {
        var j = Govde();
        var a = (JObject)KartOdemesi(j).DeepClone();
        var b = (JObject)a.DeepClone();
        a["stBankPayment"]!["authorizeCode"] = "AAA111";
        a["stBankPayment"]!["rrn"] = "TEST0000000A";
        a["stBankPayment"]!["stCard"]!["pan"] = "999999******1111";
        b["stBankPayment"]!["authorizeCode"] = "BBB222";
        b["stBankPayment"]!["rrn"] = "TEST0000000B";
        b["stBankPayment"]!["stCard"]!["pan"] = "999999******2222";
        Odemeler(j, 2, 2, a, b);
        var t = Oku(j);
        Assert.Equal("BBB222", t.ApprovalCode);
        Assert.Equal("TEST0000000B", t.Rrn);
        Assert.Equal("2222", t.CardLast4);
        // Satır listesi her kaydın kendi kodunu taşır (W79 kararı için).
        Assert.Equal(new[] { "AAA111", "BBB222" }, t.Payments!.Select(p => p.AuthorizeCode).ToArray());
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("  TST123 ", "TST123")]
    public void Bos_null_ve_bosluklu_onay_kodu_tek_bicimde_okunur(string? gelen, string? beklenen)
    {
        var j = Govde();
        var bp = KartOdemesi(j)["stBankPayment"]!;
        bp["authorizeCode"] = gelen is null ? JValue.CreateNull() : gelen;
        bp["rrn"] = "  TEST00000001 ";
        var t = Oku(j);
        Assert.Equal(beklenen, t.ApprovalCode);
        Assert.Equal("TEST00000001", t.Rrn);   // rrn de aynı yardımcıyla kırpılır
        Assert.Equal(beklenen, t.Payments!.Last().AuthorizeCode);
    }
}
