namespace Restomenum.Agent.Core;

/// <summary>
/// Satıştan hemen ÖNCE eşlemenin tazeliğini kontrol eden kanal (W29).
///
/// <para><b>Bu, K-21'in bilinçli olarak GERİ ALINMASIDIR.</b> Eskiden kural "satış anında çekme
/// yok" idi ve gerekçesi sağlamdı: satış yolu yapılandırma kanalına bağımlı olmamalı. Ama sahada
/// bunun bedeli ölçüldü (2026-09-07): operatör bir eşleme hatasını düzeltti, hemen denedi, ~30
/// dakikalık yoklama aralığı yüzünden ajan hâlâ eski sürümdeydi ve aynı reddi aldı. Düzeltmenin
/// işe yarayıp yaramadığını göremedi. Yani "satışı yapılandırmadan ayır" kararı, düzeltmeyi
/// doğrulanamaz hâle getiriyordu.</para>
///
/// <para><b>K-21'in koruduğu şey KORUNUYOR:</b> bu kontrol satışı ASLA düşürmez. Zaman aşımı,
/// ağ hatası, kimlik hatası — hepsinde diskteki eşlemeyle DEVAM edilir ve durum loglanır. Yani
/// bağımlılık "gerekli" değil "fırsatçı": kanal ayakta ve hızlıysa taze veriyle satarız, değilse
/// eskisiyle. Kart penceresi zaten 20–32 saniye; buradaki kısa zaman aşımı onun yanında
/// görünmez, ama sınırsız beklemek satışı yapılandırma kanalının rehinesi yapardı.</para>
/// </summary>
public interface IMappingRefresher
{
    /// <summary>
    /// Eşlemeyi koşullu GET ile kontrol eder; değişmişse günceller. <b>Asla istisna fırlatmaz</b> ve
    /// çağıranın sonucu kontrol etmesi gerekmez — başarısızlık sessiz değil (loglanır) ama satışı
    /// durdurmaz.
    /// </summary>
    /// <returns>Eşleme bu çağrıda GÜNCELLENDİYSE yeni sürüm; değişmediyse ya da kontrol edilemediyse <c>null</c>.</returns>
    Task<int?> EnsureFreshAsync(CancellationToken ct = default);
}
