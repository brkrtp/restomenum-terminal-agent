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
    public static bool Run(IServiceProvider services, bool operatorOnayi = false)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("VoidTicket");
        var gmp = services.GetRequiredService<IGmpWrapper>();

        // EŞLEŞME SÜRECE BAĞLI (canlı ölçüldü: süreç bitince eşleşme DÜŞER, Start → 0xF020 = 61472
        // PAIRING_REQUIRED). Start/VoidAll'dan ÖNCE bu süreç kendi eşleşmesini kurmalı. Eşleştirme
        // Echo'yu da yapıyor; ayrıca Echo çağırmaya gerek yok.
        log.LogInformation("açık-fiş temizliği: eşleşme kuruluyor (süreç-bağlı, önce şart)...");
        if (!WindowsPairing.Run(services))
        {
            log.LogError("eşleşme kurulamadı — açık fiş temizlenemedi. (Terminal ulaşılabilir mi?)");
            return false;
        }

        // FP3_Start probe: AlreadyDone → handle AÇIK FİŞİN tanıtıcısı.
        var startRc = gmp.Start(out var handle);

        if (startRc.Code == GmpCodes.AlreadyDone)
        {
            log.LogWarning("terminalde AÇIK fiş var (handle={Handle}) — önce İÇERİĞİ okunuyor.", handle);

            // ── KANITLA TEMİZLİK: önce OKU, sonra sil ────────────────────────────────────
            // `VoidAll`'ın 2069 koruması var ama o koruma "iptal edilemez" der, "ne vardı" demez.
            // Sildiğimiz şeyin ne olduğunu bilmeden silmek, üzerinde tahsilat olan bir fişi yok
            // etme riskidir. `OptionFlags(Reload)` şart: tek başına `GetTicket` ödeme detayını
            // eksik döndürebiliyor ve kararı tam o alana dayandırıyoruz.
            var of = gmp.OptionFlags(handle, GmpEchoFlags.Reload);
            var gt = gmp.GetTicket(handle, out var fis);
            if (!of.Ok || !gt.Ok)
            {
                log.LogError("fiş OKUNAMADI (OptionFlags={Of}, GetTicket={Gt}) — içeriği bilinmeyen fiş " +
                    "İPTAL EDİLMEZ. Operatöre bırakıldı.", of.Code, gt.Code);
                try { gmp.Close(handle); } catch { /* en iyi çaba */ }
                return false;
            }

            log.LogInformation("açık fiş içeriği: toplam={Toplam} tahsil={Tahsil} ödemeSayısı={Sayi} " +
                "sonÖdemeTipi={Tip} bankaBacağı={Banka}",
                fis.TotalAmountMinor, fis.PaidAmountMinor, fis.PaymentCount, fis.LastPaymentType, fis.HasBankLeg);

            // ── SERT TABAN: TAHSİL EDİLMİŞ PARA VARSA ASLA ────────────────────────────────
            // Bu kural `--onayla` ile DE gevşemez. Üzerinde para toplanmış bir fişi iptal etmek
            // banka ters işlemi ister (`VoidPayment`) ve o yol sahada hiç ölçülmedi; burada
            // denemek, geri alınamaz bir kaybı bir bakım adımına yıkmak olurdu.
            if (fis.PaidAmountMinor > 0)
            {
                log.LogError("fişte TAHSİL EDİLMİŞ PARA var (tahsil={Tahsil}) — İPTAL EDİLMEZ, " +
                    "onay bayrağı bunu değiştirmez. Banka ters işlemi gerekir.", fis.PaidAmountMinor);
                try { gmp.Close(handle); } catch { /* en iyi çaba */ }
                return false;
            }

            // Ödeme SAYACI var ama TAHSİLAT yok (sayaç 1 / tutar 0). Cihazın kendi defteri "para
            // toplanmadı" diyor ama sayaç kıpırdadığı için otomatik yol bunu BİLEREK belirsiz sayar
            // (§31.3): çelişkili okuma, tahminle silinmez.
            //
            // `--onayla` tam olarak bu boşluğu insana açar: rapor ekrana basılır, kararı operatör
            // verir. Otomatik yolu gevşetmeden, sıkışan kasayı da kilitli bırakmadan.
            if (fis.PaymentCount != 0 && !operatorOnayi)
            {
                log.LogError("fişte ödeme SAYACI var (sayı={Sayi}, tahsil={Tahsil}, bankaBacağı={Banka}) — " +
                    "otomatik İPTAL EDİLMEZ. Tahsilat 0 görünüyorsa ve bu fişi iptal etmek istiyorsanız " +
                    "komutu `--void --onayla` ile tekrar çalıştırın (kararı siz vermiş olursunuz).",
                    fis.PaymentCount, fis.PaidAmountMinor, fis.HasBankLeg);
                try { gmp.Close(handle); } catch { /* en iyi çaba */ }
                return false;
            }

            if (fis.PaymentCount != 0)
            {
                log.LogWarning("OPERATÖR ONAYI ile iptal ediliyor: ödeme sayacı {Sayi} ama TAHSİLAT 0. " +
                    "Denetim izi: toplam={Toplam} tahsil={Tahsil} sonÖdemeTipi={Tip} bankaBacağı={Banka}",
                    fis.PaymentCount, fis.TotalAmountMinor, fis.PaidAmountMinor, fis.LastPaymentType, fis.HasBankLeg);
            }

            log.LogWarning("fişte ödeme YOK (kanıtlandı) — VoidAll ile iptal ediliyor.");
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

    /// <summary>
    /// <b>SALT-OKUNUR</b> fiş durumu raporu (<c>--ticket</c>). Hiçbir şeyi iptal etmez, kapatmaz
    /// (yalnız kendi açtığı yoklama fişini kapatır — açık bırakmak bir sonraki satışı bozardı).
    ///
    /// <para>Var olma sebebi: 2026-09-07'de terminal saatlerce açık fiş yüzünden kilitliydi ve
    /// durumu görmenin tek yolu <c>--void</c> çalıştırmaktı — yani <b>ölçmek için değiştirmek</b>
    /// gerekiyordu. Bu, para taşıyan bir cihazda kabul edilemez.</para>
    /// </summary>
    public static bool Report(IServiceProvider services)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("TicketReport");
        var gmp = services.GetRequiredService<IGmpWrapper>();

        if (!WindowsPairing.Run(services))
        {
            log.LogError("eşleşme kurulamadı — fiş durumu okunamadı. (Terminal ulaşılabilir mi?)");
            return false;
        }

        var startRc = gmp.Start(out var handle);

        if (startRc.Ok)
        {
            gmp.Close(handle);   // yoklama fişi bırakılmaz
            log.LogInformation("FIS DURUMU: açık fiş YOK — terminal satışa hazır.");
            return true;
        }

        if (startRc.Code != GmpCodes.AlreadyDone)
        {
            log.LogError("FIS DURUMU OKUNAMADI (Start rc={Rc}).", startRc.Code);
            return false;
        }

        var of = gmp.OptionFlags(handle, GmpEchoFlags.Reload);
        var gt = gmp.GetTicket(handle, out var fis);
        if (!of.Ok || !gt.Ok)
        {
            log.LogError("FIS DURUMU: AÇIK FİŞ VAR ama içeriği OKUNAMADI " +
                "(OptionFlags={Of}, GetTicket={Gt}).", of.Code, gt.Code);
            return false;
        }

        log.LogWarning("FIS DURUMU: AÇIK FİŞ VAR — toplam={Toplam} tahsil={Tahsil} ödemeSayısı={Sayi} " +
            "sonÖdemeTipi={Tip} bankaBacağı={Banka} → temizlenebilir mi: {Temiz}",
            fis.TotalAmountMinor, fis.PaidAmountMinor, fis.PaymentCount, fis.LastPaymentType,
            fis.HasBankLeg, fis.PaymentCount == 0 ? "EVET (ödeme yok)" : "HAYIR (ödeme var)");
        return true;
    }
}
