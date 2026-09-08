using System.Net.Http.Headers;
using System.Text;

namespace Restomenum.Agent.Core;

/// <summary>
/// <see cref="IResultNotifier"/>'ın HTTP uygulaması — sonuç/ilerleme gövdesini platforma POST eder.
/// İnce kabuk: Bearer ekler, POST atar, ayrıştırmayı <see cref="ResultNotifyParser"/>'a bırakır.
///
/// <para>Ağ hatası <see cref="NotifyOutcome.NetworkError"/> döner (outbox'ta kalır, replay). Kimlik =
/// cihaz oturum JWT (<see cref="ISessionProvider"/>) — GET ile AYNI. URL'deki paymentId gövdedeki
/// <c>SaleTransactionID.TransactionID</c> ile eşleşmeli (çağıran ikisini tutarlı vermeli).</para>
/// </summary>
public sealed class HttpResultNotifier : IResultNotifier
{
    private readonly HttpClient _http;
    private readonly ISessionProvider _sessions;
    private readonly Uri _baseUri;

    public HttpResultNotifier(HttpClient http, ISessionProvider sessions, Uri baseUri)
    {
        _http = http;
        _sessions = sessions;
        _baseUri = baseUri;
    }

    public Task<NotifyResult> NotifyTicketCancelAsync(string bodyJson, CancellationToken ct = default) =>
        GonderAsync("plugin-api/payments/ticket-cancel/result", bodyJson, ct);

    public Task<NotifyResult> NotifyTicketClosedAsync(string bodyJson, CancellationToken ct = default) =>
        GonderAsync("plugin-api/payments/ticket-closed", bodyJson, ct);

    public Task<NotifyResult> NotifyAsync(string paymentId, string bodyJson, CancellationToken ct = default) =>
        GonderAsync($"plugin-api/payments/{Uri.EscapeDataString(paymentId)}/result", bodyJson, ct);

    private async Task<NotifyResult> GonderAsync(string yol, string bodyJson, CancellationToken ct)
    {
        int status;
        string body;
        // W48: iki ayak AYRI ölçülüyor. Tek toplam sayı, yavaşlığın oturum alımında mı POST'ta mı
        // olduğunu söylemiyor ve teşhisi yanlış yere gönderiyor (2026-09-08: `sureMs=3004`
        // görüldü, POST sanıldı, hepsi oturum alımıydı).
        var kronometre = System.Diagnostics.Stopwatch.StartNew();
        long? oturumMs = null, postMs = null;
        try
        {
            // Oturum + POST tek try'da: oturum ucu erişilemezse NetworkError → outbox'ta kalır, replay.
            var token = (await _sessions.AcquireAsync(ct)).Token;
            oturumMs = kronometre.ElapsedMilliseconds;
            (status, body) = await PostAsync(yol, bodyJson, token, ct);
            postMs = kronometre.ElapsedMilliseconds - oturumMs;

            // ── W52: 401 → TEK yenileme + TEK tekrar ────────────────────────────────
            // Jeton önbellekli; uç onu bizden önce geçersiz sayabilir. Tek yenileme + tek
            // tekrar: fırtına YOK. İkinci 401 olduğu gibi ayrıştırıcıya gider.
            // ⚠️ Süreler tekrarı DA kapsayacak şekilde yeniden okunuyor — "bir tur sürdü" diye
            // raporlamak, iki tur sürmüşken teşhisi yanıltırdı.
            if (status == 401)
            {
                _sessions.Invalidate();
                var oturum2Bas = kronometre.ElapsedMilliseconds;
                var token2 = (await _sessions.AcquireAsync(ct)).Token;
                oturumMs += kronometre.ElapsedMilliseconds - oturum2Bas;
                var post2Bas = kronometre.ElapsedMilliseconds;
                (status, body) = await PostAsync(yol, bodyJson, token2, ct);
                postMs += kronometre.ElapsedMilliseconds - post2Bas;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Hangi ayakta düştüğü, `oturumMs`'in dolu olup olmamasından okunur: null ise oturum
            // alınamadı ve POST'a HİÇ gelinmedi — o yüzden `postMs` null KALIR (0 yazmak
            // "denendi, anında bitti" derdi ki yanlış).
            if (oturumMs is null) oturumMs = kronometre.ElapsedMilliseconds;
            else postMs = kronometre.ElapsedMilliseconds - oturumMs;
            return new NotifyResult(NotifyOutcome.NetworkError, null, null, 0,
                $"ağ/oturum hatası: {e.Message}", SessionMs: oturumMs, PostMs: postMs);
        }

        return ResultNotifyParser.Parse(status, body) with { SessionMs = oturumMs, PostMs = postMs };
    }

    /// <summary>Tek POST denemesi. Jeton BAŞLIKTA kalır — hiçbir log satırına girmez.</summary>
    private async Task<(int Status, string Body)> PostAsync(string yol, string bodyJson, string token,
        CancellationToken ct)
    {
        var uri = new Uri(_baseUri, yol);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }
}
