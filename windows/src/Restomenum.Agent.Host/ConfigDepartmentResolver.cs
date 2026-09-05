using System.Text.Json;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host;

/// <summary>
/// <see cref="ILineDepartmentResolver"/>'ın dosya-tabanlı uygulaması: <c>kimlik → cihaz departman
/// indeksi</c> eşlemesini bir JSON dosyasından okur. Anahtar ProductCode VEYA CategoryId olabilir
/// (§20.2); çözümleme ÖNCE ProductCode dener. Örn. <c>{ "1043-1601": 10 }</c> (Espresso → dept 10=%10).
///
/// <para><b>Veri kaynağı GEÇİCİ:</b> §20.2'ye göre eşlemeyi işletme eklentinin kurulum ekranından
/// girer ve ajan onu hem çözer hem cihaza yazar (<c>SetDepartments</c>). O kanal (ekran→ajan) henüz
/// bağlanmadı; şimdilik yerel dosya. Arayüz sabit, kaynak sonra değişebilir.</para>
///
/// <para>Dosya yoksa harita BOŞ → her kalem <c>PRODUCT_UNMAPPED</c> ile terminale gitmeden reddedilir
/// (fail-closed — tahmin edilmiş departman yanlış mali kayıt yazardı, geri alınamaz).</para>
///
/// <para><b>Cihaz departman-oran tablosu (ikinci dosya, opsiyonel):</b> <c>{ "10": 1000 }</c> biçiminde
/// <c>departman indeksi → KDV oranı (baz puan)</c>. Yüklüyse çözümleme her eşleşmeyle o oranı da döner ve
/// çağıran GET'teki <c>TaxCode</c> ile çelişkiyi (§30.12 sessiz mali sapma) yakalar. Yoksa oran <c>null</c>
/// döner ve doğrulama atlanır — mevcut davranış korunur.</para>
/// </summary>
public sealed class ConfigDepartmentResolver : ILineDepartmentResolver
{
    private readonly IReadOnlyDictionary<string, int> _map;
    private readonly IReadOnlyDictionary<int, int> _rates;   // departman indeksi → baz puan (boş olabilir)

    public ConfigDepartmentResolver(string filePath, ILogger logger, string? ratesFilePath = null)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(filePath))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(filePath));
                if (parsed is not null)
                    foreach (var kv in parsed) map[kv.Key] = kv.Value;
                logger.LogInformation("departman eşlemesi yüklendi: {Count} kimlik ({Path})", map.Count, filePath);
            }
            else
            {
                logger.LogWarning("departman eşleme dosyası yok ({Path}) — harita BOŞ, her satış PRODUCT_UNMAPPED " +
                    "ile durur. Eşleme kaynağı sözleşmede netleşince değişecek.", filePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "departman eşleme dosyası okunamadı ({Path}) — harita BOŞ.", filePath);
        }
        _map = map;

        var rates = new Dictionary<int, int>();
        try
        {
            if (!string.IsNullOrEmpty(ratesFilePath) && File.Exists(ratesFilePath))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(ratesFilePath));
                if (parsed is not null)
                    foreach (var kv in parsed)
                        if (int.TryParse(kv.Key, out var idx)) rates[idx] = kv.Value;
                logger.LogInformation("cihaz departman-oran tablosu yüklendi: {Count} departman ({Path})", rates.Count, ratesFilePath);
            }
            else
            {
                logger.LogWarning("departman-oran tablosu yok ({Path}) — sessiz mali sapma doğrulaması KAPALI " +
                    "(yanlış departman↔TaxCode eşleşmesi yakalanmaz). Tablo koyulunca §30.12 devreye girer.", ratesFilePath ?? "(verilmedi)");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "departman-oran tablosu okunamadı ({Path}) — doğrulama KAPALI.", ratesFilePath ?? "(verilmedi)");
        }
        _rates = rates;
    }

    // ÖNCE ProductCode (en özgül), yoksa CategoryId. Aynı düz sözlük her iki kimlik türünü de tutar
    // (§20.2: productId ve/veya categoryId); ürün anahtarı kategoriyi ezer. Eşleşmeye departmanın KDV
    // oranını (tablo yüklüyse) iliştirir ki çağıran TaxCode çelişkisini yakalayabilsin.
    public DepartmentMatch? Resolve(string? productCode, string? categoryId)
    {
        int? dept = null;
        if (productCode is not null && _map.TryGetValue(productCode, out var byProduct)) dept = byProduct;
        else if (categoryId is not null && _map.TryGetValue(categoryId, out var byCategory)) dept = byCategory;
        if (dept is null) return null;
        return new DepartmentMatch(dept.Value, _rates.TryGetValue(dept.Value, out var rate) ? rate : null);
    }
}
