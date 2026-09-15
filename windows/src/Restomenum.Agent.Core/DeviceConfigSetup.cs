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
    public static DeviceConfigSetup? TryParse(string? setupString) => TryParse(setupString, out _);

    /// <summary>
    /// W150: kurulum sayfası kullanıcıya NEDEN reddedildiğini söylemeli (W129 dersi: sebep ayrışır).
    /// Kurallar eklenti sahibinin tarifi: adres <c>https</c> (yalnız loopback'te <c>http</c> — geliştirme),
    /// yol/sorgu/parça YOK (eklentinin <c>PUBLIC_ORIGIN</c>'i bir kök adrestir), sır boş değil.
    /// <b>Sebep metni sırrı ASLA içermez.</b>
    /// </summary>
    public static DeviceConfigSetup? TryParse(string? setupString, out string? neden)
    {
        neden = null;
        if (string.IsNullOrWhiteSpace(setupString)) { neden = "kurulum dizesi boş"; return null; }
        string json;
        try { json = Encoding.UTF8.GetString(Convert.FromBase64String(setupString.Trim())); }
        catch (FormatException) { neden = "kurulum dizesi base64 değil — panelden kopyalanan dizeyi eksiksiz yapıştırın"; return null; }

        string? url, secret;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            secret = root.TryGetProperty("secret", out var s) ? s.GetString() : null;
        }
        catch (JsonException) { neden = "kurulum dizesi çözüldü ama içeriği JSON değil"; return null; }

        if (string.IsNullOrWhiteSpace(url)) { neden = "kurulum dizesinde eklenti adresi (url) yok"; return null; }
        if (string.IsNullOrWhiteSpace(secret)) { neden = "kurulum dizesinde sır (secret) yok"; return null; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)) { neden = "eklenti adresi geçerli bir URL değil"; return null; }
        if (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback))
        { neden = "eklenti adresi https olmalı"; return null; }
        if (!string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) || baseUri.AbsolutePath.Trim('/').Length > 0)
        { neden = "eklenti adresi yalnız kök adres olmalı (yol/sorgu olmadan)"; return null; }

        // Taban adresin sonu "/" olmalı ki göreli uçlar (api/device/...) doğru birleşsin.
        if (!baseUri.AbsoluteUri.EndsWith('/')) baseUri = new Uri(baseUri.AbsoluteUri + "/");
        return new DeviceConfigSetup(baseUri, secret);
    }

    /// <summary>Kanonik kurulum dizesi: <c>base64(JSON{url,secret})</c>. <see cref="TryParse(string?)"/> ile geri okunur.</summary>
    public static string Kodla(DeviceConfigSetup ayar)
    {
        var json = JsonSerializer.Serialize(new { url = ayar.BaseUri.AbsoluteUri.TrimEnd('/'), secret = ayar.Secret });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}

/// <summary>
/// <b>W150 — kurulum dizesinin KAYNAĞI.</b> Kurulum sayfası dizeyi veri dizinine
/// (<c>device-config.setup</c>, <c>agent.db</c>'nin yanı) yazar; <c>appsettings.json</c>'a dokunmaz
/// (program dizini yazılamayabilir, dosya biçimi bozulabilir). Açılışta DOSYA, yapılandırmadan
/// (<c>Agent:DeviceConfigSetup</c>) ÖNCE gelir: kullanıcının sayfadan yaptığı son ayar, paketle
/// gelen ayarı ezer. Dosya var ama bozuksa yapılandırmaya DÜŞÜLMEZ — bozuk dosya sessizce eski
/// adrese dönmek değil, görünür bir uyarı demektir (üç durum: yok / bozuk / geçerli).
/// </summary>
public static class DeviceConfigSetupKaynagi
{
    public const string DosyaAdi = "device-config.setup";

    public static string DosyaYolu(string veriDizini) => Path.Combine(veriDizini, DosyaAdi);

    /// <param name="dosyaIcerigi">Dosya içeriği; <c>null</c> = dosya yok.</param>
    /// <param name="yapilandirma"><c>Agent:DeviceConfigSetup</c> değeri; <c>null</c> = yok.</param>
    /// <param name="kaynak">İnsan için: "dosya" / "yapılandırma" / "yok" / "dosya BOZUK".</param>
    public static DeviceConfigSetup? Coz(string? dosyaIcerigi, string? yapilandirma, out string kaynak)
    {
        if (dosyaIcerigi is not null)
        {
            var d = DeviceConfigSetupParser.TryParse(dosyaIcerigi, out var neden);
            kaynak = d is null ? $"dosya BOZUK ({neden})" : "dosya";
            return d;
        }
        var y = DeviceConfigSetupParser.TryParse(yapilandirma);
        kaynak = y is null ? "yok" : "yapılandırma";
        return y;
    }
}
