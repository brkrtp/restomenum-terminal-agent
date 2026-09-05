namespace Restomenum.Agent.Core;

/// <summary>
/// Kasadan gelen yerel ödeme isteği — nexo/EPAS <c>SaleToPOIRequest</c> zarfının ayrıştırılmış hâli
/// (yerel sözleşme, K-21). <b>Zarfı backend üretir, ajan yalnız AYRIŞTIRIR</b> (kasa→ajan çağrısı
/// backend'den geçmez). <b>Tutar TAŞINMAZ</b> — ajan tutarı <see cref="IPaymentDetailClient"/> ile çeker.
/// </summary>
public sealed record SaleToPoiRequest(
    /// <summary>Yerel çağrının TEKRAR anahtarı (dedupe). ≤10 karakter, paymentId'den türetilmiş,
    /// 48 saat tekil. Aynı ServiceID ile ikinci POST AYNI çağrıdır (kasa ağ-hatası retry'ı).</summary>
    string ServiceId,
    /// <summary>Kasa istasyonu. Yazdırılabilir ASCII ≤32; BOŞ olabilir.</summary>
    string SaleId,
    /// <summary>Terminal kimliği (POIID).</summary>
    string PoiId,
    /// <summary>Defterin anahtarı — platformun ödeme kaydı (<c>pay_</c> + 40 hex).</summary>
    string PaymentId,
    /// <summary>Sipariş numarası — sağlayıcının referansı. <b>Anahtar DEĞİL</b> (dedupe her zaman
    /// PaymentId/ServiceID üzerinden).</summary>
    string SaleReferenceId,
    /// <summary>İsteğin zaman damgası (ISO-8601 UTC).</summary>
    DateTimeOffset TimeStamp);

/// <summary>SaleToPOIRequest reddi sebepleri — her biri farklı <c>SaleToPOIResponse</c> davranışına eşlenir.</summary>
public enum SaleToPoiRejectReason
{
    /// <summary>JSON değil, zarf yok, zorunlu alan eksik/biçimsiz.</summary>
    Malformed,
    /// <summary><c>MessageCategory</c> ≠ <c>Payment</c> → <c>CAPABILITY_NOT_SUPPORTED</c>. Reversal/Abort/… bu turda yok.</summary>
    UnsupportedCategory,
    /// <summary><c>PaymentTransaction</c> zarfta VAR — tutar burada taşınamaz. Bozuk/sahte istek.</summary>
    AmountNotAllowed,
    /// <summary><c>TransactionID</c> (paymentId) <c>pay_</c>+40hex değil.</summary>
    InvalidPaymentId,
    /// <summary><c>ServiceID</c> boş ya da 10 karakterden uzun.</summary>
    InvalidServiceId,
}

/// <summary>
/// İlerleme olayı — platforma bildirilen ara durum (kasaya değil). Platformun tanımladığı sapma
/// (nexo resmî <c>EventToNotify</c> enum'u DEĞİL); bu yüzden yalnız bu değerler geçerli.
/// </summary>
public enum ProgressEvent
{
    /// <summary>Komut terminale gönderildi.</summary>
    SentToTerminal,
    /// <summary>Kart bekleniyor.</summary>
    WaitingForCard,
    /// <summary>Müşteri etkileşimi bekleniyor (PIN vb.).</summary>
    WaitingCustomer,
    /// <summary>İşleniyor.</summary>
    Processing,
}

/// <summary>Ayrıştırma sonucu: geçerli istek ya da red sebebi.</summary>
public abstract record SaleToPoiParseResult
{
    private SaleToPoiParseResult() { }

    public sealed record Ok(SaleToPoiRequest Request) : SaleToPoiParseResult;

    public sealed record Invalid(SaleToPoiRejectReason Reason, string Detail) : SaleToPoiParseResult;
}
