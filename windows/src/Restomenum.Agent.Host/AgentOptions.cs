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

    /// <summary>
    /// Plugin API kök adresi (GET tutar + POST sonuç), örn. <c>https://plugins-….run.app/</c>.
    /// Yerel mimaride ajan tutarı buradan çeker ve sonucu buraya bildirir (Bearer = oturum JWT).
    /// </summary>
    [Required] public string PluginsApiUrl { get; set; } = "";

    /// <summary>
    /// Yerel HTTP dinleyici ön eki (HttpListener), örn. <c>http://127.0.0.1:7788/</c>. Kasa buraya
    /// <c>POST /nexo</c> ile SaleToPOIRequest yollar. Terminal kaydındaki <c>endpoint</c> ile eşleşmeli.
    /// </summary>
    public string ListenPrefix { get; set; } = "http://127.0.0.1:7788/";

    /// <summary>
    /// Yerel dinleyicinin kabul ettiği yol. Terminal kaydı <c>http://127.0.0.1:7788/nexo</c> ise <c>/nexo</c>.
    /// </summary>
    public string ListenPath { get; set; } = "/nexo";

    /// <summary>Oturum ucu, örn. <c>https://…/v1/connectors/session</c>.</summary>
    [Required] public string SessionUrl { get; set; } = "";

    /// <summary>Restoran kimliği.</summary>
    [Required] public string ServerId { get; set; } = "";

    /// <summary>
    /// Bu cihazın kayıt sırasında aldığı kimlik. <b><see cref="Required"/> DEĞİL:</b> ilk çalıştırmada
    /// YOKTUR — kayıt (enrollment) yanıtından doğar ve dayanıklı duruma (anahtarın yanı) yazılır,
    /// appsettings'e değil. Zorunlu tutmak, kaydolmak için gereken önyükleme kodunun daha çalışmadan
    /// host'u öldürürdü (A-için-B / B-için-A önyükleme kilidi).
    /// </summary>
    public string? ConnectorId { get; set; }

    /// <summary>
    /// Kayıt ucu, örn. <c>https://…/v1/connectors/enroll</c>. Yalnız İLK kurulumda gerekli.
    /// </summary>
    public string? EnrollUrl { get; set; }

    /// <summary>
    /// ⚠️ Tek kullanımlık kayıt kodu (TTL ~10 dk). Yalnız İLK çalıştırmada doldurulur; cihaz kayıtlı
    /// değilse <see cref="EnrollUrl"/> ile kaydolmak için kullanılır. Kayıttan sonra yok sayılır.
    /// <b>ASLA loglanmaz</b> — 10 dk boyunca cihaz bağlama yetkisi taşır, log satırı ondan uzun yaşar.
    /// </summary>
    public string? EnrollmentCode { get; set; }

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
