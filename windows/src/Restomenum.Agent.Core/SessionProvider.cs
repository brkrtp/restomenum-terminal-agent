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

    /// <summary>
    /// Elde tutulan jetonu ATAR (W52). Bir çağrı <c>401</c> alırsa jeton erken geçersiz olmuş
    /// demektir; çağıran bunu söyler, sağlayıcı bir sonraki <see cref="AcquireAsync"/>'te yeni
    /// jeton alır.
    ///
    /// <para><b>Varsayılan gövde BOŞ:</b> önbelleklemeyen uygulamalar (testlerdeki sahteler)
    /// için atacak bir şey yok. Zorunlu üye yapmak onlarca sahte uygulamayı kırardı ve
    /// kırılanların hiçbiri gerçek bir hata olmazdı.</para>
    /// </summary>
    void Invalidate() { }
}
