using System.Text.Json;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host;

/// <summary>
/// <see cref="IPaymentMethodResolver"/>'ın dosya-tabanlı uygulaması: <c>PaymentMethodId → cihaz ödeme
/// tipi</c> eşlemesini bir JSON dosyasından okur. Örn. <c>{ "11-cash": 1, "11-credit": 4, "11-voucher": 16 }</c>
/// (anahtar platformun DİKTE ettiği yöntem kimliği, değer <c>GmpPaymentTypes</c> 1/4/16).
///
/// <para><b>Kaynak GEÇİCİ olarak yerel dosya:</b> üretimde bu eşlemeyi Ingenico eklentisi kendi UI'ında
/// kurar ve <c>GET /api/device/mapping</c> ile ajana ulaştırır (departmanlarla aynı kanal, tek sürüm/ETag).
/// O çekme istemcisi ayrı bir iş; arayüz sabit, kaynak sonra değişebilir.</para>
///
/// <para>Dosya yoksa harita BOŞ → her satış PAYMENT_METHOD_UNMAPPED ile terminale gitmeden reddedilir
/// (fail-closed — tahmin edilmiş ödeme tipi yanlış iptal yolu seçer). Değeri bilinmeyen (1/4/16 dışı)
/// girdiler yüklemede atlanır (uydurma tip terminale gitmesin).</para>
/// </summary>
public sealed class ConfigPaymentMethodResolver : IPaymentMethodResolver
{
    private readonly IReadOnlyDictionary<string, int> _map;

    public ConfigPaymentMethodResolver(string filePath, ILogger logger)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(filePath))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(filePath));
                if (parsed is not null)
                    foreach (var kv in parsed)
                    {
                        if (GmpPaymentTypes.IsKnown(kv.Value)) map[kv.Key] = kv.Value;
                        else logger.LogWarning("ödeme yöntemi eşlemesi atlandı: {Method} → {Type} bilinmeyen cihaz tipi (1/4/16 değil)", kv.Key, kv.Value);
                    }
                logger.LogInformation("ödeme yöntemi eşlemesi yüklendi: {Count} yöntem ({Path})", map.Count, filePath);
            }
            else
            {
                logger.LogWarning("ödeme yöntemi eşleme dosyası yok ({Path}) — harita BOŞ, her satış " +
                    "PAYMENT_METHOD_UNMAPPED ile durur. Üretimde eşleme eklentiden gelecek.", filePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ödeme yöntemi eşleme dosyası okunamadı ({Path}) — harita BOŞ.", filePath);
        }
        _map = map;
    }

    public int? Resolve(string paymentMethodId) =>
        !string.IsNullOrEmpty(paymentMethodId) && _map.TryGetValue(paymentMethodId, out var type) ? type : null;
}
