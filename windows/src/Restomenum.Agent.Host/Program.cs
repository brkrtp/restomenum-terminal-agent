using Microsoft.Extensions.Hosting;
using Restomenum.Agent.Host;

// Çapraz-platform (geliştirme/Mac) giriş noktası. Temel yapılandırma HostComposition'da; donanıma
// bağlı IDeviceKey/ITerminalTransport BURADA KAYITLI DEĞİL — fail-closed varsayılanlar devrede.
// Gerçek terminal için Windows üretim kökü: Restomenum.Agent.Host.Windows (net8.0-windows).
var builder = Host.CreateApplicationBuilder(args);

HostComposition.AddAgentBaseServices(builder);

await builder.Build().RunAsync();
