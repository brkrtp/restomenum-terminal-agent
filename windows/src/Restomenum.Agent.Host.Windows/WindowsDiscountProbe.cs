using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>W60 — indirim yoklamasının Windows kabuğu</b> (<c>--discount-probe</c>). Ölçümün kendisi
/// <see cref="DiscountProbe"/>'da (çekirdek, testli); burada yalnız eşleşme, operatör onayı ve çıktı.
///
/// <para><b>⚠️ MALİ FİŞ AÇAR</b> (yazdırmadan iptal eder, ama bir mali fiş numarası tüketir —
/// 2026-09-09'da üç kez ölçüldü). <c>--onayla</c> ŞART; onaysız çağrı yalnız planı yazar.</para>
/// </summary>
public static class WindowsDiscountProbe
{
    public static bool Run(IServiceProvider services, bool operatorOnayi = false)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("DiscountProbe");

        log.LogWarning("İNDİRİM YOKLAMASI — ölçülecek sorular: (1) `promotion` dolu ItemSale kabul " +
            "ediliyor mu; (2) fiş toplamı NET mi BRÜT mü; (3) indirim KDV MATRAHINI düşürüyor mu; " +
            "(4) `IndexOfItem=0` ara toplam indirimi yapıyor mu (Minus/Dec); (5) tam ikram (990→0) geçer mi.");
        // Şablon YER TUTUCU SAYISI kadar argüman ister (CA2017) ve aynı adı tekrar etmek de sayılır;
        // sabitler burada bir kez okunup düz metne çevriliyor — yapısal alan olarak taşınmalarının
        // bir faydası yok, bunlar derleme zamanı sabitleri.
        log.LogWarning("Plan (TEK fiş): [1] indirimsiz {Tutar} kuruşluk referans kalem → [2] aynı tutar " +
            "+ promotion {Ind} → [3] Minus({Ind}, indeks 0) → [4] Dec(%{Oran}, indeks 0) → [5] tam ikram " +
            "(tutar + eşit promotion) → VoidAll+Close.",
            DiscountProbe.ItemAmountMinor, DiscountProbe.DiscountAmountMinor,
            DiscountProbe.DiscountAmountMinor, DiscountProbe.DiscountRatePercent);
        log.LogWarning("YAZDIRMA YOK — kâğıda mali fiş çıkmaz. Ama fiş AÇILDIĞI için bir mali fiş " +
            "numarası (FNo) TÜKETİR; 2026-09-09'da üç kez ölçüldü.");

        if (!operatorOnayi)
        {
            log.LogWarning("KURU PROVA — `--onayla` verilmedi, cihaza DOKUNULMADI. " +
                "Çalıştırmak için: --discount-probe --onayla");
            return true;
        }

        // Eşleşme süreç-bağlı: bu süreç kendi eşleşmesini kurmadan cihaz konuşmaz.
        if (!WindowsPairing.Run(services))
        {
            log.LogError("eşleşme kurulamadı — yoklama yapılamadı.");
            return false;
        }

        var rapor = DiscountProbe.Run(services.GetRequiredService<IGmpWrapper>(),
            (m, d) => log.LogWarning("{Mesaj} {Detay}", m, d));

        if (rapor.Status == ProbeStatus.TicketAlreadyOpen)
        {
            log.LogError("terminalde AÇIK fiş var — yoklama YAPILMADI. Önce o fişi sonuçlandırın " +
                "(`--ticket` ile bakın, gerekirse `--void`).");
            return false;
        }
        if (rapor.Status == ProbeStatus.SetupFailed)
        {
            log.LogError("yoklama fişi açılamadı: {Adim} rc={Rc} — hiçbir soru cevaplanmadı.",
                rapor.FailedStep, rapor.FailedRc);
            return false;
        }
        if (rapor.Status == ProbeStatus.HandleLost)
            log.LogError("tanıtıcı ölçüm sırasında geçersizleşti ({Adim} rc={Rc}) — KALAN ADIMLAR ÖLÇÜLMEDİ.",
                rapor.FailedStep, rapor.FailedRc);

        if (!rapor.CleanedUp)
            log.LogError("⚠️ TEMİZLİK TAM DEĞİL (VoidAll={V}, Close={C}) — terminalde fiş kalmış olabilir. " +
                "`--ticket` ile bakın.", rapor.VoidAllRc, rapor.CloseRc);

        log.LogWarning("═══ İNDİRİM YOKLAMASI SONUCU (ham sayılar; yorum YOK) ═══");
        log.LogWarning("  {Adim,-46} {Rc,-6} {Toplam,-9} {Ind,-9} {Kdv,-7} {Hesap}",
            "adım", "rc", "toplam", "indirim", "KDV", "Dec-tutar");
        foreach (var a in rapor.Steps)
            log.LogWarning("  {Adim,-46} {Rc,-6} {Toplam,-9} {Ind,-9} {Kdv,-7} {Hesap}",
                a.Name, a.Rc, a.TotalMinor, a.DiscountMinor, a.TaxMinor,
                a.ChangedMinor?.ToString() ?? "-");
        log.LogWarning("Yorum burada YAPILMAZ — sayılar rapora aynen taşınır. `TotalReceiptAmount`ın " +
            "net mi brüt mü olduğu ve KDV'nin hangi tutardan hesaplandığı bu tablodan okunur.");
        return rapor.Status == ProbeStatus.Completed;
    }
}
