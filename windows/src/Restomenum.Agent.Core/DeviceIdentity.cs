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
    /// <summary>
    /// Kanonik dizeyi imzalar (Ed25519 / EC P-256 / RSA-PSS).
    ///
    /// <para><b>EC imzası DER kodlu olmak ZORUNDA</b> (ASN.1 <c>SEQUENCE{r,s}</c>), ham
    /// <c>r‖s</c> (IEEE P1363) <b>DEĞİL</b>. Sunucu <c>crypto.verify(null, …)</c> çağırıyor ve
    /// Node'un varsayılan <c>dsaEncoding</c>'i <c>der</c>. Ölçüldü: DER doğrulanıyor (71 bayt),
    /// P1363 reddediliyor (64 bayt).</para>
    ///
    /// <para><b>.NET tuzağı:</b> <c>ECDsaCng.SignData</c> P1363 üretir ve DER'e çevrilmesi gerekir.
    /// Java/Android'in <c>Signature</c>'ı zaten DER üretir. Yanlış kodlama sessiz kalmaz —
    /// <b>her</b> isteği reddeder — ama sebebi <c>unauthorized</c> göründüğü için sahada anahtar
    /// ya da parmak izi sorunu sanılır ve teşhisi zordur.</para>
    ///
    /// <para>Sözleşme: <c>conformance/signing.json</c> → <c>imzaKodlamasi</c>.</para>
    /// </summary>
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

    /// <summary>
    /// Açık anahtar, <b>SPKI PEM</b> (<c>-----BEGIN PUBLIC KEY-----</c>). Kayıt (enrollment)
    /// isteğinde bu değer gönderilir ve sunucu imzaları bununla doğrular.
    ///
    /// <para><b>Biçim pazarlık konusu değil:</b> sunucu <c>Connector.assertValidEnrollment</c>'ta
    /// PEM başlığını regex ile kontrol ediyor ve <c>crypto.verify</c>'a düz PEM dizesi geçiyor.
    /// Ham CNG blob'u ya da base64 DER göndermek <b>kayıt aşamasında</b> reddedilir — bu iyi
    /// haber: hata cihazın ilk kurulumunda çıkar, aylar sonra bir ödeme sırasında değil.</para>
    ///
    /// <para>.NET'te: <c>ECDsa.ExportSubjectPublicKeyInfoPem()</c> (net7+) ya da
    /// <c>ExportSubjectPublicKeyInfo()</c> + PEM sarmalama. CNG anahtarından üretmek için
    /// <c>ECDsaCng</c> örneği üzerinden dışa aktar; <b>özel anahtar dışa aktarılmaz</b>, yalnız açık
    /// kısım — <see cref="CngExportPolicies"/> kısıtı özel anahtara aittir.</para>
    /// </summary>
    string PublicKeyPem { get; }
}
