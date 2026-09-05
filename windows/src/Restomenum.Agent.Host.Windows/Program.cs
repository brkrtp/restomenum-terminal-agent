using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Restomenum.Agent.Host;
using Restomenum.Agent.Host.Windows;

// Windows ÜRETİM giriş noktası. Temel yapılandırma çapraz-platform host'la ORTAK
// (HostComposition), üstüne donanıma bağlı gerçek kayıtlar (WindowsDeviceKey + GmpTerminalTransport)
// eklenir. Bu proje net8.0-windows'tur ve yalnız Windows'ta derlenir/çalışır.

// --pair: ELLE eşleştirme provizyonu; --void: ELLE açık-fiş temizliği (bakım). Yapılandırma
// sağlayıcısını bozmasın diye argümanlardan ayıklanır.
bool eslesModu = args.Contains("--pair");
bool voidModu = args.Contains("--void");
var configArgs = args.Where(a => a != "--pair" && a != "--void").ToArray();

var builder = Host.CreateApplicationBuilder(configArgs);

HostComposition.AddAgentBaseServices(builder);   // önce temel (fail-closed varsayılanlar)
builder.Services.AddWindowsTerminal();           // sonra gerçek kayıtlar (varsayılanları geçersiz kılar)

var host = builder.Build();

// KAYIT, OTURUMDAN ÖNCE: cihaz kayıtlı değilse tek kullanımlık kodla kaydol ve connectorId'yi
// dayanıklı yaz. AgentWorker (oturum/gateway) buradan SONRA, RunAsync ile başlar; yani session
// _key.ConnectorId'yi zaten dolu görür. Kayıt başarısızsa host HİÇ çalışmaz (fail-closed).
await WindowsEnrollment.EnsureEnrolledAsync(host.Services);

// BAKIM (--void): terminaldeki açık/yarım fişi VoidAll ile iptal et ve ÇIK — dinleyici AÇILMAZ.
// Başarısız bir satış ödenmemiş açık fiş bırakabilir; sonraki satış üstüne açamaz (2080).
if (voidModu)
{
    Environment.ExitCode = WindowsVoidTicket.Run(host.Services) ? 0 : 1;
    return;
}

// EŞLEŞTİRME YALNIZ --pair ile: eşleşme tek-slot + sürece bağlı olduğundan bu binary kendi
// StartPairingInit'ini çağırmalı; başarılıysa AYNI süreçte dinleyici başlar (eşleşme o süreçte tutulur).
// Normal (üretim) akış bu adımı ATLAR ve eşleşmiş cihaz VARSAYAR. Eşleşme başarısız olsa BİLE dinleyici
// AÇILIR (GET zinciri çalışsın, kasa bloke olmasın; eşleşmesiz satış zaten güvenli-UNKNOWN'a düşer) —
// operatör logda "EŞLEŞME BAŞARISIZ" görür ve terminali menüye alıp --pair'i tekrarlar.
if (eslesModu) WindowsPairing.Run(host.Services);

await host.RunAsync();
