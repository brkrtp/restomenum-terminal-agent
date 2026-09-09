using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>W69 — kalıcı reddedilmiş bir fiş iptali bildirimini yeniden gönderir</b>
/// (<c>--replay-ticket-cancel &lt;saleSessionId&gt; &lt;ticketId&gt; &lt;serviceId&gt; [ticketCancelId]</c>).
///
/// <para><b>CİHAZA DOKUNMAZ.</b> Ne eşleşme kurar ne fiş açar; mali fiş numarası (FNo) YAKMAZ.
/// Yaptığı tek şey, cihazda ZATEN gerçekleşmiş bir iptali platforma bildirmektir.</para>
///
/// <para><c>--onayla</c> olmadan yalnız gövdeyi basar (kuru prova) ve hiçbir şey göndermez.</para>
/// </summary>
public static class WindowsReplayTicketCancel
{
    public static async Task<bool> RunAsync(
        IServiceProvider services, string[] args, bool operatorOnayi, CancellationToken ct = default)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("ReplayTicketCancel");

        // ⚠️ `poiId` DE İSTENİYOR — ajanda saklı bir "terminal kimliği" YOK: o değer nexo zarfından
        // (`SaleToPOIRequest.MessageHeader.POIID`) geliyor ve kasa dikte ediyor. `ticketCancelId`
        // deseninden ayıklamak platformun ürettiği bir kimliği parçalamak olurdu. İkisi de zaten
        // operatörün okuduğu AYNI günlük satırında duruyor:
        //   [iptal] fiş sonucu { … terminalId = windows-gmp-test … ticketCancelId = …-ticketcancel-2 }
        // Outbox'ta damgalı kayıt varsa bu iki argüman KULLANILMAZ (gövde orijinaldir).
        if (args.Length < 4)
        {
            log.LogError("Kullanım: --replay-ticket-cancel <saleSessionId> <ticketId> <serviceId> " +
                "<poiId> [ticketCancelId] [--onayla]   " +
                "(poiId ve ticketCancelId `[iptal] fiş sonucu` günlük satırında)");
            return false;
        }
        var (saleSessionId, ticketId, serviceId, poiId) = (args[0], args[1], args[2], args[3]);
        var ticketCancelId = args.Length > 4 ? args[4] : null;

        var outbox = services.GetRequiredService<Outbox>();

        var plan = TicketCancelReplay.Hazirla(outbox, saleSessionId, ticketId, serviceId,
            ticketCancelId, poiId, DateTimeOffset.UtcNow, out var hata);
        if (plan is not TicketCancelReplayPlan p)
        {
            log.LogError("Gövde hazırlanamadı: {Hata}", hata);
            return false;
        }

        log.LogWarning("KAYNAK: {Kaynak}", p.FromOutbox
            ? "outbox'taki DAMGALI kayıt — gövde ORİJİNALİN TA KENDİSİ, hiçbir alan yeniden yorumlanmadı"
            : "outbox'ta kayıt YOK — gövde verdiğiniz alanlardan YENİDEN KURULDU (aynı üretici)");
        log.LogWarning("EVENT ID : {Eid}", p.EventId);
        log.LogWarning("GÖVDE    : {Govde}", p.Body);

        if (!operatorOnayi)
        {
            log.LogWarning("KURU PROVA — `--onayla` verilmedi, HİÇBİR ŞEY GÖNDERİLMEDİ. " +
                "Göndermek için aynı komuta `--onayla` ekleyin.");
            return true;
        }

        // Dayanıklı ÖNCE: gönderim başarısız olursa kayıt kuyrukta kalsın ve arka plan replay etsin.
        // `INSERT OR IGNORE` — kayıt zaten varsa (damgalı hâli) ikinci kez yazılmaz.
        outbox.Enqueue(p.EventId, "", OutboxKinds.TicketCancel, p.Body, "");

        var notifier = services.GetRequiredService<IResultNotifier>();
        var sonuc = await notifier.NotifyTicketCancelAsync(p.Body, ct);

        log.LogWarning("SONUÇ: outcome={Outcome} status={Status} state={State} message={Mesaj}",
            sonuc.Outcome, sonuc.StatusCode, sonuc.State ?? "(yok)", sonuc.Message);

        if (sonuc.Outcome == NotifyOutcome.Recorded)
        {
            outbox.Confirm(p.EventId);
            log.LogWarning("✅ PLATFORM KAYDETTİ — outbox kaydı onaylandı ve silindi. Defter artık cihazla hizada.");
            return true;
        }

        // Hâlâ reddediliyorsa kayıt DURUR (W68): sebep giderilince yeniden denenebilir.
        if (sonuc.IsPermanentGap)
        {
            outbox.MarkPermanentlyRejected(p.EventId);
            log.LogError("❌ HÂLÂ REDDEDİLİYOR (400) — kayıt damgalandı ve DURUYOR. Sebep giderilmeden " +
                "tekrar denemeyin; platform tarafındaki düzeltmenin canlıda olduğundan emin olun.");
        }
        else
        {
            log.LogError("Beklenen 200/`recorded` değil. Kayıt outbox'ta DURUYOR; arka plan " +
                "yeniden deneyecek (ağ/hız hatası) ya da operatör bakmalı.");
        }
        return false;
    }
}
