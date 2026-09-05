using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Restomenum.Agent.Core;
using Restomenum.Agent.Host;
using Restomenum.Agent.Windows;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// Windows'a özgü kayıt (enrollment) önyüklemesi — <b>oturumdan ÖNCE</b>, host çalışmaya
/// başlamadan çalışır. Cihaz henüz kayıtlı değilse tek kullanımlık kod ile kaydolur ve dönen
/// <c>connectorId</c>'yi dayanıklı olarak yazar.
///
/// <para>Neden Core'da değil: tek somut .NET istemcisi bu; Android ayrı Kotlin. İkinci gerçek örnek
/// yokken <c>IEnrollmentProvider</c> soyutlamak erken soyutlama olurdu. Kayıt akışının imzası yok
/// (kod'un kendisi kimlik), o yüzden protokol karmaşası (kanonik dize, DER) session tarafında kalıyor
/// ve bu adım gerçekten "Windows'a özgü basit bir önyükleme" oluyor. Dondurulmuş <see cref="IDeviceKey"/>
/// yüzeyine dokunmaz.</para>
///
/// <para><b>Üç değişmez (doğruluğu bunlar belirler):</b>
/// <list type="number">
///   <item>Anahtar bir kez üretilir ve denemeler arasında KORUNUR. <see cref="WindowsDeviceKey"/>
///   anahtarı CNG MachineKey deposunda aç-ya-da-üret ile tutar; burada yalnız açık anahtar OKUNUR,
///   asla yeniden üretilmez. Yeniden üretmek açık anahtarı değiştirir ve sunucudaki kaydı yetim bırakır.</item>
///   <item><c>connectorId</c> oturuma geçmeden ÖNCE dayanıklı yazılır (agent'ın durable-write-before-ack
///   invariant'ının aynısı). Kalıcılaştırma başarısızsa akış DEVAM ETMEZ — kod yandı, cihaz yetim kalmasın.</item>
///   <item>Kayıt başarılı + kalıcılaştırma başarısız = SESSİZ RETRY YOK. Yüksek sesle dur, operatöre
///   "yeni kod gerekli, sebep: &lt;yerel hata&gt;" de; döngüde "kod kullanılmış" hatası gerçek sorunu gizler.</item>
/// </list></para>
///
/// <para><b>Kod ASLA loglanmaz</b> — 10 dk boyunca cihaz bağlama yetkisi taşır.</para>
/// </summary>
public static class WindowsEnrollment
{
    public static async Task EnsureEnrolledAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Enrollment");
        var key = services.GetRequiredService<IDeviceKey>();
        var opt = services.GetRequiredService<IOptions<AgentOptions>>().Value;

        // Zaten kayıtlı mı? WindowsDeviceKey.ConnectorId kalıcı dosyayı okur — doluysa kaydolma.
        if (!string.IsNullOrEmpty(key.ConnectorId))
        {
            log.LogInformation("cihaz zaten kayıtlı (connectorId mevcut) — kayıt atlanıyor");
            return;
        }

        // Yalnız gerçek donanım anahtarı kaydolur. Dev anahtarı (DevDeviceKey) kimliğini appsettings'ten
        // alır; kalıcılaştırma seam'i (SetConnectorId) yoktur — onun için kayıt akışı geçerli değil.
        if (key is not WindowsDeviceKey windowsKey)
        {
            log.LogWarning("cihaz anahtarı WindowsDeviceKey değil (dev?) ve connectorId boş — kayıt atlanıyor");
            return;
        }

        if (string.IsNullOrWhiteSpace(opt.EnrollmentCode) || string.IsNullOrWhiteSpace(opt.EnrollUrl))
            throw new InvalidOperationException(
                "Cihaz kayıtlı değil ve Agent:EnrollmentCode / Agent:EnrollUrl eksik. İlk kurulumda " +
                "tek kullanımlık kayıt kodu (TTL ~10 dk) ve kayıt ucu gereklidir.");

        // Değişmez #1: anahtar zaten kalıcı (WindowsDeviceKey ctor aç-ya-da-üret yaptı). Yalnız OKUYORUZ.
        var payload = new
        {
            serverId = opt.ServerId,
            data = new
            {
                code = opt.EnrollmentCode,            // loglanMAZ
                publicKey = key.PublicKeyPem,
                fingerprint = key.Fingerprint,
                platform = "windows",
                version = opt.Version,
            },
        };

        log.LogInformation("cihaz kayıtlı değil — kayıt isteği gönderiliyor (kod loglanmaz)");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        string body;
        System.Net.Http.HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync(opt.EnrollUrl, payload, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            // Ağ hatası: kod HENÜZ yanmamış olabilir (istek sunucuya ulaşmadıysa). Yüksek sesle dur.
            throw new InvalidOperationException(
                $"Kayıt isteği gönderilemedi ({ex.GetType().Name}: {ex.Message}). Ağı kontrol edip yeniden deneyin.", ex);
        }

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Kayıt reddedildi: HTTP {(int)resp.StatusCode}. Kod tükenmiş/geçersiz olabilir — yeni kod gerekli. Yanıt: {body}");

        string? connectorId;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (!success)
            {
                // success:false → sunucu isteği İŞLEDİ ama reddetti (biçim hatası DEĞİL). Sunucu
                // `enrollmentRejected` için bilerek tek mesaj veriyor: kod yanlış / süresi dolmuş /
                // zaten kullanılmış — hangisi olduğunu söylemiyor (tahmin kolaylaştırmasın diye).
                var msg = root.TryGetProperty("message", out var mEl) ? mEl.GetString() : null;
                if (msg == "plugin.connector.enrollmentRejected")
                    throw new InvalidOperationException(
                        "Kayıt reddedildi: kayıt kodu yanlış, süresi dolmuş veya zaten kullanılmış. " +
                        "YENİ kayıt kodu alıp tekrar deneyin.");
                throw new InvalidOperationException(
                    $"Kayıt reddedildi (sunucu mesajı: {msg ?? "yok"}). Gerekirse yeni kod alın. Ham yanıt: {body}");
            }
            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("connectorId", out var idEl))
                throw new InvalidOperationException($"Kayıt yanıtı beklenmedik biçimde: {body}");
            connectorId = idEl.GetString();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Kayıt yanıtı ayrıştırılamadı: {ex.Message}. Yanıt: {body}", ex);
        }

        if (string.IsNullOrWhiteSpace(connectorId))
            throw new InvalidOperationException("Kayıt yanıtında connectorId boş.");

        // Değişmez #2 + #3: connectorId'yi oturumdan ÖNCE dayanıklı yaz. Başarısızsa DEVAM ETME ve
        // SESSİZCE YENİDEN DENEME — kod yandı, tek çıkış yeni kod. Sebebi operatöre yüksek sesle söyle.
        try
        {
            windowsKey.SetConnectorId(connectorId);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Kayıt SUNUCUDA oluştu ama connectorId yerel diske YAZILAMADI — cihaz yetim kalır. " +
                $"YENİ kayıt kodu gerekli. Yerel sebep: {ex.GetType().Name}: {ex.Message}. " +
                "(LocalSystem yazma izni / disk durumu kontrol edilsin.)", ex);
        }

        // connectorId sır değildir (kimliktir); loglanabilir. Kod loglanmaz.
        log.LogInformation("kayıt başarılı — connectorId kalıcı yazıldı: {ConnectorId}", connectorId);
    }
}
