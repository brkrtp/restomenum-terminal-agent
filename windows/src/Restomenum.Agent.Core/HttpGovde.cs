namespace Restomenum.Agent.Core;

/// <summary>
/// HEAD yanıtında gövde OLAMAZ (RFC 9110 §9.3.2). HttpListener HEAD'e yazılan gövdeyi istisnayla
/// reddediyor: sahada (2026-10-14 15:12) 405 hiç ulaşmadı ve her HEAD "istek işlenemedi" ERR satırı
/// üretti. Yanıt yazan her yardımcı bu karara bakar.
/// </summary>
public static class HttpGovde
{
    public static bool Yazilir(string yontem) => !string.Equals(yontem, "HEAD", StringComparison.OrdinalIgnoreCase);
}
