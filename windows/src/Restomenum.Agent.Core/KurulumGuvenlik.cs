using System.Security.Cryptography;
using System.Text;

namespace Restomenum.Agent.Core;

/// <summary>
/// <b>RM-0389 — kurulum uçlarının kapısı.</b> Kurulum sayfası cihaz kaydını söker, terminal adresini
/// ve eklenti sırrını değiştirir, terminali eşleştirir. Yalnız onay sözcüğü yetmez: başka bir site
/// tarayıcı üzerinden <c>127.0.0.1</c>'e "basit istek" gönderebilir (OWASP CSRF).
///
/// <list type="bullet">
/// <item>Yöntem yalnız GET ya da POST. HEAD/OPTIONS/PUT/… tarayıcıdan ön sorgusuz gelebilir ve
///   eskiden işlem dalına giriyordu (denetim 2, Fable A).</item>
/// <item>İstemci yerel olmalı (dinleyici başka adrese açılmış olsa bile).</item>
/// <item><c>Host</c> başlığı yerel adres olmalı — DNS yeniden bağlamayla gelen sayfalar düşer.</item>
/// <item>POST'ta gövde <c>application/json</c> olmalı: tarayıcı bunu ancak ön kontrolle (CORS)
///   gönderir ve dinleyici CORS izni vermediği için başka siteden hiç ulaşamaz.</item>
/// <item><c>Origin</c> gönderildiyse sayfanın kendi adresi olmalı.</item>
/// <item>Sayfaya gömülü, süreç başına üretilen tahmin edilemez anahtar <c>X-Kurulum-Anahtari</c>
///   başlığında gelmeli.</item>
/// </list>
/// </summary>
public static class KurulumGuvenlik
{
    public const string AnahtarBasligi = "X-Kurulum-Anahtari";

    /// <summary>
    /// Her kurulum yanıtına eklenir. Sayfa eklentiden kod hazır gelmiş hâlde açılabildiği için başka
    /// bir sitenin çerçevesine GÖMÜLEMEZ — yoksa görünmez çerçeveyle "Kaydol"a tıklatılabilirdi
    /// (tıklama kaçırma). <c>no-referrer</c>: sayfa adresi dışarı sızmaz.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> YanitBasliklari =
    [
        new("X-Frame-Options", "DENY"),
        new("Content-Security-Policy", "frame-ancestors 'none'"),
        new("Referrer-Policy", "no-referrer"),
    ];

    public static string YeniAnahtar() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static bool GetMi(string yontem) => string.Equals(yontem, "GET", StringComparison.OrdinalIgnoreCase);
    public static bool PostMu(string yontem) => string.Equals(yontem, "POST", StringComparison.OrdinalIgnoreCase);

    /// <returns><c>null</c> = geçer; dolu = ret sebebi (sır içermez).</returns>
    public static string? Denetle(string yontem, bool yerelIstemci, string? host, string? origin,
        string? icerikTuru, string? anahtar, string beklenenAnahtar, int port)
    {
        if (!GetMi(yontem) && !PostMu(yontem)) return $"yöntem desteklenmiyor ({yontem})";
        if (!yerelIstemci) return "kurulum yalnız bu bilgisayardan yapılabilir";

        var izinli = new[] { $"127.0.0.1:{port}", $"localhost:{port}" };
        if (host is null || !izinli.Contains(host, StringComparer.OrdinalIgnoreCase))
            return $"geçersiz Host başlığı ({host ?? "yok"})";

        if (GetMi(yontem)) return null;

        if (icerikTuru is null || !icerikTuru.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            return "istek gövdesi JSON olmalı (application/json)";
        if (origin is not null && !izinli.Any(h => string.Equals(origin, "http://" + h, StringComparison.OrdinalIgnoreCase)))
            return $"istek başka bir kaynaktan geldi ({origin})";
        if (anahtar is null || beklenenAnahtar.Length == 0 ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(anahtar), Encoding.UTF8.GetBytes(beklenenAnahtar)))
            return "kurulum anahtarı geçersiz — sayfayı yenileyip tekrar deneyin";
        return null;
    }
}
