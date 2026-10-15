namespace Restomenum.Agent.Core;

/// <summary>Terminaldeki açık fişin durumu — ham okuma.</summary>
public sealed record TicketState(
    bool HasOpenTicket,
    long TotalAmountMinor,
    long PaidAmountMinor,
    string? Rrn = null,
    string? CardLast4 = null,
    /// <summary>
    /// Fişteki ödeme adedi. <b>Artımlı modelde belirsizliği çözen asıl alan budur:</b> tutar
    /// karşılaştırması iki eşit ödemeyi ayırt edemez (20 ₺ + 20 ₺), sayaç ayırt eder.
    /// </summary>
    int PaymentCount = 0,

    /// <summary>Son ödeme kaydının cihazdaki hata kodu — bkz. <see cref="GmpTicket.LastPaymentErrorCode"/>.</summary>
    string? LastPaymentErrorCode = null,

    /// <summary>Son ödeme kaydının cihazdaki hata metni — bkz. <see cref="GmpTicket.LastPaymentErrorText"/>.</summary>
    string? LastPaymentErrorText = null,

    /// <summary>Cihazın uygulama hata kodu — bkz. <see cref="GmpTicket.LastPaymentAppErrorCode"/>.</summary>
    string? LastPaymentAppErrorCode = null,

    /// <summary>
    /// Cihazın bu okumada DOLDURDUĞU ödeme satırları. Tamamı olup olmadığını
    /// <see cref="PaymentsAreComplete"/> söyler — bkz. <see cref="GmpTicket.PaymentsAreComplete"/>.
    /// </summary>
    IReadOnlyList<GmpPaymentLine>? Payments = null,

    /// <summary><see cref="Payments"/> fişin TAMAMI mı? Değilse deftere satır yazılamaz.</summary>
    bool PaymentsAreComplete = false,

    /// <summary><c>ST_PaymentErrMessage.AppErrorMsg</c> — ayrı tutuluyor, birleştirme teşhisi kör eder.</summary>
    string? LastPaymentAppErrorText = null,

    /// <summary>RM-0396: bankanın onay kodu (bkz. <see cref="GmpTicket.ApprovalCode"/>); günlüğe yazılmaz.</summary>
    string? ApprovalCode = null)
{
    /// <summary>Fiş tamamen ödenmiş mi? Sahadaki kurtarma mantığının aynısı.</summary>
    public bool IsFullyPaid => TotalAmountMinor > 0 && PaidAmountMinor >= TotalAmountMinor;

    /// <summary>
    /// Kalan tutar. Artımlı modelde (Türkiye) sıfırdan büyük olması <b>arıza değildir</b> — kasiyer
    /// kalanı ayrı bir ödemeyle ekleyecek. Ama fiş bu hâlde <b>kapatılamaz</b>: ya tamamlanır ya
    /// da yarım ödenen de dahil iptal edilir.
    /// </summary>
    public long RemainingMinor => Math.Max(0, TotalAmountMinor - PaidAmountMinor);
}
