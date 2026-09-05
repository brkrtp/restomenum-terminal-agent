namespace Restomenum.Agent.Core;

/// <summary>
/// Kalem → cihaz departman indeksi çözümleyici (yerel mimari, K-21 / §20.2). <b>Departman eşlemesi
/// cihaz kurulumuna aittir</b> (sağlayıcı=ajan), platformun veri modelinde DEĞİL — o yüzden GET
/// yanıtında <c>DepartmentNo</c> gelmez; ajan onu <see cref="SaleLine.ProductCode"/> (en özgül kararlı
/// kimlik), yoksa <see cref="SaleLine.CategoryId"/> üzerinden çözer.
///
/// <para><b>Neden ProductCode önce (peer ölçtü):</b> tek bir kategori KARMA KDV taşıyabilir — ör. bir
/// kategori altında %10 ve %7 ürünler bir arada. Kategoriyi tek departmana eşlersen o kategorideki
/// FARKLI oranlı ürünler de o departmana (dolayısıyla yanlış KDV'ye) yazılır. ProductCode kalemi tek
/// başına tanımlar; kategori yalnız tüm ürünleri gerçekten aynı oranı paylaşıyorsa güvenli bir yedektir.
/// §20.2 eşlemeye <c>productId</c> VE/VEYA <c>categoryId</c> izin verir; ürün eşlemesi kategoriyi EZER.</para>
///
/// <para><b>Neden TaxCode/ad değil:</b> KDV oranı departmanı belirlemez (çok departman aynı oranı
/// paylaşır) ve <c>TaxCode</c> kimlik taşımaz. Ayrıca <c>TaxCode</c> YÜZDE-string'dir (<c>"10"</c>),
/// cihaz departman oranı BAZ PUAN'dır (<c>1000</c>); doğrudan karşılaştırma her zaman ıskalar — o yüzden
/// eşleme kimlik üzerinden, KDV departmandan türetilir (fiş satırı <c>VatRate=0</c> gönderir). Ada göre
/// eşleme de yasak — ad değişince eşleme sessizce kopar.</para>
///
/// <para><b>Arayüz arkasında:</b> eşleme verisinin (eklenti kurulum ekranından ajana) hangi yolla
/// ulaşacağı ayrı bir karar; bu arayüz veri kaynağını gizler, dinleyici ona bağlanır.</para>
/// </summary>
public interface ILineDepartmentResolver
{
    /// <summary>
    /// Kararlı kimlikten departman indeksi. ÖNCE <paramref name="productCode"/> denenir, yoksa
    /// <paramref name="categoryId"/>'ye düşülür (ürün eşlemesi kategoriyi ezer — karma-KDV kategoride tek
    /// ürünü doğru departmana yazmanın tek yolu). Hiçbiri eşleşmezse <c>null</c> → PRODUCT_UNMAPPED
    /// (terminale gitmeden ret).
    /// </summary>
    int? Resolve(string? productCode, string? categoryId);
}

/// <summary>
/// Bir <see cref="SaleLine"/>'ı GMP fiş satır(lar)ına çevirir — <b>kuruş dağıtımı</b> ile.
///
/// <para>GMP fiş satır toplamını <c>birim × adet</c> hesaplıyor; GET ise satır TOPLAMINI
/// (<see cref="SaleLine.ItemAmountMinor"/>) veriyor ve türetilmiş birim fiyat adete tam bölünmeyebilir
/// (satır indirimi toplamın içinde). Tamsayı tek birim fiyat toplamı üretemezse, satır İKİYE bölünür
/// ki hem <b>adet</b> hem <b>toplam TAM</b> korunsun (peer ölçtü). Bölünüyorsa tek satır kalır — normal
/// durumda fiş görünümü değişmez. İki alt-satır aynı ürün olduğundan aynı departman + aynı KDV taşır.</para>
/// </summary>
public static class FiscalLineBuilder
{
    public static IEnumerable<FiscalLine> Build(SaleLine item, int departmentNo)
    {
        var total = item.ItemAmountMinor;   // OTORİTE
        var qty = item.Quantity;
        if (qty <= 1 || total <= 0)
        {
            // Tek kalem (ya da adet≤1): birim = toplam, adet = max(1, qty).
            yield return Line(item, departmentNo, Math.Max(1, qty), total);
            yield break;
        }

        var q = total / qty;          // aşağı yuvarlanmış birim
        var r = total - q * qty;      // dağıtılacak kalan kuruş (0 ≤ r < qty)
        if (r == 0)
        {
            yield return Line(item, departmentNo, qty, q);
        }
        else
        {
            // (qty−r) × q + r × (q+1) = qty·q + r = total  (adet ve toplam TAM korunur)
            yield return Line(item, departmentNo, qty - (int)r, q);
            yield return Line(item, departmentNo, (int)r, q + 1);
        }
    }

    private static FiscalLine Line(SaleLine item, int dept, int qty, long unitMinor) =>
        // VatRate=0: bilgi amaçlı; cihaz KDV'yi departmandan türetir (GmpWrapper taxRate=0).
        new(ProductId: item.ProductCode, Name: item.ProductLabel, Quantity: qty,
            UnitPriceMinor: unitMinor, VatRate: 0m, DepartmentNo: dept);
}
