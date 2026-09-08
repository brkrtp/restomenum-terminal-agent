using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>W57 — yetenek yoklamasının Windows kabuğu</b> (<c>--capability-probe</c>). Ölçümün kendisi
/// <see cref="CapabilityProbe"/>'da (çekirdek, testli); burada yalnız eşleşme, operatör onayı ve
/// çıktı var.
///
/// <para><b>⚠️ MALİ FİŞ AÇAR</b> (yazdırmadan iptal eder, ama bir mali fiş numarası tüketebilir —
/// FR07'de ölçüldü). Bu yüzden <c>--onayla</c> ŞART: onaysız çağrı yalnız ne yapacağını yazar ve
/// cihaza DOKUNMAZ.</para>
/// </summary>
public static class WindowsCapabilityProbe
{
    public static bool Run(IServiceProvider services, bool operatorOnayi = false)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("CapabilityProbe");

        log.LogWarning("YETENEK YOKLAMASI — ölçülecek tipler: {Tipler}",
            string.Join(", ", CapabilityProbe.Candidates.Select(a => $"{a.Name}({a.Type})→{a.Op}")));
        log.LogWarning("Plan: TEK fiş açılır ({Kurus} kuruşluk 1 kalem), her tip için 1 kuruş kısmi ödeme " +
            "denenir, sonra VoidAll+Close. YAZDIRMA YOK — kâğıda mali fiş çıkmaz, ama fiş AÇILDIĞI için " +
            "bir mali fiş numarası (FNo) tüketebilir.", CapabilityProbe.ItemAmountMinor);

        if (!operatorOnayi)
        {
            log.LogWarning("KURU PROVA — `--onayla` verilmedi, cihaza DOKUNULMADI. " +
                "Çalıştırmak için: --capability-probe --onayla");
            return true;
        }

        // Eşleşme süreç-bağlı: bu süreç kendi eşleşmesini kurmadan cihaz konuşmaz.
        if (!WindowsPairing.Run(services))
        {
            log.LogError("eşleşme kurulamadı — yoklama yapılamadı.");
            return false;
        }

        var rapor = CapabilityProbe.Run(services.GetRequiredService<IGmpWrapper>(),
            (m, d) => log.LogWarning("{Mesaj} {Detay}", m, d));

        if (rapor.Status == CapabilityProbeStatus.TicketAlreadyOpen)
        {
            log.LogError("terminalde AÇIK fiş var — yoklama YAPILMADI. Önce o fişi sonuçlandırın " +
                "(`--ticket` ile bakın, gerekirse `--void`).");
            return false;
        }
        if (rapor.Status == CapabilityProbeStatus.SetupFailed)
        {
            log.LogError("yoklama fişi açılamadı: {Adim} rc={Rc} — hiçbir tip ölçülmedi.",
                rapor.FailedStep, rapor.FailedRc);
            return false;
        }
        if (rapor.Status == CapabilityProbeStatus.HandleLost)
            log.LogError("tanıtıcı ölçüm sırasında geçersizleşti ({Adim} rc={Rc}) — KALAN TİPLER ÖLÇÜLMEDİ.",
                rapor.FailedStep, rapor.FailedRc);

        if (!rapor.CleanedUp)
            log.LogError("⚠️ TEMİZLİK TAM DEĞİL (VoidAll={V}, Close={C}) — terminalde fiş kalmış olabilir. " +
                "`--ticket` ile bakın.", rapor.VoidAllRc, rapor.CloseRc);

        log.LogWarning("═══ YOKLAMA SONUCU ═══");
        foreach (var s in rapor.Results)
            log.LogWarning("  {Ad,-16} {Op,-20} rc={Rc,-6} {Sonuc}", s.Name, s.Op, s.Rc, s.Accepted ? "KABUL" : "RET");

        var kabuller = rapor.Results.Where(s => s.Accepted).Select(s => s.Op).ToList();
        log.LogWarning("BEYANA EKLENEBİLECEK op'lar (ÖLÇÜLDÜ): {Ops}",
            kabuller.Count == 0 ? "(yok)" : string.Join(", ", kabuller));
        log.LogWarning("Bu araç beyanı DEĞİŞTİRMEZ — `AgentCapabilities.Declared` elle, bu ölçüme " +
            "dayanarak güncellenir. Ölçülmemiş yetenek beyan edilmez.");
        return rapor.Status == CapabilityProbeStatus.Completed;
    }
}
