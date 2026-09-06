using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

public class DeviceMappingTests
{
    private const string FullBody = """
    {
      "version": 42,
      "updatedAt": "2026-09-06T00:00:00Z",
      "departments": [
        { "index": 0, "name": "YEMEK", "taxRateBasisPoints": 2000 },
        { "index": 10, "name": "SICAK KAHVE", "taxRateBasisPoints": 1000 }
      ],
      "entries": [
        { "kind": "product", "productCode": "1043-1601", "departmentIndex": 10 },
        { "kind": "category", "categoryId": "26-86", "departmentIndex": 0 }
      ],
      "paymentMethods": { "11-cash": 1, "11-credit": 4, "11-voucher": 16 }
    }
    """;

    private static DeviceMapping ParseOk(string body)
    {
        var r = DeviceMappingParser.Parse(body);
        return Assert.IsType<DeviceMappingParseResult.Ok>(r).Mapping;
    }

    [Fact]
    public void Parse_tam_govde()
    {
        var m = ParseOk(FullBody);
        Assert.Equal(42, m.Version);
        Assert.Equal(2, m.Departments.Count);
        Assert.Equal(1000, m.Departments.Single(d => d.Index == 10).TaxRateBasisPoints);
        Assert.Equal(2, m.Entries.Count);
        Assert.Equal(3, m.PaymentMethods.Count);
        Assert.Equal(1, m.PaymentMethods["11-cash"]);
        Assert.Equal(4, m.PaymentMethods["11-credit"]);
    }

    [Fact]
    public void Parse_bozuk_JSON_Invalid()
    {
        Assert.IsType<DeviceMappingParseResult.Invalid>(DeviceMappingParser.Parse("{ bozuk"));
    }

    [Fact]
    public void Parse_version_yoksa_sifir()
    {
        var m = ParseOk("""{ "paymentMethods": { "11-cash": 1 } }""");
        Assert.Equal(0, m.Version);   // kurulum eksik sinyali
    }

    [Fact]
    public void Parse_bilinmeyen_odeme_tipi_elenir()
    {
        // 99 cihaz tipi değil (1/4/16) → atlanır; geçerli olan kalır.
        var m = ParseOk("""{ "version": 1, "paymentMethods": { "11-cash": 1, "11-bozuk": 99 } }""");
        Assert.True(m.PaymentMethods.ContainsKey("11-cash"));
        Assert.False(m.PaymentMethods.ContainsKey("11-bozuk"));
    }

    [Fact]
    public void Parse_bozuk_girdi_atlanir_digeri_kalir()
    {
        // İlk girdide departmentIndex yok → atla; ikincisi geçerli → kalır.
        var m = ParseOk("""
        { "version": 1, "entries": [
            { "kind": "product", "productCode": "X" },
            { "kind": "product", "productCode": "Y", "departmentIndex": 3 } ] }
        """);
        Assert.Single(m.Entries);
        Assert.Equal("Y", m.Entries[0].ProductCode);
    }

    [Fact]
    public void Store_urun_departmani_oranla_cozer()
    {
        var store = new DeviceMappingStore();
        store.Update(ParseOk(FullBody), FullBody);
        var match = store.Resolve("1043-1601", "26-86");
        Assert.NotNull(match);
        Assert.Equal(10, match!.Value.Index);            // ürün ÖNCE (kategori 26-86 → dept 0'ı ezer)
        Assert.Equal(1000, match.Value.TaxRateBasisPoints);   // dept 10'un oranı
    }

    [Fact]
    public void Store_odeme_yontemi_cihaz_tipine_cozer()
    {
        var store = new DeviceMappingStore();
        store.Update(ParseOk(FullBody), FullBody);
        Assert.Equal(GmpPaymentTypes.Cash, store.Resolve("11-cash"));
        Assert.Equal(GmpPaymentTypes.Card, store.Resolve("11-credit"));
        Assert.Null(store.Resolve("11-uydurma"));   // eşlenmemiş → null → fail-closed
    }

    [Fact]
    public void Store_IsConfigured_versiyon_sifirsa_false()
    {
        var store = new DeviceMappingStore();
        Assert.False(store.IsConfigured);                          // hiç yüklenmedi
        Assert.Null(store.CurrentVersion);
        store.Update(ParseOk("""{ "version": 0 }"""), """{ "version": 0 }""");
        Assert.False(store.IsConfigured);                          // yüklendi ama version 0 = kurulum eksik
        store.Update(ParseOk(FullBody), FullBody);
        Assert.True(store.IsConfigured);                           // version>0
    }
}
