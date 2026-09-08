namespace Restomenum.Agent.Core;

/// <summary>
/// Reddedilen bir yerel isteğin <b>kaynağını</b> tek satırda tarif eder (W45).
///
/// <para><b>Neden gerekti (ölçüm 2026-09-08 18:57:53):</b> ajana `/nexo` yoluna geçerli JSON ama
/// nexo zarfı olmayan bir POST geldi ve reddedildi. "Kim gönderdi?" sorusunun cevabı YOKTU —
/// reddetme satırı yalnız <c>Reason</c>/<c>Detail</c> yazıyordu. Kabul edilen istekler için aynı
/// boşluk W18'de kapatılmıştı; reddedilenler dışarıda kalmış.</para>
///
/// <para><b>Gövde BURAYA GİRMEZ.</b> Bu metodun gövde parametresi yok — dolayısıyla gövde
/// loglanamaz. İstek gövdesi kart/kişisel veri taşıyabilir ve reddedilen bir gövde de taşıyabilir
/// (biçimi bozuk olması içeriğinin zararsız olduğunu göstermez).</para>
///
/// <para><b>"Veri yok" ile "boş" AYRI:</b> başlık hiç gelmediyse <c>(yok)</c>, boş geldiyse
/// <c>(boş)</c>. İkisini birleştirmek "istemci UA göndermiyor" ile "istemci UA'yı boş gönderiyor"
/// ayrımını yok ederdi — teşhiste bu ayrım tam da aranan şey.</para>
/// </summary>
public static class IstekKaynagi
{
    /// <param name="uzakUc">
    /// <c>HttpListenerRequest.RemoteEndPoint</c> metni (IP:port). Dinleyici loopback'e bağlıysa
    /// bu daima 127.0.0.1'dir ve <b>port</b> ayırt edici olan tek alandır (süreç eşlemesi için).
    /// </param>
    /// <param name="userAgent"><c>User-Agent</c> başlığı; yoksa <c>null</c>.</param>
    /// <param name="contentLength">
    /// <c>Content-Length</c>; başlık yoksa <c>HttpListener</c> <b>-1</b> verir. -1 "bilinmiyor"
    /// demek, 0 "gövde boş" demek — ayrı yazılır.
    /// </param>
    public static string Tanimla(string? uzakUc, string? userAgent, long contentLength)
        => $"uzakUç={Alan(uzakUc)} ua={Alan(userAgent)} uzunluk={(contentLength < 0 ? "(yok)" : contentLength.ToString())}";

    private static string Alan(string? s) => s is null ? "(yok)" : s.Length == 0 ? "(boş)" : s;
}
