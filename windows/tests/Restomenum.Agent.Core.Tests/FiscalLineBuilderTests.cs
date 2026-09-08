using System.Linq;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Kuruş dağıtımı — GMP fiş toplamını (birim×adet) GET'in ItemAmount'una eşitler; <b>adet ve toplam
/// TAM korunur</b>. Peer'in ölçtüğü vakalar dahil.
/// </summary>
public class FiscalLineBuilderTests
{
    private static SaleLine Item(long itemAmount, int qty) =>
        new(ItemId: 0, ProductCode: "p1", ProductLabel: "Ürün", Quantity: qty,
            ItemAmountMinor: itemAmount, TaxCode: "10", CategoryId: "c1", LineId: "l1");

    [Theory]
    [InlineData(2999, 3, 2)]   // 999 + 2×1000
    [InlineData(3001, 3, 2)]   // 2×1000 + 1001
    [InlineData(1000, 3, 2)]   // 2×333 + 334
    [InlineData(1000, 7, 2)]   // 142 + 6×143
    [InlineData(3000, 3, 1)]   // tam bölünür → tek satır
    [InlineData(24000, 2, 1)]  // tam bölünür
    public void Adet_ve_toplam_TAM_korunur(long itemAmount, int qty, int beklenenSatir)
    {
        var lines = FiscalLineBuilder.Build(Item(itemAmount, qty), 5).ToList();
        Assert.Equal(beklenenSatir, lines.Count);
        Assert.Equal(itemAmount, lines.Sum(l => l.UnitPriceMinor * l.Quantity));   // toplam TAM
        Assert.Equal(qty, lines.Sum(l => l.Quantity));                             // adet TAM
        Assert.All(lines, l => Assert.Equal(5, l.DepartmentNo));                    // aynı departman
    }

    [Fact]
    public void Iki_satir_birim_farki_tam_1_kurus()
    {
        // 2999/3 → (1 × 999) + (2 × 1000)
        var lines = FiscalLineBuilder.Build(Item(2999, 3), 5).OrderBy(l => l.UnitPriceMinor).ToList();
        Assert.Equal((999, 1), (lines[0].UnitPriceMinor, lines[0].Quantity));
        Assert.Equal((1000, 2), (lines[1].UnitPriceMinor, lines[1].Quantity));
    }

    [Fact]
    public void Bolunende_tek_satir()
    {
        var l = Assert.Single(FiscalLineBuilder.Build(Item(3000, 3), 5).ToList());
        Assert.Equal((1000L, 3), (l.UnitPriceMinor, l.Quantity));
    }

    // ── TaxRule: TEK karar noktası (satış + kuru prova aynı çağrıyı yapar) ──────

    [Theory]
    [InlineData("10", 1000)]   // %10 ürün, %10 departman
    [InlineData("20", 2000)]
    [InlineData("1", 100)]
    [InlineData("24", 2400)]
    public void Oranlar_UYUSUYORSA_celiski_YOK(string taxCode, int deptRate)
        => Assert.False(TaxRule.Conflicts(taxCode, deptRate));

    [Theory]
    [InlineData("7", 2000)]    // canlı vaka: ürün %7, departman %20
    [InlineData("19", 2000)]
    [InlineData("10", 2000)]
    [InlineData("20", 1000)]
    public void Oranlar_UYUSMUYORSA_celiski_VAR(string taxCode, int deptRate)
        => Assert.True(TaxRule.Conflicts(taxCode, deptRate));

    [Theory]
    [InlineData("10", null)]   // departman oranı bilinmiyor
    [InlineData(null, 2000)]   // TaxCode yok
    [InlineData("", 2000)]
    [InlineData("abc", 2000)]  // sayı değil
    [InlineData("10.5", 2000)] // ondalık — tam sayı değil
    public void KARSILASTIRILAMIYORSA_reddetmeyiz(string? taxCode, int? deptRate)
    {
        // ← ÇİVİ: kanıtsız ret YOK. "Okunamadı"yı "yanlış" saymak her kalemi düşürürdü;
        // §30.12 koruması yalnız GERÇEK, sayısal çelişkide devreye girer.
        Assert.False(TaxRule.Conflicts(taxCode, deptRate));
    }

    // ── PaymentWindow: cihazın İKİ ayrı dizi biçimi de doğru okunmalı ───────────

    [Fact]
    public void Dizi_COUNT_uzunlugundaysa_dolu_kayitlar_SONDA()
    {
        // Ölçüm 2026-09-07 18:00: total=3, inThis=1, dizi 3 uzunluğunda, dolu olan index 2.
        Assert.Equal((2, 3), PaymentWindow.Range(totalCount: 3, inThisCount: 1, arrayLength: 3));
    }

    [Fact]
    public void Dizi_INTHIS_uzunlugundaysa_dolu_kayitlar_BASTA()
    {
        // ← ÇİVİ (ölçüm 2026-09-08 13:02): total=2, inThis=1, dizi 1 uzunluğunda, dolu index 0.
        // Eski mantık [count-inThis .. count-1] = index 1'e bakıyordu; dizi o kadar uzun değil,
        // sonuç boş liste → başarısız kart denemesinde banka bacağı bildirilemedi.
        Assert.Equal((0, 1), PaymentWindow.Range(totalCount: 2, inThisCount: 1, arrayLength: 1));
    }

    [Fact]
    public void Dizinin_TAMAMI_doluysa_hepsi_alinir()
    {
        Assert.Equal((0, 2), PaymentWindow.Range(totalCount: 2, inThisCount: 2, arrayLength: 2));
    }

    [Theory]
    [InlineData(0, 0, 24)]    // fişte ödeme yok
    [InlineData(3, 0, 3)]     // bu yanıtta dolu kayıt yok
    [InlineData(2, 5, 2)]     // inThis > total → tutarsız, iddia etme
    [InlineData(2, -1, 2)]    // negatif sayaç
    [InlineData(3, 1, 0)]     // dizi hiç gelmemiş
    public void Tutarsiz_ya_da_bos_hallerde_BOS_aralik(int total, int inThis, int len)
    {
        // ← ÇİVİ: şüphede kalınca hiçbir kayıt iddia edilmez. Uydurulmuş bir satır, deftere
        // gerçekleşmemiş bir ödeme yazdırabilirdi.
        var (b, s2) = PaymentWindow.Range(total, inThis, len);
        Assert.Equal(b, s2);
    }
}
