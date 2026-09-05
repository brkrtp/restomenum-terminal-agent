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
/// </summary>
public sealed class ConfigDepartmentResolver : ILineDepartmentResolver
{
    private readonly IReadOnlyDictionary<string, int> _map;

    public ConfigDepartmentResolver(string filePath, ILogger logger)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(filePath))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(filePath));
                if (parsed is not null)
                    foreach (var kv in parsed) map[kv.Key] = kv.Value;
                logger.LogInformation("departman eşlemesi yüklendi: {Count} kategori ({Path})", map.Count, filePath);
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
    }

    // ÖNCE ProductCode (en özgül), yoksa CategoryId. Aynı düz sözlük her iki kimlik türünü de tutar
    // (§20.2: productId ve/veya categoryId); ürün anahtarı kategoriyi ezer.
    public int? Resolve(string? productCode, string? categoryId)
    {
        if (productCode is not null && _map.TryGetValue(productCode, out var byProduct)) return byProduct;
        if (categoryId is not null && _map.TryGetValue(categoryId, out var byCategory)) return byCategory;
        return null;
    }
}
