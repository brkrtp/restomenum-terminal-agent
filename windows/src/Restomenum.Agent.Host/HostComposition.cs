using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host;

/// <summary>
/// Platformdan BAĞIMSIZ temel DI kaydı. Hem çapraz-platform <c>Restomenum.Agent.Host</c>
/// (geliştirme/Mac) hem de Windows üretim kompozisyon kökü (<c>Restomenum.Agent.Host.Windows</c>)
/// bu tek metodu çağırır — böylece iki giriş noktası aynı temel yapılandırmayı paylaşır ve
/// biri değişince diğeri geride kalmaz (ödeme servisi için sürüklenme = risk).
///
/// <para><b>Donanıma bağlı kayıtlar burada YOK.</b> <see cref="IDeviceKey"/> ve
/// <see cref="ITerminalTransport"/> burada bilerek FAIL-CLOSED fırlatan varsayılanlarla kalır;
/// Windows kompozisyon kökü bunları gerçek uygulamalarla (WindowsDeviceKey / GmpTerminalTransport)
/// EN SON kaydederek geçersiz kılar (son <c>AddSingleton</c> kazanır). Bu proje net8.0'dır ve
/// net8.0-windows projelerine referans VERMEZ — Mac derlemesi bozulmasın diye.</para>
/// </summary>
public static class HostComposition
{
    public static void AddAgentBaseServices(HostApplicationBuilder builder)
    {
        // Windows'ta servis olarak çalışır (açılışta başlar, çökerse SCM yeniden başlatır).
        // Windows dışında sessizce hiçbir şey yapmaz — bu sayede aynı ikili geliştirmede de çalışır.
        builder.Services.AddWindowsService(o => o.ServiceName = "RestomenumAgent");

        builder.Services
            .AddOptions<AgentOptions>()
            .Bind(builder.Configuration.GetSection(AgentOptions.Section))
            .ValidateDataAnnotations()
            // Eksik yapılandırmayla BAŞLAMAZ (§15). Yarım yapılandırmayla açılıp ilk ödemede patlamak,
            // hatayı kasiyerin kart okuttuğu ana taşımak olurdu.
            .ValidateOnStart();

        builder.Services.Configure<HostOptions>(o =>
        {
            // KRİTİK: varsayılan 5 saniyedir. Kartlı ödeme 20–32 sn, belirsizlik kurtarması ~100 sn
            // sürüyor; 5 saniyede kesilen bir süreç, para terminalde hareket etmişken sonucu outbox'a
            // yazamaz ve tahsilat deftere hiç düşmez.
            o.ShutdownTimeout = TimeSpan.FromMinutes(3);
            // Bir arka plan işi patlarsa sessizce devam etmek yerine host durur ve SCM yeniden başlatır.
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        // Dayanıklı depolar TEK ÖRNEK ve DI'da. Worker kendi içinde açsaydı taşıma katmanı aynı
        // dosyaya erişemez ve ödeme öncesi fiş görüntüsü **diske yazılamazdı** — o zaman süreç kart
        // penceresinde öldüğünde çözülebilir bir vaka gereksiz yere insana çıkardı (§12.3).
        // İkisi aynı SQLite dosyasını kullanıyor; WAL bunu destekliyor ve her biri kendi kilidini tutuyor.
        builder.Services.AddSingleton<CommandStore>(sp =>
            CommandStore.Open(sp.GetRequiredService<IOptions<AgentOptions>>().Value.ResolveStorePath()));
        builder.Services.AddSingleton<Outbox>(sp =>
            Outbox.Open(sp.GetRequiredService<IOptions<AgentOptions>>().Value.ResolveStorePath()));
        // Taşıma katmanının gördüğü anlık görüntü deposu, komut deposunun ta kendisi.
        builder.Services.AddSingleton<ITicketSnapshotStore>(sp => sp.GetRequiredService<CommandStore>());

        // ── Yerel mimari: kimlik + HTTP istemcileri + orkestrasyon (K-21) ─────────────────
        // Saat ORTAK: HttpSessionProvider oturum yanıtından senkronlar, AgentOrchestrator IsExpired'da okur.
        builder.Services.AddSingleton<ClockOffset>();
        // Tek HttpClient (session/GET/notify hepsi hızlı; terminal sürüşü HTTP değil, GMP P/Invoke).
        builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

        builder.Services.AddSingleton<ISessionProvider>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return new HttpSessionProvider(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IDeviceKey>(),
                o.ServerId, new Uri(o.SessionUrl), sp.GetRequiredService<ClockOffset>());
        });
        builder.Services.AddSingleton<IPaymentDetailClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return new HttpPaymentDetailClient(sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<ISessionProvider>(), new Uri(o.PluginsApiUrl));
        });
        builder.Services.AddSingleton<IResultNotifier>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return new HttpResultNotifier(sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<ISessionProvider>(), new Uri(o.PluginsApiUrl));
        });
        // AgentOrchestrator ITerminalTransport'u TEMBEL çözer — pure Host'ta fırlatan varsayılan (fail-closed),
        // Windows'ta gerçek GmpTerminalTransport. Çekirdek (dedupe/durum-makinesi/UNKNOWN) korundu.
        builder.Services.AddSingleton(sp => new AgentOrchestrator(
            sp.GetRequiredService<CommandStore>(), sp.GetRequiredService<ITerminalTransport>(),
            sp.GetRequiredService<ClockOffset>()));
        builder.Services.AddSingleton<ILineDepartmentResolver>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Departments");
            var dir = string.IsNullOrEmpty(Path.GetDirectoryName(o.ResolveStorePath())) ? "." : Path.GetDirectoryName(o.ResolveStorePath())!;
            return new ConfigDepartmentResolver(
                Path.Combine(dir, "departments.json"), log,
                Path.Combine(dir, "department-rates.json"));   // cihaz departman→oran (§30.12 doğrulama)
        });
        builder.Services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("LocalSale");
            return new LocalSaleHandler(
                sp.GetRequiredService<IPaymentDetailClient>(), sp.GetRequiredService<AgentOrchestrator>(),
                sp.GetRequiredService<CommandStore>(), sp.GetRequiredService<ILineDepartmentResolver>(),
                sp.GetRequiredService<IResultNotifier>(), sp.GetRequiredService<Outbox>(),
                log: (m, d) => log.LogInformation("{Mesaj} {Detay}", m, d));
        });

        builder.Services.AddSingleton<IDeviceKey>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DeviceKey");
            var ortam = sp.GetRequiredService<IHostEnvironment>();

            if (opt.UseInsecureDevKey)
            {
                // FAIL-CLOSED: üretimde güvensiz anahtar ASLA. Bayrağın yanlışlıkla açık kalması,
                // sahadaki tüm cihazların kimliğini kopyalanabilir yapardı.
                if (!ortam.IsDevelopment())
                    throw new InvalidOperationException(
                        "Agent:UseInsecureDevKey yalnız geliştirme ortamında kullanılabilir. " +
                        "Üretimde donanım destekli anahtar (CNG/TPM) zorunludur.");
                log.LogWarning("⚠️ GÜVENSİZ GELİŞTİRME ANAHTARI kullanılıyor — donanıma bağlı DEĞİL, her açılışta değişir");
                return new DevDeviceKey(opt.ConnectorId ?? "");
            }

            // Donanım destekli anahtar ayrı bir Windows projesindedir (Restomenum.Agent.Windows).
            // Kayıtlı değilse BAŞLAMAYIZ: sessizce zayıf bir yola düşmek en tehlikeli sonuçtur.
            throw new InvalidOperationException(
                "Donanım destekli cihaz anahtarı kayıtlı değil. Windows'ta Restomenum.Agent.Windows " +
                "sağlayıcısını kaydedin ya da geliştirme için Agent:UseInsecureDevKey=true kullanın.");
        });

        builder.Services.AddSingleton<ITerminalTransport>(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Terminal");
            // Gerçek Ingenico taşıması `Restomenum.Agent.Gmp` projesindedir (IGmpWrapper uygulaması).
            // O kaydedilmemişse simülatöre düşmek YASAK: kart çekmediği hâlde "onaylandı" döndürürdü.
            throw new InvalidOperationException(
                "Terminal taşıması kayıtlı değil. Restomenum.Agent.Gmp sağlayıcısını kaydedin. " +
                "Simülatöre otomatik düşülmez — kart çekmeden 'onaylandı' dönmek kabul edilemez.");
        });

        builder.Services.AddHostedService<AgentWorker>();
    }
}
