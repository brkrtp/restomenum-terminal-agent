using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Restomenum.Agent.Host;
using Restomenum.Agent.Host.Windows;

// Windows ÜRETİM giriş noktası. Temel yapılandırma çapraz-platform host'la ORTAK
// (HostComposition), üstüne donanıma bağlı gerçek kayıtlar (WindowsDeviceKey + GmpTerminalTransport)
// eklenir. Bu proje net8.0-windows'tur ve yalnız Windows'ta derlenir/çalışır.
var builder = Host.CreateApplicationBuilder(args);

HostComposition.AddAgentBaseServices(builder);   // önce temel (fail-closed varsayılanlar)
builder.Services.AddWindowsTerminal();           // sonra gerçek kayıtlar (varsayılanları geçersiz kılar)

await builder.Build().RunAsync();
