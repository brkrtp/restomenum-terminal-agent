using System.Net.Http.Headers;

namespace Restomenum.Agent.Core;

/// <summary>
/// <see cref="IPaymentDetailClient"/>'ın HTTP uygulaması — İNCE kabuk: Bearer ekler, GET atar,
/// ayrıştırmayı <see cref="PaymentDetailParser"/>'a bırakır (test edilebilirlik orada).
///
/// <para>Kimlik = cihaz oturum JWT'si (<see cref="ISessionProvider"/> → mevcut
/// <c>/v1/connectors/session</c> akışı; kayıt/anahtar/DER imza korunuyor). Install API key DEĞİL.</para>
///
/// <para><b>Platform erişilemezse ödeme başlatılamaz</b> — çevrimdışı ödeme YOK (bilinçli). Ağ
/// hatası <see cref="PaymentRejectReason.Unknown"/> reddi olur; terminal SÜRÜLMEZ.</para>
/// </summary>
public sealed class HttpPaymentDetailClient : IPaymentDetailClient
{
    private readonly HttpClient _http;
    private readonly ISessionProvider _sessions;
    private readonly Uri _baseUri;   // plugins API kök adresi (sonu "/"): https://plugins-….run.app/

    public HttpPaymentDetailClient(HttpClient http, ISessionProvider sessions, Uri baseUri)
    {
        _http = http;
        _sessions = sessions;
        _baseUri = baseUri;
    }

    public async Task<PaymentDetailResult> FetchAsync(string paymentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paymentId))
            return new PaymentDetailResult.Rejected(PaymentRejectReason.Unknown, "paymentId boş", 0);

        int status;
        string body;
        try
        {
            // Oturum token'ı + GET tek try'da: oturum ucu erişilemezse de "platform erişilemez"→sürme.
            var token = (await _sessions.AcquireAsync(ct)).Token;
            var uri = new Uri(_baseUri, $"plugin-api/payments/{Uri.EscapeDataString(paymentId)}");
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            status = (int)resp.StatusCode;
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new PaymentDetailResult.Rejected(PaymentRejectReason.Unknown, $"ağ/oturum hatası: {e.Message}", 0);
        }

        return PaymentDetailParser.Parse(status, body);
    }
}
