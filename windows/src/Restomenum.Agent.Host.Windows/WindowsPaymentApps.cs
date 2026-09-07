using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>SALT-OKUNUR</b> teşhis (<c>--payment-apps</c>): cihazda KURULU ödeme (banka) uygulamalarını
/// okur ve <b>ham JSON'u olduğu gibi</b> basar.
///
/// <para><b>Neden yorumlamıyoruz:</b> <c>ST_PAYMENT_APPLICATION_INFO</c>'nun <c>Status</c>,
/// <c>AppType</c>, <c>AppFlag</c> alanlarının anlamı ne üretici belgesinde ne de kodda tanımlı.
/// Bugün bir kez bu türden bir varsayım denendi ve ölçüm onu çürüttü (cihazın ödeme hata alanında
/// "iletilmedi" beklerken "NO RESPONSE" çıktı). Bu yüzden burada tek iş ölçmek; yorumu, ölçüm
/// elde olduktan sonra birlikte yaparız.</para>
///
/// <para>Hiçbir şeyi değiştirmez: fiş açmaz, ödeme yapmaz.</para>
/// </summary>
public static class WindowsPaymentApps
{
    public static bool Run(IServiceProvider services)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("PaymentApps");
        var gmp = services.GetRequiredService<IGmpWrapper>();

        // Eşleşme süreç-bağlı: bu süreç kendi eşleşmesini kurmadan cihaz konuşmaz.
        if (!WindowsPairing.Run(services))
        {
            log.LogError("eşleşme kurulamadı — kurulu uygulamalar okunamadı.");
            return false;
        }

        var rc = gmp.GetPaymentApplicationsRaw(out var json, out var total, out var received);
        if (rc.Code != GmpCodes.Ok)
        {
            log.LogError("FP3_GetPaymentApplicationInfo BAŞARISIZ (rc={Rc}) — toplam={Toplam} alınan={Alinan}",
                rc, total, received);
            return false;
        }

        log.LogWarning("PAYMENT APPS: toplamKayit={Toplam} alinanKayit={Alinan}", total, received);
        log.LogWarning("PAYMENT APPS HAM JSON >>> {Json}", json);
        return true;
    }
}
