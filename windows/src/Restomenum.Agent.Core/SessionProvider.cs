namespace Restomenum.Agent.Core;

/// <summary>
/// Session ucundan alınan cihaz kimliği (§4.2). <b>Yerel mimaride de KORUNUR</b> — GET tutar ve
/// sonuç bildirimi çağrılarının Bearer'ı budur (kayıt/anahtar/DER imza akışı değişmedi).
/// </summary>
public sealed record SessionToken(string Token, int ExpiresInSec, long ServerTime);

/// <summary>
/// Session JWT sağlayıcısı. Gerçek uygulama <c>POST /v1/connectors/session</c> çağırır ve
/// <see cref="SessionSigning.CanonicalString"/> ile imzalar (DER); testte sahte olabilir.
/// </summary>
public interface ISessionProvider
{
    Task<SessionToken> AcquireAsync(CancellationToken ct = default);
}
