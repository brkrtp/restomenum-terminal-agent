using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Restomenum.Agent.Core;
using Restomenum.Agent.Host;
using Restomenum.Agent.Host.Windows;

// Windows ÜRETİM giriş noktası. Temel yapılandırma çapraz-platform host'la ORTAK
// (HostComposition), üstüne donanıma bağlı gerçek kayıtlar (WindowsDeviceKey + GmpTerminalTransport)
// eklenir. Bu proje net8.0-windows'tur ve yalnız Windows'ta derlenir/çalışır.

// --pair: ELLE eşleştirme provizyonu; --void: ELLE açık-fiş temizliği (bakım). Yapılandırma
// sağlayıcısını bozmasın diye argümanlardan ayıklanır.
bool eslesModu = args.Contains("--pair");
bool voidModu = args.Contains("--void");
bool configSmoke = args.Contains("--config-smoke");
bool ticketModu = args.Contains("--ticket");
// --finish-ticket: baskı adımı patladığı için AÇIK kalmış, üzerinde GERÇEK TAHSİLAT olan fişi
// üreticinin kurtarma akışıyla (GMP3OlumsuzDurumYönetimi §2) TAMAMLAR. `--onayla` olmadan yalnız
// okur ve planı yazar. `VoidAll`/`VoidPayment` bu yolda YOK — para geri verilmez, fiş kapatılır.
bool finishTicketModu = args.Contains("--finish-ticket");
bool voidOnayi = args.Contains("--onayla");
bool paymentAppsModu = args.Contains("--payment-apps");
// --capability-probe: hangi ödeme tiplerini cihazın KABUL ettiğini ölçer (W57). MALİ FİŞ AÇAR —
// `--onayla` olmadan yalnız ne yapacağını yazar.
bool capabilityProbeModu = args.Contains("--capability-probe");
// --discount-probe: indirimin cihaz sözleşmesini ölçer (W60). MALİ FİŞ AÇAR — `--onayla` şart.
bool discountProbeModu = args.Contains("--discount-probe");
// --replay-ticket-cancel <saleSessionId> <ticketId> <serviceId> <poiId> [ticketCancelId]:
// kalıcı reddedilmiş bir fiş iptali bildirimini yeniden gönderir (W69). CİHAZA DOKUNMAZ.
var replayIdx = Array.IndexOf(args, "--replay-ticket-cancel");
var replayArgs = replayIdx >= 0
    ? args.Skip(replayIdx + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray()
    : Array.Empty<string>();
// --retract <commandId>: daha önce onaylı bildirilmiş bir komutun düzeltmesini platforma yollar.
var retractIdx = Array.IndexOf(args, "--retract");
string? retractKomut = retractIdx >= 0 && retractIdx + 1 < args.Length ? args[retractIdx + 1] : null;
// --cancel-ticket [saleSessionId]: operatör fiş iptali — KASANIN YOLUNDAN (bildirim dahil).
var cancelIdx = Array.IndexOf(args, "--cancel-ticket");
bool cancelModu = cancelIdx >= 0;
string? cancelOturum = cancelIdx >= 0 && cancelIdx + 1 < args.Length
    && !args[cancelIdx + 1].StartsWith("--", StringComparison.Ordinal) ? args[cancelIdx + 1] : null;
// --check-tax <dosya>: KURU PROVA — cihaza dokunmadan hangi kalemin reddedileceğini söyler.
var taxIdx = Array.IndexOf(args, "--check-tax");
string? taxDosya = taxIdx >= 0 && taxIdx + 1 < args.Length ? args[taxIdx + 1] : null;
var configArgs = args
    .Where(a => a != "--pair" && a != "--void" && a != "--config-smoke" && a != "--ticket"
             && a != "--finish-ticket"
             && a != "--onayla" && a != "--payment-apps" && a != "--capability-probe"
             && a != "--discount-probe" && a != "--replay-ticket-cancel" && !replayArgs.Contains(a)
             && a != "--retract" && a != retractKomut
             && a != "--cancel-ticket" && a != cancelOturum
             && a != "--check-tax" && a != taxDosya)
    .ToArray();

var builder = Host.CreateApplicationBuilder(configArgs);

// --config-smoke: config kanalını (session+mapping) canlı uca dener ve ÇIKAR. Cihaz anahtarı/terminal/
// dinleyici YOK — tam DI'dan ÖNCE kısa devre, yönetici gerekmez. Yalnız Agent:DeviceConfigSetup + ağ.
if (configSmoke)
{
    Environment.ExitCode = await WindowsConfigSmoke.RunAsync(builder.Configuration) ? 0 : 1;
    return;
}

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
    Environment.ExitCode = WindowsVoidTicket.Run(host.Services, voidOnayi) ? 0 : 1;
    return;
}

// TEŞHİS (--ticket): fiş durumunu OKUR ve çıkar. Hiçbir şeyi değiştirmez — "önce ölç, sonra
// müdahale et" için gereken tek okuma. `--void` ile karıştırılmasın diye ayrı bayrak.
if (ticketModu)
{
    Environment.ExitCode = WindowsVoidTicket.Report(host.Services) ? 0 : 1;
    return;
}

// KURTARMA (--finish-ticket): baskı yarıda kaldığı için AÇIK kalmış, ÜZERİNDE PARA olan fişi
// tamamlar. `--onayla` yoksa KURU PROVA (okur, planı yazar, cihaza baskı yapmaz).
// `--void`den AYRI tutuldu bilerek: `--void`in niyeti parayı geri vermek, bunun niyeti korumak.
if (finishTicketModu)
{
    Environment.ExitCode = WindowsFinishTicket.Run(host.Services, voidOnayi) ? 0 : 1;
    return;
}

// TEŞHİS (--payment-apps): cihazda kurulu ödeme (banka) uygulamalarını HAM okur ve çıkar.
// Hiçbir şeyi değiştirmez.
if (paymentAppsModu)
{
    Environment.ExitCode = WindowsPaymentApps.Run(host.Services) ? 0 : 1;
    return;
}

// YETENEK YOKLAMASI (--capability-probe): hangi ödeme tiplerini cihazın KABUL ettiğini ölçer.
// ⚠️ MALİ FİŞ AÇAR (yazdırmadan iptal eder, ama FNo tüketebilir) — bu yüzden `--onayla` şart;
// onaysız çağrı yalnız planı yazar ve cihaza DOKUNMAZ.
if (capabilityProbeModu)
{
    Environment.ExitCode = WindowsCapabilityProbe.Run(host.Services, voidOnayi) ? 0 : 1;
    return;
}

// İNDİRİM YOKLAMASI (--discount-probe): `promotion` alanının cihaz sözleşmesini ölçer.
// ⚠️ MALİ FİŞ AÇAR (yazdırmadan iptal eder, ama FNo tüketir) — `--onayla` şart.
if (discountProbeModu)
{
    Environment.ExitCode = WindowsDiscountProbe.Run(host.Services, voidOnayi) ? 0 : 1;
    return;
}

// FİŞ İPTALİ YENİDEN GÖNDERİM (--replay-ticket-cancel): platform 400 ile reddettiği için
// deftere yazılamamış bir iptali yeniden bildirir. CİHAZA DOKUNMAZ, FNo YAKMAZ.
if (replayIdx >= 0)
{
    Environment.ExitCode = await WindowsReplayTicketCancel.RunAsync(host.Services, replayArgs, voidOnayi) ? 0 : 1;
    return;
}

// BAKIM (--retract <commandId>): hatalı bir ONAYI platformda geri çeker. Cihaza DOKUNMAZ.
// Karar insanda: W14'ten sonra hayaletin tekrar oluşması için bilinen bir yol yok, dolayısıyla
// güvenilir bir otomatik tetikleyici de yok. Uydurma tetikleyici doğru onayları bozardı.
if (retractKomut is not null)
{
    var h = host.Services.GetRequiredService<LocalSaleHandler>();
    Environment.ExitCode = await h.RetractLandedAsync(retractKomut) ? 0 : 1;
    return;
}

// KURU PROVA (--check-tax): cihaza HİÇ dokunmaz, eşleştirme YAPMAZ. Satışın çalıştırdığı
// çözümleyicinin ve oran kuralının AYNISINI kuru kuruya işletir — "prova geçti ama satış
// reddetti" olamasın diye kural ikinci kez yazılmadı.
if (taxDosya is not null)
{
    Environment.ExitCode = await WindowsTaxCheck.RunAsync(host.Services, taxDosya) ? 0 : 1;
    return;
}

// OPERATÖR FİŞ İPTALİ (--cancel-ticket): kasanın gönderemediği durumlarda açık fişi KASANIN
// YOLUNDAN iptal eder — cihaz + DAYANIKLI bildirim. `--void`'dan farkı: defter de düzelir.
// `--onayla` olmadan yalnız ne iptal edileceğini yazar.
if (cancelModu)
{
    Environment.ExitCode = await WindowsCancelTicket.RunAsync(host.Services, cancelOturum, voidOnayi) ? 0 : 1;
    return;
}

// EŞLEŞTİRME YALNIZ --pair ile: eşleşme tek-slot + sürece bağlı olduğundan bu binary kendi
// StartPairingInit'ini çağırmalı; başarılıysa AYNI süreçte dinleyici başlar (eşleşme o süreçte tutulur).
// Normal (üretim) akış bu adımı ATLAR ve eşleşmiş cihaz VARSAYAR. Eşleşme başarısız olsa BİLE dinleyici
// AÇILIR (GET zinciri çalışsın, kasa bloke olmasın; eşleşmesiz satış zaten güvenli-UNKNOWN'a düşer) —
// operatör logda "EŞLEŞME BAŞARISIZ" görür ve terminali menüye alıp --pair'i tekrarlar.
if (eslesModu) WindowsPairing.Run(host.Services);

await host.RunAsync();
