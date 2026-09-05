using System.Security.Cryptography;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host;

/// <summary>
/// ⚠️ <b>YALNIZ GELİŞTİRME.</b> Bellekte üretilmiş bir EC anahtarı — süreç her başladığında
/// <b>değişir</b>, hiçbir yere kaydedilmez ve donanıma bağlı değildir.
///
/// <para><b>Neden var:</b> protokolü Windows makinesi ve TPM olmadan uçtan uca çalıştırabilmek
/// için. <b>Neden tehlikeli:</b> değişmez #9 ("özel anahtar OS güvenli deposunda, dışa
/// aktarılamaz") burada sağlanmaz — bu anahtarla çalışan bir cihazın kimliği kopyalanabilir.</para>
///
/// <para>Bu yüzden <see cref="Program"/> üretim ortamında bunun kullanılmasını <b>engeller</b>;
/// geliştirmede de her açılışta uyarı basılır. Sessizce zayıf yola düşmek, zayıf yolun kendisinden
/// daha tehlikelidir.</para>
/// </summary>
public sealed class DevDeviceKey : IDeviceKey, IDisposable
{
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public DevDeviceKey(string connectorId) => ConnectorId = connectorId;

    public string ConnectorId { get; }

    /// <summary>Donanıma bağlı DEĞİL — adı bunu açıkça söylüyor ki loglarda ayırt edilebilsin.</summary>
    public string Fingerprint => "dev-insecure-" +
        Convert.ToHexString(SHA256.HashData(_ecdsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();

    public byte[] Sign(byte[] data) => _ecdsa.SignData(data, HashAlgorithmName.SHA256);

    public void Dispose() => _ecdsa.Dispose();
}
