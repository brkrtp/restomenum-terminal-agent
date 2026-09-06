using Microsoft.Extensions.Configuration;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <c>--config-smoke</c>: cihaz config kanalını (session + GET /mapping + If-None-Match) canlı uca karşı
/// dener ve ÇIKAR. Cihaz anahtarına/terminale/dinleyiciye DOKUNMAZ — tam DI'dan ÖNCE kısa devre, o yüzden
/// yönetici gerekmez. Yalnız <c>Agent:DeviceConfigSetup</c> (base64{url,secret}) + ağ ister. Duman testi.
/// </summary>
public static class WindowsConfigSmoke
{
    public static async Task<bool> RunAsync(IConfiguration config)
    {
        var setup = DeviceConfigSetupParser.TryParse(config["Agent:DeviceConfigSetup"]);
        if (setup is null)
        {
            Console.WriteLine("HATA: Agent:DeviceConfigSetup yok ya da geçersiz (base64{url,secret} bekleniyor).");
            return false;
        }
        Console.WriteLine($"== CONFIG SMOKE == taban: {setup.BaseUri}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var client = new DeviceConfigClient(http, setup.BaseUri, setup.Secret,
            log: (m, d) => Console.WriteLine($"  {m}"));

        var r1 = await client.FetchMappingAsync(currentVersion: null);
        switch (r1)
        {
            case MappingFetchResult.Updated up:
                Console.WriteLine($"[fetch #1] Updated — version={up.Mapping.Version} " +
                    $"dept={up.Mapping.Departments.Count} entry={up.Mapping.Entries.Count} pm={up.Mapping.PaymentMethods.Count}");
                // İkinci çekme: If-None-Match ile 304 bekle (aynı sürüm değişmediyse).
                var r2 = await client.FetchMappingAsync(currentVersion: up.Mapping.Version);
                Console.WriteLine($"[fetch #2 If-None-Match v{up.Mapping.Version}] {Ad(r2)}  (304=NotModified beklenir)");
                return true;
            case MappingFetchResult.NotModified:
                Console.WriteLine("[fetch #1] NotModified (304) — beklenmedik (ilk çekmede If-None-Match yok).");
                return true;
            case MappingFetchResult.AuthFailed af:
                Console.WriteLine($"[fetch #1] AuthFailed — {af.Detail} (sır yanlış/geçersiz?)");
                return false;
            case MappingFetchResult.Failed f:
                Console.WriteLine($"[fetch #1] Failed — {f.Detail}");
                return false;
            default:
                return false;
        }
    }

    private static string Ad(MappingFetchResult r) => r switch
    {
        MappingFetchResult.NotModified => "NotModified",
        MappingFetchResult.Updated up => $"Updated(v{up.Mapping.Version})",
        MappingFetchResult.AuthFailed af => $"AuthFailed({af.Detail})",
        MappingFetchResult.Failed f => $"Failed({f.Detail})",
        _ => "?",
    };
}
