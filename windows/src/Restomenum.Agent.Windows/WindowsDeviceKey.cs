using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Windows;

/// <summary>
/// <see cref="IDeviceKey"/>'in Windows uygulaması (değişmez #9).
///
/// <para><b>Anahtar — dışa aktarılamaz, iki katman:</b></para>
/// <list type="number">
///   <item><b>TPM 2.0 varsa:</b> anahtar <c>Microsoft Platform Crypto Provider</c>'da üretilir
///   (<see cref="CngExportPolicies.None"/>). Özel anahtar fiziksel olarak TPM'den çıkamaz; imaj
///   klonlandığında yeni makinenin TPM'i o anahtara sahip değildir → klon <see cref="Sign"/> yapamaz.
///   "Oku ve karşılaştır" bir parmak izinden kategorik olarak güçlüdür: delinecek bir kontrol yoktur.</item>
///   <item><b>TPM yoksa:</b> <c>Microsoft Software KSP</c> + <see cref="CngExportPolicies.None"/>
///   (OS-içi dışa aktarılamaz). Bu anahtar diskte DPAPI ile durur, ham klon taşıyabilir — bu yüzden
///   klon TESPİTİ <see cref="Fingerprint"/>'e düşer.</item>
/// </list>
///
/// <para><b>Parmak izi — donanıma bağlı:</b> SMBIOS System UUID + BaseBoard Serial'den türetilir
/// (<c>GetSystemFirmwareTable</c>, WMI DEĞİL — winmgmt kilitli restoran PC'lerinde kapalı/bozuk olabilir).
/// Bu değerler BIOS'ta durur, disk imajının parçası değildir → farklı donanımda farklı çıkar. Enrollment'ta
/// sunucuya kaydedilir; aynı anahtarı sunan ama farklı parmak izi taşıyan klon reddedilir.</para>
///
/// <para><b>Hangi yolda olduğumuz LOGLANIR</b> — bir cihazın sessizce zayıf yola (yazılım KSP / pubkey
/// parmak izi) düşmesi görünmez kalmamalı.</para>
/// </summary>
public sealed class WindowsDeviceKey : IDeviceKey, IDisposable
{
    private const string KeyName = "Restomenum.Agent.DeviceKey.v1";
    private const string PlatformProvider = "Microsoft Platform Crypto Provider"; // TPM
    private const string SoftwareProvider = "Microsoft Software Key Storage Provider";

    private readonly CngKey _key;
    private readonly bool _tpmBacked;
    private readonly string _connectorIdPath;
    private readonly Action<string>? _log;

    private string? _fingerprint;   // tembel + tek sefer
    private string? _publicKeyPem;  // tembel + tek sefer

    /// <param name="log">Yol seçimi ve zayıf-yol uyarıları buraya yazılır (isteğe bağlı ama önerilir).</param>
    /// <param name="connectorIdPath">
    /// connectorId'nin saklandığı dosya. Varsayılan: %ProgramData%\RestoMenum\Agent\connector.id.
    /// connectorId sır DEĞİL (kimliktir); onu koruyan şey anahtardır. Enrollment <see cref="SetConnectorId"/>
    /// ile yazar.
    /// </param>
    public WindowsDeviceKey(Action<string>? log = null, string? connectorIdPath = null)
    {
        _log = log;
        _connectorIdPath = connectorIdPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RestoMenum", "Agent", "connector.id");

        (_key, _tpmBacked) = OpenOrCreateKey();
        _log?.Invoke(_tpmBacked
            ? "[DeviceKey] Anahtar TPM'de (Platform Crypto Provider) — dışa aktarılamaz, klonlanamaz."
            : "[DeviceKey] UYARI: TPM yok — anahtar Yazılım KSP'de. Klon koruması SMBIOS parmak izine bağlı.");
    }

    // ── Anahtar açılışı: önce TPM, olmazsa yazılım ─────────────────────────────
    private (CngKey key, bool tpm) OpenOrCreateKey()
    {
        // 1) TPM'de mevcut anahtarı aç
        if (TryOpen(PlatformProvider, out var k)) return (k!, true);
        // 2) TPM'de yoksa üretmeyi dene
        if (TryCreate(PlatformProvider, out k)) return (k!, true);
        // 3) TPM yok/erişilemez → yazılım KSP'de aç/üret
        if (TryOpen(SoftwareProvider, out k)) return (k!, false);
        if (TryCreate(SoftwareProvider, out k)) return (k!, false);

        throw new CryptographicException(
            "Cihaz anahtarı ne TPM ne de Yazılım KSP'de açılabildi/üretilebildi.");
    }

    private bool TryOpen(string provider, out CngKey? key)
    {
        key = null;
        try
        {
            if (!CngKey.Exists(KeyName, new CngProvider(provider), CngKeyOpenOptions.MachineKey))
                return false;
            key = CngKey.Open(KeyName, new CngProvider(provider), CngKeyOpenOptions.MachineKey);
            return true;
        }
        catch (CryptographicException) { return false; } // sağlayıcı yok / erişim yok
        catch (PlatformNotSupportedException) { return false; }
    }

    private bool TryCreate(string provider, out CngKey? key)
    {
        key = null;
        try
        {
            var p = new CngKeyCreationParameters
            {
                Provider = new CngProvider(provider),
                // ⚠️ Değişmez #9'un kalbi: anahtar HİÇBİR biçimde dışa aktarılamaz.
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
                // Makine anahtarı: servis LocalSystem olarak çalışır, kullanıcıdan bağımsız kalıcı olmalı.
                KeyCreationOptions = CngKeyCreationOptions.MachineKey,
            };
            key = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, p);
            return true;
        }
        catch (CryptographicException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
    }

    // ── IDeviceKey ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Kanonik dizeyi imzalar. <b>EC P-256 / ECDSA, SHA-256, DER (Rfc3279DerSequence) biçimi.</b>
    ///
    /// <para>DER ZORUNLU, P1363 DEĞİL — sunucu Node <c>crypto.verify</c> varsayılanı (<c>dsaEncoding: "der"</c>)
    /// DER bekliyor; ölçüldü: P1363 (64 bayt) → doğrulama <c>false</c>, DER (~71 bayt) → <c>true</c>.
    /// Android <c>Signature</c> zaten DER üretir, yani iki platform tek biçimde buluşur. .NET varsayılanı
    /// P1363 olduğu için burada AÇIKÇA DER isteniyor — yoksa her istek sessizce <c>unauthorized</c> olurdu.</para>
    /// </summary>
    public byte[] Sign(byte[] data)
    {
        using var ecdsa = new ECDsaCng(_key);
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    public string Fingerprint => _fingerprint ??= ComputeFingerprint();

    /// <summary>
    /// Açık anahtar, <b>SPKI PEM</b> (<c>-----BEGIN PUBLIC KEY-----</c>). Kayıt isteğinde gönderilir;
    /// sunucu imzaları bununla doğrular ve <c>assertValidEnrollment</c>'ta PEM başlığını regex'le kontrol eder.
    /// Yalnız AÇIK kısım dışa aktarılır — <see cref="CngExportPolicies.None"/> kısıtı ÖZEL anahtara aittir,
    /// açık kısmı dışa aktarmak her zaman serbesttir.
    /// </summary>
    public string PublicKeyPem => _publicKeyPem ??= ExportPublicKeyPem();

    private string ExportPublicKeyPem()
    {
        using var ecdsa = new ECDsaCng(_key);
        return ecdsa.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>
    /// Enrollment'ta alınan kimlik. Henüz enroll edilmemişse boş dizedir.
    /// </summary>
    public string ConnectorId
    {
        get
        {
            try
            {
                return File.Exists(_connectorIdPath)
                    ? File.ReadAllText(_connectorIdPath).Trim()
                    : "";
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }
    }

    /// <summary>
    /// Enrollment sonucu gelen connectorId'yi kalıcı olarak yazar. (Arayüz dışı — enrollment akışı çağırır.)
    /// </summary>
    public void SetConnectorId(string connectorId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_connectorIdPath)!);
        File.WriteAllText(_connectorIdPath, connectorId.Trim());
    }

    // ── Parmak izi: SMBIOS (donanım), yoksa pubkey (zayıf, loglanır) ──────────────
    private string ComputeFingerprint()
    {
        string uuid = "", baseboard = "";
        try
        {
            var smbios = ReadSmbios();
            if (smbios is not null)
            {
                uuid = ExtractSystemUuid(smbios) ?? "";
                baseboard = ExtractBaseboardSerial(smbios) ?? "";
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[DeviceKey] SMBIOS okunamadı: {ex.GetType().Name}: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(uuid) || !string.IsNullOrWhiteSpace(baseboard))
        {
            var material = $"smbios-uuid:{uuid}|baseboard-sn:{baseboard}";
            return "hw:" + Hash(Encoding.UTF8.GetBytes(material));
        }

        // Zayıf yol: SMBIOS boş/erişilemez. TPM anahtarı varsa pubkey hardware-bound sayılır;
        // yazılım KSP'de ise bu GERÇEK bir zayıflıktır — mutlaka loglanır.
        _log?.Invoke(_tpmBacked
            ? "[DeviceKey] UYARI: SMBIOS boş — parmak izi TPM public anahtarından türetiliyor (kabul edilebilir)."
            : "[DeviceKey] KRİTİK: SMBIOS boş VE TPM yok — parmak izi klon-dayanıklı DEĞİL. Donanım incelensin.");
        byte[] pub = _key.Export(CngKeyBlobFormat.EccPublicBlob); // public export her zaman serbest
        return "pk:" + Hash(pub);
    }

    private static string Hash(byte[] data)
    {
        byte[] h = SHA256.HashData(data);
        // base64url — dosya/URL güvenli
        return Convert.ToBase64String(h).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public void Dispose() => _key.Dispose();

    // ═══ SMBIOS okuma + ayrıştırma (GetSystemFirmwareTable, WMI yok) ═══════════════

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature,
        uint firmwareTableID, byte[]? pFirmwareTableBuffer, uint bufferSize);

    private const uint RSMB = 0x52534D42; // 'RSMB' (little-endian 'B''M''S''R')

    /// <summary>Ham SMBIOS tablosunu döndürür (8 baytlık RawSMBIOSData başlığı atlanmış).</summary>
    private static byte[]? ReadSmbios()
    {
        uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        uint written = GetSystemFirmwareTable(RSMB, 0, buf, size);
        if (written == 0 || written > size) return null;

        // RawSMBIOSData: [0]=Used20CallingMethod [1]=Major [2]=Minor [3]=DmiRevision [4..7]=Length, sonra tablo
        if (written <= 8) return null;
        int tableLen = (int)Math.Min(written - 8, buf.Length - 8);
        var table = new byte[tableLen];
        Array.Copy(buf, 8, table, 0, tableLen);
        return table;
    }

    // Type 1 (System Information): UUID, formatlı alanda offset 0x08, 16 bayt.
    private static string? ExtractSystemUuid(byte[] t)
    {
        foreach (var (type, start, formattedLen) in EnumerateStructures(t))
        {
            if (type != 1 || formattedLen < 0x08 + 16) continue;
            var b = new byte[16];
            Array.Copy(t, start + 0x08, b, 0, 16);
            // Tümü 0x00 veya 0xFF ise geçersiz (UUID atanmamış).
            if (b.All(x => x == 0x00) || b.All(x => x == 0xFF)) return null;
            // SMBIOS UUID ilk üç grubu little-endian saklar (WMI ile aynı gösterim için çevrilir).
            return FormatUuid(b);
        }
        return null;
    }

    // Type 2 (Baseboard): Serial Number, offset 0x07'deki string indeksi.
    private static string? ExtractBaseboardSerial(byte[] t)
    {
        foreach (var (type, start, formattedLen) in EnumerateStructures(t))
        {
            if (type != 2 || formattedLen < 0x08) continue;
            byte strIndex = t[start + 0x07];
            var s = GetString(t, start + formattedLen, strIndex);
            if (!string.IsNullOrWhiteSpace(s) && !IsPlaceholder(s)) return s.Trim();
        }
        return null;
    }

    // Her SMBIOS yapısı: [0]=type [1]=length(formatlı alan) [2..3]=handle, sonra formatlı alan,
    // sonra çift-null ile biten string bölgesi. (type, formatlıAlanBaşı, formatlıAlanUzunluğu) döner.
    private static IEnumerable<(byte type, int start, int len)> EnumerateStructures(byte[] t)
    {
        int i = 0;
        while (i + 4 <= t.Length)
        {
            byte type = t[i];
            byte len = t[i + 1];
            if (len < 4 || i + len > t.Length) yield break;
            if (type == 127) yield break; // End-of-table

            yield return (type, i, len);

            // formatlı alanı atla, sonra string bölgesini çift-null'a kadar geç
            int p = i + len;
            while (p + 1 < t.Length && !(t[p] == 0 && t[p + 1] == 0)) p++;
            p += 2; // çift-null'ı atla
            if (p <= i) yield break;
            i = p;
        }
    }

    // SMBIOS string tablosundan 1-tabanlı indeksle string oku (formatlı alanın hemen sonrası).
    private static string? GetString(byte[] t, int stringsStart, int index)
    {
        if (index == 0) return null;
        int p = stringsStart, cur = 1;
        while (p < t.Length)
        {
            int end = p;
            while (end < t.Length && t[end] != 0) end++;
            if (cur == index) return Encoding.ASCII.GetString(t, p, end - p);
            if (end + 1 < t.Length && t[end] == 0 && t[end + 1] == 0) return null; // bölge sonu
            p = end + 1;
            cur++;
        }
        return null;
    }

    private static bool IsPlaceholder(string s)
    {
        var v = s.Trim();
        // OEM'lerin sık bıraktığı anlamsız değerler — parmak izine katma.
        return v.Length == 0
            || v.Equals("None", StringComparison.OrdinalIgnoreCase)
            || v.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            || v.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase)
            || v.All(c => c == '0')
            || v.All(c => c == 'F' || c == 'f');
    }

    private static string FormatUuid(byte[] b)
    {
        // İlk üç grup little-endian → big-endian çevir (SMBIOS spec + WMI gösterimi).
        Span<byte> o = stackalloc byte[16];
        b.CopyTo(o);
        (o[0], o[3]) = (o[3], o[0]);
        (o[1], o[2]) = (o[2], o[1]);
        (o[4], o[5]) = (o[5], o[4]);
        (o[6], o[7]) = (o[7], o[6]);
        return string.Concat(
            Convert.ToHexString(o[0..4]), "-",
            Convert.ToHexString(o[4..6]), "-",
            Convert.ToHexString(o[6..8]), "-",
            Convert.ToHexString(o[8..10]), "-",
            Convert.ToHexString(o[10..16])).ToUpperInvariant();
    }
}
