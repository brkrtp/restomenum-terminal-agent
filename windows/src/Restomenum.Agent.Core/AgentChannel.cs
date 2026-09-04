namespace Restomenum.Agent.Core;

/// <summary>Session ucundan alınan kimlik (§4.2).</summary>
public sealed record SessionToken(string Token, int ExpiresInSec, long ServerTime);

/// <summary>
/// Session JWT sağlayıcısı. Gerçek uygulama <c>POST /v1/connectors/session</c> çağırır ve
/// <see cref="SessionSigning.CanonicalString"/> ile imzalar; testte sahte olabilir.
/// </summary>
public interface ISessionProvider
{
    Task<SessionToken> AcquireAsync(CancellationToken ct = default);
}

/// <summary>
/// Tel katmanı soyutlaması. <b>Gerçek soket olmadan protokolü test edebilmek için var:</b> el
/// sıkışma, ACK sırası ve kapanma kodu davranışları donanım ya da ağ gerektirmeden doğrulanabilir.
/// </summary>
public interface IAgentChannel : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct = default);
    Task SendAsync(string json, CancellationToken ct = default);
    /// <summary>Sıradaki frame; bağlantı kapandıysa <c>null</c>.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct = default);
    Task CloseAsync(int code, string reason, CancellationToken ct = default);
    /// <summary>Uzak tarafın kapatma kodu — yeniden bağlanma stratejisi buna göre seçilir.</summary>
    int? CloseCode { get; }
}

/// <summary>Gateway kapatma kodları (§3.4).</summary>
public static class CloseCodes
{
    public const int Unauthorized = 4401;
    public const int SessionRevoked = 4403;
    public const int HelloTimeout = 4408;
    public const int Replaced = 4409;
    public const int Draining = 4503;

    /// <summary>
    /// Yeniden bağlanılmalı mı? <b><c>4403</c> için HAYIR</b> — oturum iptal edilmiş, cihaz devre
    /// dışı bırakılmıştır. Yeniden denemek, kapatılmış bir cihazın sonsuza kadar kapı çalması olurdu.
    /// </summary>
    public static bool ShouldReconnect(int? code) => code != SessionRevoked;

    /// <summary>
    /// Bu kapanış <b>bizim hatamız değil</b>: gateway kapanıyor ya da bu soket eskimiş. Geri çekilme
    /// sayacı sıfırlanır, yoksa sağlıklı bir devir teslim cezalandırılıp gecikme büyür.
    /// </summary>
    public static bool IsBenign(int? code) => code == Draining || code == Replaced;
}
