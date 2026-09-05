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
public sealed record NotifyResult(NotifyOutcome Outcome, string? State, string? Reason, int StatusCode, string Message)
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
}
