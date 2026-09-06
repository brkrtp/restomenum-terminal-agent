using System.Text;
using System.Text.Json;

namespace Restomenum.Agent.Core;

/// <summary>
/// Cihaz config kanalı kurulum bilgisi: eklentinin taban adresi + kurulum sırrı. Operatör TEK bir
/// "kurulum dizesi" (<c>base64(JSON{url,secret})</c>) yapıştırır — dört alanı elle girmek, bu projede
/// kodların boşa yandığı aynı insan-zinciri hatasıdır. Sır bir kimlik taşır (eklenti hash'iyle cihazı
/// bulur); ajan yalnız saklar ve <c>POST /api/device/session</c>'da sunar.
/// </summary>
public sealed record DeviceConfigSetup(Uri BaseUri, string Secret);

/// <summary>Kurulum dizesini (<c>base64(JSON{url,secret})</c>) çözer. Yok/geçersizse <c>null</c> — o
/// durumda config kanalı KAPALI, ajan yerel dosya eşlemesiyle çalışır (bugünkü davranış).</summary>
public static class DeviceConfigSetupParser
{
    public static DeviceConfigSetup? TryParse(string? setupString)
    {
        if (string.IsNullOrWhiteSpace(setupString)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(setupString.Trim()));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            var secret = root.TryGetProperty("secret", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(secret)) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)) return null;
            // Taban adresin sonu "/" olmalı ki göreli uçlar (api/device/...) doğru birleşsin.
            if (!baseUri.AbsoluteUri.EndsWith('/')) baseUri = new Uri(baseUri.AbsoluteUri + "/");
            return new DeviceConfigSetup(baseUri, secret);
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
