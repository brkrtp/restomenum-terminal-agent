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
/// <summary>
/// Çözümleme sonucu: cihaz departman <see cref="Index"/>'i + (biliniyorsa) o departmanın KDV oranı
/// <see cref="TaxRateBasisPoints"/> (BAZ PUAN, ör. 1000 = %10). Oran yalnız cihaz departman tablosu
/// yüklüyse doludur; <c>null</c> ise sessiz-mali-sapma doğrulaması atlanır.
/// </summary>
public readonly record struct DepartmentMatch(int Index, int? TaxRateBasisPoints);

public interface ILineDepartmentResolver
{
    /// <summary>
    /// Kararlı kimlikten departman eşleşmesi. ÖNCE <paramref name="productCode"/> denenir, yoksa
    /// <paramref name="categoryId"/>'ye düşülür (ürün eşlemesi kategoriyi ezer — karma-KDV kategoride tek
    /// ürünü doğru departmana yazmanın tek yolu). Hiçbiri eşleşmezse <c>null</c> → PRODUCT_UNMAPPED
    /// (terminale gitmeden ret). Dönen eşleşme, biliniyorsa departmanın KDV oranını da taşır ki çağıran
    /// GET'teki <c>TaxCode</c> ile çelişkiyi (sessiz mali sapma, §30.12) yakalayabilsin.
    /// </summary>
    DepartmentMatch? Resolve(string? productCode, string? categoryId);
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
        // İndirim de SATIR TOPLAMIDIR (sözleşme: `0 < DiscountAmount ≤ ItemAmount`, ikisi de satır).
        // Doğrulaması `LocalSaleHandler.IndirimDogrula`da yapıldı; buraya yalnız geçerli değer gelir.
        var indirim = item.DiscountAmountRaw is decimal ham && ham > 0 ? (long)ham : 0L;

        if (qty <= 1 || total <= 0)
        {
            // Tek kalem (ya da adet≤1): birim = toplam, adet = max(1, qty).
            yield return Line(item, departmentNo, Math.Max(1, qty), total, indirim);
            yield break;
        }

        // ── FİYAT VE İNDİRİM AYNI ANDA DAĞITILIR ────────────────────────────────────
        // İkisi ayrı ayrı bölünseydi kalan kuruşlar farklı birimlere düşer ve satırı ikiye ayırmak
        // yetmezdi; üstelik toplamlardan biri kayabilirdi. Birim i şunu alır:
        //   fiyat   = q  + (i >= qty-rp ? 1 : 0)
        //   indirim = dq + (i >= qty-rd ? 1 : 0)
        // Fazla kuruş İKİSİNDE DE SONDAKİ birimlere veriliyor; böylece indirim yokken (rd=0)
        // gruplar bugünkü iki satıra birebir çöküyor ve fiş görünümü değişmiyor.
        var q = total / qty;
        var rp = (int)(total - q * qty);       // fiyatın kalan kuruşu (0 ≤ rp < qty)
        var dq = indirim / qty;
        var rd = (int)(indirim - dq * qty);    // indirimin kalan kuruşu (0 ≤ rd < qty)

        // Sınırlar: birim indeksleri artarken fiyat ve indirim en fazla iki noktada değişir.
        var sinir1 = qty - Math.Max(rp, rd);
        var sinir2 = qty - Math.Min(rp, rd);

        foreach (var (adet, fiyatArti, indirimArti) in new[]
        {
            (sinir1,          0, 0),
            (sinir2 - sinir1, rp >= rd ? 1 : 0, rd >= rp ? 1 : 0),
            (qty - sinir2,    1, 1),
        })
        {
            if (adet > 0)
                yield return Line(item, departmentNo, adet, q + fiyatArti, dq + indirimArti);
        }
    }

    private static FiscalLine Line(SaleLine item, int dept, int qty, long unitMinor, long indirimMinor) =>
        // VatRate=0: bilgi amaçlı; cihaz KDV'yi departmandan türetir (GmpWrapper taxRate=0).
        new(ProductId: item.ProductCode, Name: item.ProductLabel, Quantity: qty,
            UnitPriceMinor: unitMinor, VatRate: 0m, DepartmentNo: dept,
            LineDiscountMinor: indirimMinor);
}
