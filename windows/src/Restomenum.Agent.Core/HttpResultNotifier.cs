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

    public async Task<NotifyResult> NotifyAsync(string paymentId, string bodyJson, CancellationToken ct = default)
    {
        int status;
        string body;
        try
        {
            // Oturum + POST tek try'da: oturum ucu erişilemezse NetworkError → outbox'ta kalır, replay.
            var token = (await _sessions.AcquireAsync(ct)).Token;
            var uri = new Uri(_baseUri, $"plugin-api/payments/{Uri.EscapeDataString(paymentId)}/result");
            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            status = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new NotifyResult(NotifyOutcome.NetworkError, null, null, 0, $"ağ/oturum hatası: {e.Message}");
        }

        return ResultNotifyParser.Parse(status, body);
    }
}
