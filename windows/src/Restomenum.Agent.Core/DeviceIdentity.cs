namespace Restomenum.Agent.Core;

/// <summary>
/// Cihazın kalıcı kimliği — **değişmez #9**: özel anahtar işletim sisteminin güvenli deposunda
/// durur ve <b>dışa aktarılamaz</b>.
///
/// <para><b>Neden arayüz:</b> gerçek uygulama Windows'ta CNG (dışa aktarılamaz anahtar), Android'de
/// Keystore kullanır. Çekirdek mantığın bunların hiçbirini bilmesine gerek yok ve bilmemesi sayesinde
/// bu makinede (macOS/Linux) derlenip test edilebiliyor.</para>
///
/// <para><b>İhlalin bedeli:</b> anahtar dışa aktarılabilirse restoranda kasa imajı kopyalandığında
/// klon makine aynı <c>connectorId</c> ile oturum açar ve iki makine aynı terminali sürer.</para>
/// </summary>
public interface IDeviceKey
{
    /// <summary>Kanonik dizeyi imzalar (Ed25519 / EC P-256 / RSA-PSS).</summary>
    byte[] Sign(byte[] data);

    /// <summary>
    /// Cihaz parmak izi — **kalıcı ve cihaza bağlı** olmalı.
    ///
    /// <para>Rastgele üretip bir dosyaya yazmayın: imaj kopyalandığında o da kopyalanır ve korumanın
    /// tamamı boşa çıkar. Donanıma bağlı bir değerden türetin.</para>
    /// </summary>
    string Fingerprint { get; }

    /// <summary>Bu cihazın enrollment sırasında aldığı kimlik.</summary>
    string ConnectorId { get; }
}
