using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>ELLE tetiklenen açık-fiş temizliği</b> — YALNIZ <c>--void</c> argümanıyla. Başarısız/yarım bir
/// satış terminalde ÖDENMEMİŞ açık fiş bırakabilir; sonraki satış üstüne açamaz (<c>2080 AlreadyDone</c>).
/// Bu bakım adımı o fişi <c>VoidAll</c> ile iptal eder.
///
/// <para><b>Tanıtıcı kurtarma (sertifikalı DLLController.GetTicket ile birebir):</b> yeni süreçte fişin
/// tanıtıcısı bellekte yoktur. <c>FP3_Start</c> probe'u açık fiş varsa <c>ALREADY_DONE</c> döner ve
/// <c>handle</c>'ı AÇIK FİŞİN tanıtıcısıyla doldurur (<see cref="GmpWrapper.Start"/> hTrx'i her durumda
/// yansıtır). O tanıtıcıyla <c>VoidAll</c> → <c>Close</c>. Açık fiş yoksa probe yeni fiş açar; onu
/// kapatırız (açık bırakmak bir sonraki satışı bozardı).</para>
///
/// <para><b>Ödenmiş fiş:</b> <c>VoidAll</c> <c>2069</c> dönerse fişte TAHSİL EDİLMİŞ ödeme var — kart
/// bacağı gerçek banka ters işlemi ister (<c>VoidPayment</c>) ve bu tek satırlık bir bakım adımının işi
/// değildir. Böyle bir durumda yüksek sesle dur, körlemesine void deneme.</para>
/// </summary>
public static class WindowsVoidTicket
{
    public static bool Run(IServiceProvider services)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("VoidTicket");
        var gmp = services.GetRequiredService<IGmpWrapper>();

        log.LogInformation("açık-fiş temizliği: Echo (bağlantı testi)");
        var echo = gmp.Echo();
        if (!echo.Ok)
        {
            log.LogError("Echo BAŞARISIZ (rc={Rc}) — terminale ulaşılamıyor (IP/port/eşleşme?).", echo.Code);
            return false;
        }

        // FP3_Start probe: AlreadyDone → handle AÇIK FİŞİN tanıtıcısı.
        var startRc = gmp.Start(out var handle);

        if (startRc.Code == GmpCodes.AlreadyDone)
        {
            log.LogWarning("terminalde AÇIK fiş var (handle={Handle}) — VoidAll ile iptal ediliyor.", handle);
            var vr = gmp.VoidAll(handle, out _);
            if (!vr.Ok)
            {
                if (vr.Code == GmpCodes.PaymentFound)
                    log.LogError("VoidAll 2069 — fişte TAHSİL EDİLMİŞ ödeme var; banka ters işlemi (VoidPayment) gerekir, " +
                        "tek satırlık bakım adımının işi değil. İptal edilmedi.");
                else if (vr.Code == GmpCodes.CannotVoid)
                    log.LogError("VoidAll 2357 — fiş mali hafızada, artık VoidAll ile iptal edilemez.");
                else
                    log.LogError("VoidAll BAŞARISIZ (rc={Rc}).", vr.Code);
                try { gmp.Close(handle); } catch { /* en iyi çaba */ }
                return false;
            }
            gmp.Close(handle);
            log.LogInformation("✓ AÇIK FİŞ İPTAL EDİLDİ (VoidAll ok, Close ok). Terminal temiz — satışa hazır.");
            return true;
        }

        if (startRc.Ok)
        {
            // Açık fiş yoktu; probe yeni bir fiş AÇTI — açık bırakmak sonraki satışı bozar, kapat.
            gmp.Close(handle);
            log.LogInformation("terminalde açık fiş YOK — temizlenecek bir şey yok (probe fişi kapatıldı).");
            return true;
        }

        log.LogError("fiş durumu okunamadı (Start rc={Rc}) — terminal meşgul ya da eşleşme yok olabilir.", startRc.Code);
        return false;
    }
}
