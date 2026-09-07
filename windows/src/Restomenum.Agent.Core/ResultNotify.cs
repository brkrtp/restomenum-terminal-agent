namespace Restomenum.Agent.Core;

/// <summary>
/// Sonuç/ilerleme bildiriminin platform yanıtı (<c>POST /plugin-api/payments/{id}/result</c>).
/// <b>Defterin otoritesi platformdur</b> — bu yanıt "yazıldı mı" der.
/// </summary>
public enum NotifyOutcome
{
    /// <summary>200 <c>recorded:true</c> — deftere yazıldı. İş bitti (outbox onaylanır).</summary>
    Recorded,
    /// <summary>200 <c>recorded:false</c> (<c>stale</c>/<c>illegalTransition</c>) — YAZILMADI ama HATA DEĞİL
    /// (bayat/sırasız). At-least-once tekrarında normal; RETRY ETME (outbox onaylanır).</summary>
    Superseded,
    /// <summary>409 — çelişki (conflictingResult / amountExceedsRequested / currencyMismatch /
    /// paymentIdMismatch). Aynı gövde tekrar aynı hatayı verir → RETRY ETME; sorun sinyali (alarm).</summary>
    Conflict,
    /// <summary>400 — gövde reddedildi (invalidCardLast4 / approvedAmountRequired / invalidStatus).
    /// RETRY ETME (aynı gövde), üretici hatası — düzelt/alarm.</summary>
    Rejected,
    /// <summary>404 — bilinmeyen ödeme VEYA bu cihazın değil. RETRY ETME.</summary>
    NotFound,
    /// <summary>429 — hız sınırı. Geri çekilip TEKRAR DENE.</summary>
    RateLimited,
    /// <summary>Ağ/HTTP hatası — platforma ulaşılamadı. Outbox'ta kalır, TEKRAR DENE (replay).</summary>
    NetworkError,
}

/// <summary>Bildirim sonucu — outbox'ın "onayla / tekrar dene / alarm" kararını verir.</summary>
/// <param name="Replayed">
/// Platform bu bildirimi <b>tekrar</b> saydı mı (aynı sonuç daha önce yazılmıştı)? <c>null</c> =
/// alan gelmedi. Teşhiste "yazıldı" ile "zaten yazılıydı"yı ayırır — ikisi de başarı ama replay
/// fırtınası ancak bu alanla görülür.
/// </param>
/// <param name="AlreadyPosted">
/// Fiş kapanış bildiriminde: daha önce yazılmış satır sayısı. <c>null</c> = alan gelmedi.
/// </param>
public sealed record NotifyResult(NotifyOutcome Outcome, string? State, string? Reason, int StatusCode,
    string Message, bool? Replayed = null, int? AlreadyPosted = null)
{
    /// <summary>Outbox kaydı silinmeli mi? Kesin (yazıldı/bayat/çelişki/red/notfound) → EVET; ağ/hız → HAYIR.</summary>
    public bool IsFinal => Outcome is not (NotifyOutcome.RateLimited or NotifyOutcome.NetworkError);

    /// <summary>Operatör/alarm gerektiren kalıcı sorun mu? (çelişki/red/notfound)</summary>
    public bool IsProblem => Outcome is NotifyOutcome.Conflict or NotifyOutcome.Rejected or NotifyOutcome.NotFound;
}

/// <summary>Sonuç/ilerleme gövdesini platforma bildiren istemci (kimlik = cihaz oturum JWT).</summary>
public interface IResultNotifier
{
    /// <param name="paymentId">URL'deki kimlik — gövdedeki <c>SaleTransactionID.TransactionID</c> ile EŞLEŞMELİ.</param>
    /// <param name="bodyJson">Üretilmiş <c>SaleToPOIResponse</c> (sonuç) ya da <c>EventNotification</c> (ilerleme).</param>
    Task<NotifyResult> NotifyAsync(string paymentId, string bodyJson, CancellationToken ct = default);

    /// <summary>
    /// FİŞ iptali sonucu — <c>POST /plugin-api/payments/ticket-cancel/result</c>.
    ///
    /// <para><b>Neden ayrı uç:</b> URL'de <c>paymentId</c> YOK. Fiş iptali bir ödeme denemesine ait
    /// değil — cihazda deneme olmadan da meşru bir fiş durabilir (başka kasadan kalmış olabilir).
    /// Komutu gövdedeki <c>ticketCancelId</c> tanımlar.</para>
    /// </summary>
    Task<NotifyResult> NotifyTicketCancelAsync(string bodyJson, CancellationToken ct = default);

    /// <summary>
    /// FİŞ KAPANDI bildirimi (K-26/P27) — <c>POST /plugin-api/payments/ticket-closed</c>.
    ///
    /// <para><b>Neden ayrı uç ve neden paymentId yok:</b> bildirim tek bir denemeye değil, bir
    /// FİŞE aittir; içinde o fişin BÜTÜN ödemeleri var. Ödeme ucuna gönderilseydi hangi
    /// paymentId ile adresleneceği keyfî olurdu ve platform tekilleştirmeyi yanlış anahtara
    /// dayardı. Tekilleştirme anahtarı gövdedeki <c>ticketId</c>.</para>
    ///
    /// <para>Kimlik <c>/ticket-cancel/result</c> ile AYNI (cihaz oturum JWT'si).</para>
    /// </summary>
    Task<NotifyResult> NotifyTicketClosedAsync(string bodyJson, CancellationToken ct = default);
}
