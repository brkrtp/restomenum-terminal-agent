namespace Restomenum.Agent.Core;

/// <summary>Yeniden gönderim için hazırlanan gövde ve nereden geldiği.</summary>
/// <param name="Body">Gönderilecek P5 gövdesi.</param>
/// <param name="FromOutbox">
/// <c>true</c> = outbox'ta damgalı kayıt bulundu, gövde <b>orijinalin ta kendisi</b>.
/// <c>false</c> = kayıt yok, gövde operatörün verdiği alanlardan YENİDEN kuruldu.
/// </param>
/// <param name="EventId">Outbox anahtarı — gönderim sonrası bu kayıt onaylanır.</param>
public readonly record struct TicketCancelReplayPlan(string Body, bool FromOutbox, string EventId);

/// <summary>
/// <b>W69 — kalıcı reddedilmiş bir fiş iptali bildiriminin yeniden gönderimi.</b>
///
/// <para><b>Neden var (sahada ölçüldü, 2026-09-09 14:42):</b> kullanıcı kasadan fiş iptali yaptı,
/// cihaz fişi iptal etti (<c>Approved</c>, tutar 0), ama platform bildirimi <c>400
/// plugin.payment.invalidResult</c> ile reddetti — kök sebep platformdaydı ve düzeltildi. O sırada
/// ajan 400'ü "kesin sonuç" sayıp kaydı SİLİYORDU (W68 bunu değiştirdi), dolayısıyla iptalin
/// platformda hiçbir izi kalmadı ve kendiliğinden gidecek bir yol da yoktu.</para>
///
/// <para><b>İki kaynak, biri diğerinden ÜSTÜN:</b></para>
/// <list type="number">
///   <item><b>Outbox'taki damgalı kayıt</b> (W68 sonrası normal hâl) — gövde orijinaldir, hiçbir
///     şey yeniden yorumlanmaz. Tercih edilen yol.</item>
///   <item><b>Yeniden kurma</b> — kayıt silinmişse (W68 öncesi vakalar). Gövde AYNI
///     <see cref="SaleToPoiResponseBuilder"/> ile kurulur; elle JSON yazmak "tek üretici" kuralını
///     kırardı. Bu yolda <c>ticketCancelId</c> <b>ZORUNLUDUR</b>: platformun ürettiği bir kimliği
///     desenden türetmek onu UYDURMAK olurdu ve yanlışı 409 <c>voidNotRequested</c> ile döner.</item>
/// </list>
/// </summary>
public static class TicketCancelReplay
{
    /// <summary>Outbox anahtarı — üretimdeki ile AYNI kural (<c>LocalSaleHandler</c>).</summary>
    public static string EventIdFor(string ticketCancelId) => ticketCancelId + ":ticket-cancel";

    /// <summary>
    /// Gönderilecek gövdeyi hazırlar. <b>Hiçbir şey göndermez ve hiçbir şeyi değiştirmez</b> —
    /// çağıran kuru provada da bunu kullanır.
    /// </summary>
    /// <returns>Plan; <c>null</c> = hazırlanamadı (sebebi <paramref name="hata"/>'da).</returns>
    public static TicketCancelReplayPlan? Hazirla(
        Outbox outbox, string saleSessionId, string ticketId, string serviceId,
        string? ticketCancelId, string poiId, DateTimeOffset now, out string? hata)
    {
        hata = null;

        // ── 1. ÖNCE OUTBOX: orijinal gövde varsa o kullanılır ───────────────────────
        // Yeniden kurulan gövde, orijinalin "en iyi tahmini"dir; orijinalin kendisi değil.
        // Elimizde gerçeği varken tahmini göndermek olmaz.
        var damgali = outbox.Rejected(limit: 200)
            .FirstOrDefault(e => e.Status == OutboxKinds.TicketCancel && e.PayloadJson.Contains(ticketId, StringComparison.Ordinal));
        if (damgali is not null)
            return new TicketCancelReplayPlan(damgali.PayloadJson, FromOutbox: true, damgali.EventId);

        // ── 2. YENİDEN KURMA — yalnız kayıt yoksa ───────────────────────────────────
        if (string.IsNullOrWhiteSpace(ticketCancelId))
        {
            hata = "outbox'ta damgalı kayıt YOK ve `ticketCancelId` verilmedi. Bu kimliği PLATFORM " +
                   "üretir; desenden türetmek onu uydurmak olur ve yanlışı 409 `voidNotRequested` " +
                   "ile döner. Günlükten okuyup 4. argüman olarak verin.";
            return null;
        }

        var req = new ReversalRequest(
            ServiceId: serviceId, SaleId: "", PoiId: poiId, PaymentId: null,
            OriginalPoiTransactionId: null, OriginalServiceId: null, ReversalReason: null,
            TimeStamp: now, Scope: "ticket", SaleSessionId: saleSessionId,
            TicketCancelId: ticketCancelId);

        // İptalin SONUCU: cihazda gerçekten iptal edildi, üzerinde ödeme yoktu. Bu üç değer
        // ölçümden geliyor (günlük), varsayılmıyor — `VoidedPaymentCount`/`VoidedAmountMinor` 0
        // olduğu için gövdede `ReversedAmount` HİÇ yer almaz (olmayan bir iadeyi bildirmeyiz).
        var sonuc = new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: true,
            VoidedPaymentCount: 0, VoidedAmountMinor: 0,
            CancelledSaleSessionId: saleSessionId, CancelledTicketId: ticketId,
            Reason: RestomenumReasons.TicketCancelled);

        return new TicketCancelReplayPlan(
            SaleToPoiResponseBuilder.BuildTicketReversalResult(req, sonuc, 2, now),
            FromOutbox: false, EventIdFor(ticketCancelId));
    }
}
