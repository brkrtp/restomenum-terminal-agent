using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>ELLE, açıkça tetiklenen</b> eşleştirme provizyonu — YALNIZ <c>--pair</c> argümanıyla çalışır,
/// normal (üretim) akışında ASLA. Neden ayrı: GMP eşleşmesi <b>tek-slot ve sürece bağlı</b> (canlı
/// ölçüldü) — işlemi yapacak binary kendi <c>FP3_StartPairingInit</c>'ini çağırmak zorunda; başka bir
/// süreç eşleşse o eşleşme bu sürece geçmez. Bu yüzden normal <see cref="AgentWorker"/> akışı eşleşmiş
/// cihaz VARSAYAR ve eşleştirme yapmaz. Bu adım operatörün bilinçli çalıştırdığı bir kesme.
///
/// <para><b>Sıra (sertifikalı StartPairing ile aynı):</b> Echo (bağlantı) → durum → StartPairingInit
/// (terminal eşleştirme menüsünde olmalı) → durum. Başarısızsa dinleyici AÇILMAZ. Başarılıysa çağıran
/// AYNI süreçte dinleyiciyi başlatır — eşleşme o süreçte tutulur.</para>
/// </summary>
public static class WindowsPairing
{
    public static bool Run(IServiceProvider services)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Pairing");
        var gmp = services.GetRequiredService<IGmpWrapper>();

        log.LogInformation("eşleştirme: Echo (bağlantı testi) — terminal GMP.XML'deki IP'de açık olmalı");
        var echo = gmp.Echo();
        if (!echo.Ok)
        {
            log.LogError("Echo BAŞARISIZ (rc={Rc}) — terminale ulaşılamıyor (IP/port/ağ?). Eşleşme YAPILMADI.", echo.Code);
            return false;
        }

        gmp.CheckPairing(out var once);
        log.LogInformation("eşleşme durumu (önce): {Durum}", once ? "EŞLİ" : "eşli DEĞİL");

        log.LogWarning("StartPairingInit çağrılıyor — terminal ŞU AN eşleştirme menüsünde OLMALI.");
        var rc = gmp.Pair(new GmpPairingConfig(ProcOrderNumber: "", EcrSerialNumber: ""), out var info);
        if (!rc.Ok)
        {
            log.LogError("EŞLEŞME BAŞARISIZ (rc={Rc}). Terminali eşleştirme menüsüne alıp tekrar çalıştırın (--pair).", rc.Code);
            return false;
        }

        gmp.CheckPairing(out var sonra);
        log.LogInformation("EŞLEŞME BAŞARILI — cihaz: {Brand}/{Model} seri={Serial} sürüm={Version}; durum(sonra): {Durum}",
            info.Brand, info.Model, info.Serial, info.Version, sonra ? "EŞLİ" : "eşli DEĞİL");
        if (!sonra)
            log.LogWarning("StartPairingInit Ok döndü ama IsGmpPairingDone hâlâ 'eşli değil' — satış öncesi dikkat.");

        // Config kanalı AÇIKSA (DeviceConfigClient DI'da) departman tablosunu bildir — cihaz ARTIK eşli,
        // GetDepartments/GetTaxRates okunabilir (S3: rapor eşleşmeden SONRA). Bildirim başarısız olsa da
        // eşleşme geçerli (satış yolu ayrı). Config kanalı kapalıysa (yerel dosya modu) atlanır.
        var configClient = services.GetService<DeviceConfigClient>();
        if (configClient is not null)
            RaporDepartmanlar(gmp, info, log, configClient);

        return true;
    }

    /// <summary>GetDepartments + GetTaxRates → birleştir → config kanalına bildir. Oran tabloda yoksa null
    /// (uydurma yok). Rapor başarısızlığı eşleşmeyi/satışı ETKİLEMEZ — yalnız eklenti ekranı tabloyu geç görür.</summary>
    private static void RaporDepartmanlar(IGmpWrapper gmp, GmpDeviceInfo info, ILogger log, DeviceConfigClient client)
    {
        var drc = gmp.GetDepartments(out var deptJson);
        if (!drc.Ok) { log.LogWarning("GetDepartments başarısız (rc={Rc}) — departman tablosu bildirilemedi.", drc.Code); return; }
        var trc = gmp.GetTaxRates(out var taxJson);
        if (!trc.Ok) { log.LogWarning("GetTaxRates başarısız (rc={Rc}) — oranlar null bildirilecek.", trc.Code); taxJson = "[]"; }

        var departments = DeviceDepartmentsBuilder.FromGmp(deptJson, taxJson);
        var nullOran = departments.Count(d => d.TaxRateBasisPoints is null);
        try
        {
            var ok = client.ReportDepartmentsAsync(departments, info).GetAwaiter().GetResult();
            log.LogInformation("departman tablosu config kanalına bildirildi: {Adet} departman ({Null} oranı null), sonuç={Ok}",
                departments.Count, nullOran, ok);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "departman bildirimi hata verdi — eşleşme yine de geçerli, sonraki --pair'de tekrar denenir.");
        }
    }
}
