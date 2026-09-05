using System.ComponentModel.DataAnnotations;

namespace Restomenum.Agent.Host;

/// <summary>
/// Agent yapılandırması. <b>Sır İÇERMEZ</b> (§14): cihazın özel anahtarı işletim sisteminin güvenli
/// deposunda yaşar ve buraya hiç uğramaz; burada yalnız hangi sunucuya bağlanılacağı ve hangi
/// kimliğin kullanılacağı yazılıdır.
///
/// <para>Değerler <c>appsettings.json</c>, ortam değişkeni ve komut satırından okunur — ortam
/// değişkeni dosyayı ezer, böylece dağıtımda dosyaya dokunmadan yönlendirme yapılabilir.</para>
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>Gateway WSS adresi, örn. <c>wss://payment-gateway…/v1/agent</c>.</summary>
    [Required] public string GatewayUrl { get; set; } = "";

    /// <summary>Oturum ucu, örn. <c>https://…/v1/connectors/session</c>.</summary>
    [Required] public string SessionUrl { get; set; } = "";

    /// <summary>Restoran kimliği.</summary>
    [Required] public string ServerId { get; set; } = "";

    /// <summary>Bu cihazın kayıt sırasında aldığı kimlik.</summary>
    [Required] public string ConnectorId { get; set; } = "";

    /// <summary>
    /// Dayanıklı store dosyası. Varsayılan <c>%ProgramData%</c> altındadır — kullanıcı profilinde
    /// tutmak, servis farklı bir hesapla çalıştığında <b>komut geçmişini görünmez kılar</b> ve
    /// çözülmemiş bir tahsilat sessizce kaybolur.
    /// </summary>
    public string? StorePath { get; set; }

    /// <summary>Agent sürümü — gateway'e bildirilir, saha teşhisinde tek ayırt edici.</summary>
    public string Version { get; set; } = "1.0.3";

    /// <summary>
    /// ⚠️ <b>YALNIZ GELİŞTİRME.</b> Donanım destekli anahtar yerine bellekte üretilmiş bir anahtar
    /// kullanır. Üretim ortamında açılması <b>engellenir</b> (bkz. <c>Program.cs</c>): açık
    /// bırakılırsa cihaz kimliği kopyalanabilir hâle gelir ve değişmez #9 çöker.
    /// </summary>
    public bool UseInsecureDevKey { get; set; }

    public string ResolveStorePath()
    {
        if (!string.IsNullOrWhiteSpace(StorePath)) return StorePath!;
        var kok = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(kok)) kok = Path.GetTempPath();
        var dizin = Path.Combine(kok, "Restomenum", "Agent");
        Directory.CreateDirectory(dizin);
        return Path.Combine(dizin, "agent.db");
    }
}
