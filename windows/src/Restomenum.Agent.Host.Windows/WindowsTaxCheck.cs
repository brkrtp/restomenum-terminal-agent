using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>KURU PROVA</b> — <c>--check-tax &lt;dosya.json&gt;</c> (W28). Katalogdaki kalemlerin satışta
/// reddedilip reddedilmeyeceğini <b>terminale gitmeden, satış denemeden</b> söyler.
///
/// <para><b>Neden var:</b> "eşlemeyi düzelttim, artık çalışır mı?" sorusunun bugünkü tek cevabı
/// kasiyerin müşteri karşısında kart okutması. 7 Eylül 2026'da kullanıcı bunu üç kez yaptı ve üç
/// kez aynı reddi aldı. Denemeden görebilmek gerekiyordu.</para>
///
/// <para><b>Neden bu araç, eklentinin kendi hesabı değil:</b> aynı kuralı iki yerde uygulamak, iki
/// yerin sessizce ıraksaması demek. Bu araç <b>satışın çalıştırdığı kodun ta kendisini</b>
/// çalıştırır: aynı <see cref="ILineDepartmentResolver"/>, aynı karşılaştırma. Yani "prova geçti
/// ama satış reddetti" olamaz — olursa hata provada değil, kuralın kendisindedir.</para>
///
/// <para>Girdi: <c>[{"productCode":"...","categoryId":"...","taxCode":"7","label":"..."}]</c>.
/// <c>label</c> isteğe bağlı, yalnız çıktıyı okunur kılar. Cihaza HİÇ dokunulmaz, eşleştirme bile
/// yapılmaz.</para>
/// </summary>
public static class WindowsTaxCheck
{
    private sealed record Kalem(string? ProductCode, string? CategoryId, string? TaxCode, string? Label);

    public static bool Run(IServiceProvider services, string dosya)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("TaxCheck");
        var resolver = services.GetRequiredService<ILineDepartmentResolver>();

        List<Kalem>? kalemler;
        try
        {
            var json = File.ReadAllText(dosya);
            kalemler = JsonSerializer.Deserialize<List<Kalem>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (Exception e)
        {
            log.LogError("dosya okunamadı ({Dosya}): {Hata}", dosya, e.Message);
            return false;
        }

        if (kalemler is null || kalemler.Count == 0)
        {
            log.LogError("dosyada kalem yok — hiçbir şey iddia edilmiyor.");
            return false;
        }

        int gecer = 0, esleme_yok = 0, oran_celiskisi = 0, atlanan = 0;
        foreach (var k in kalemler)
        {
            var ad = k.Label ?? k.ProductCode ?? "(isimsiz)";

            // 1. Departman çözümü — satıştaki İLK kapı. Ürün eşlemesi kategoriyi EZER.
            var m = resolver.Resolve(k.ProductCode, k.CategoryId);
            if (m is null)
            {
                esleme_yok++;
                log.LogWarning("RET  PRODUCT_UNMAPPED  {Ad} (ürün={P} kategori={K}) — eşleme yok",
                    ad, k.ProductCode ?? "-", k.CategoryId ?? "-");
                continue;
            }

            // 2. Oran doğrulaması — satıştaki İKİNCİ kapı, birebir aynı koşul.
            //    Departmanın oranı bilinmiyorsa ya da TaxCode sayı değilse doğrulama ATLANIR
            //    (satışta da atlanıyor); "geçer" demiyoruz, "kontrol edilemedi" diyoruz.
            if (m.Value.TaxRateBasisPoints is not int deptRate)
            {
                atlanan++;
                log.LogInformation("? ATLANDI  {Ad} — departman {D} oranı BİLİNMİYOR, satışta da kontrol edilmez",
                    ad, m.Value.Index);
                continue;
            }
            if (!int.TryParse(k.TaxCode, out var taxPct))
            {
                atlanan++;
                log.LogInformation("? ATLANDI  {Ad} — TaxCode sayı değil ('{T}'), satışta da kontrol edilmez",
                    ad, k.TaxCode ?? "-");
                continue;
            }

            // ⚠️ Kural BURADA YENİDEN YAZILMIYOR — satışın çağırdığı `TaxRule`'un aynısı.
            // K-30'dan sonra sonucu değişti: bu artık RET değil UYARI. Satış geçer, fişe BÖLÜMÜN
            // oranı basılır, ayrışma yanıtta `taxMismatches` ile bildirilir.
            if (TaxRule.Conflicts(k.TaxCode, deptRate))
            {
                oran_celiskisi++;
                log.LogWarning("UYARI  {Ad} — satış GEÇER, fişe %{DO} basılır; ürün beyanı %{U}",
                    ad, deptRate / 100.0, taxPct);
                continue;
            }

            gecer++;
            log.LogInformation("OK   {Ad} — departman {D}, iki taraf da %{U}", ad, m.Value.Index, taxPct);
        }

        log.LogInformation("── ÖZET ── toplam={T} geçer={G} eşleme-yok(RET)={E} oran-uyarısı={O} kontrol-edilemedi={A}",
            kalemler.Count, gecer, esleme_yok, oran_celiskisi, atlanan);

        // Çıkış kodu YALNIZ gerçek retlerde 1 (K-30). Oran çelişkisi artık satışı düşürmüyor;
        // uyarıyı hata sayarsak betikler çalışan bir kurulumu "bozuk" diye raporlar.
        return esleme_yok == 0;
    }
}
