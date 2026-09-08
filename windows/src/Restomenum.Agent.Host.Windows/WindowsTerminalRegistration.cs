using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Restomenum.Agent.Core;
using Restomenum.Agent.Gmp;
using Restomenum.Agent.Host;
using Restomenum.Agent.Windows;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// Windows üretim kompozisyon kökü: donanıma bağlı gerçek uygulamaları kaydeder ve
/// <see cref="HostComposition"/>'ın fail-closed fırlatan varsayılanlarını geçersiz kılar
/// (son <c>AddSingleton</c> kazanır). Bu tip net8.0-windows'a özgüdür; çapraz-platform host
/// bu dosyayı GÖRMEZ, o yüzden Mac derlemesi bozulmaz.
/// </summary>
public static class WindowsTerminalRegistration
{
    /// <summary>
    /// Sıra ÖNEMLİ: <see cref="HostComposition.AddAgentBaseServices"/>'ten SONRA çağır ki bu
    /// gerçek kayıtlar temeldeki fırlatan varsayılanların yerine geçsin.
    /// </summary>
    public static void AddWindowsTerminal(this IServiceCollection services)
    {
        // ── Terminal sarmalayıcısı (sertifika sınırının kendisi) ─────────────────────────
        services.AddSingleton<IGmpWrapper, GmpWrapper>();

        // ── Cihaz anahtarı: donanım (TPM/CNG) ya da geliştirme anahtarı ───────────────────
        // Temeldeki fırlatan IDeviceKey'i geçersiz kılar. UseInsecureDevKey mantığı KORUNUR:
        // yalnız fark, üretim dalında fırlatmak yerine gerçek WindowsDeviceKey dönmesidir.
        services.AddSingleton<IDeviceKey>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var env = sp.GetRequiredService<IHostEnvironment>();
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DeviceKey");

            if (opt.UseInsecureDevKey)
            {
                if (!env.IsDevelopment())
                    throw new InvalidOperationException(
                        "Agent:UseInsecureDevKey yalnız geliştirme ortamında kullanılabilir. " +
                        "Üretimde donanım destekli anahtar (TPM/CNG) zorunludur.");
                log.LogWarning("⚠️ GÜVENSİZ GELİŞTİRME ANAHTARI — donanıma bağlı DEĞİL, her açılışta değişir");
                return new DevDeviceKey(opt.ConnectorId ?? "");
            }

            // WindowsDeviceKey: önce TPM Platform Crypto Provider, yoksa SMBIOS/Software'e düşer
            // ve log uyarısı basar. connectorId'yi kalıcılaştırır — cihaz KİMLİĞİ veridir, yapılandırma
            // değil: anahtarın yanında (ResolveStorePath dizini) dursun ki birlikte yedeklensin/silinsin
            // ve güncellemede kaybolmasın (appsettings'e YAZILMAZ).
            // NOT: CngKey.Create MachineKey izni ister — host'u Windows SERVİSİ (LocalSystem) olarak
            // çalıştır; düz kullanıcı oturumunda anahtar üretimi izinsiz kalıp başarısız olabilir.
            var storeDir = Path.GetDirectoryName(opt.ResolveStorePath());
            var connectorIdPath = string.IsNullOrEmpty(storeDir) ? null : Path.Combine(storeDir, "connector.id");
            return new WindowsDeviceKey(log: m => log.LogInformation("{Msg}", m), connectorIdPath: connectorIdPath);
        });

        // ── Terminal taşıması (sıra/kurtarma/hata yorumu — sertifika DIŞI) ────────────────
        // Temeldeki fırlatan ITerminalTransport'u geçersiz kılar. Departman eşlemesi ARTIK agent'ta
        // DEĞİL: FiscalLine.DepartmentNo'yu eklenti çözüp gönderiyor (3e013ed) — IDepartmentMap kalktı.
        services.AddSingleton<ITerminalTransport>(sp =>
        {
            var gmp = sp.GetRequiredService<IGmpWrapper>();
            // Kalıcı anlık görüntü deposu DI'dan (HostComposition'da CommandStore olarak kayıtlı).
            // null DEĞİL: ödeme öncesi fiş görüntüsü diske yazılır, kurtarma gereksiz insana çıkmaz.
            var snapshots = sp.GetRequiredService<ITicketSnapshotStore>();
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Terminal");
            return new GmpTerminalTransport(
                gmp, snapshots,
                log: (m, a) => log.LogInformation("{Msg} {Arg}", m, a));
        });

        // ── W56: cihaz bölüm tablosunun periyodik tazelenmesi ─────────────────────────────
        // Yalnız GMP'nin OLDUĞU yerde kayıtlı: platform-nötr Host bu tipi görmez, yoklayıcı onu
        // `GetService` ile İSTEĞE BAĞLI çözer ve yoksa eskisi gibi sadece platformdan çeker.
        services.AddSingleton<DeviceIdentityHolder>();
        // Cihaz kilidi satışınkinin TA KENDİSİ: ikinci bir kilit "cihaz başına tek işlem"i iki ayrı
        // yerde tanımlar ve bakım turu satışın ortasına girerdi.
        services.AddSingleton<IDeviceIdleGate>(sp => sp.GetRequiredService<LocalSaleHandler>());
        services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("DeviceDepartments");
            return new DeviceDepartmentRefresher(
                sp.GetRequiredService<IGmpWrapper>(), sp.GetRequiredService<DeviceConfigClient>(),
                sp.GetRequiredService<IDeviceMappingStore>(), sp.GetRequiredService<IDeviceIdleGate>(),
                sp.GetRequiredService<DeviceIdentityHolder>(),
                log: (m, d) => log.LogInformation("{Mesaj} {Detay}", m, d));
        });
    }
}
