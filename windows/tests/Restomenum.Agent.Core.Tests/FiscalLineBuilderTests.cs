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
}
