namespace Restomenum.Agent.Core;

/// <summary>
/// Platformdan çekilen ödeme detayı (<c>GET /plugin-api/payments/{paymentId}</c>) — <b>tutarın ve
/// kalemlerin TEK otoritesi</b> (yerel sözleşme, K-21). Yerel istemci bunu değiştiremez.
///
/// <para><b>GET = ACK:</b> bu detayı çekmek denemeyi <c>DISPATCHING→ACCEPTED</c> geçirir; "komut
/// ulaştı"nın tek kanıtıdır. Bu yüzden terminali SÜRMEDEN önce çekilir, çekmeden sürülmez.</para>
///
/// <para>Tutarlar İÇERİDE tam sayı kuruş (<see cref="Money"/> ile çevrilir). <see cref="ItemsScope"/>
/// <c>"fullSale"</c> ise kalemler TÜM satışın; <see cref="RequestedAmountMinor"/> kısmi olabilir
/// (TR'de artımlı ödeme normal) — ikisinin eşit olmasını bekleme.</para>
/// </summary>
public sealed record PaymentDetail(
    string PaymentId,
    string SaleReferenceId,
    string Currency,
    /// <summary>Minor unit basamağı (<c>RestomenumExt.Exponent</c>). Tutar dönüşümünde çarpan
    /// <c>10^Exponent</c>; para biriminden TAHMİN edilmez, GET yanıtından okunur.</summary>
    int Exponent,
    long RequestedAmountMinor,
    long SaleTotalAmountMinor,
    string Market,
    string State,
    long ExpiresAtMs,
    string ItemsScope,
    IReadOnlyList<SaleLine> Items,
    /// <summary>
    /// Kasiyerin seçtiği ödeme yöntemi kimliği (<c>RestomenumExt.PaymentMethodId</c>, ör. <c>"11-cash"</c>).
    /// <b>Platform DİKTE eder</b> (defter bütünlüğü) ve GET yanıtında DAİMA dolu gelir (kasa göndermese de
    /// varsayılan çözülmüş hâliyle). Ajan bunu <see cref="IPaymentMethodResolver"/> ile cihaz ödeme tipine
    /// (<c>GmpPaymentTypes</c> 1/4/16) çevirir; eşleme yoksa fail-closed. TEK EKSEN — kaba PaymentType yok.
    /// </summary>
    string PaymentMethodId = "");

/// <summary>
/// Satış kalemi. <b><see cref="ItemAmountMinor"/> OTORİTEDİR.</b> Birim fiyat türetilmiştir
/// (<c>round(satırToplamı/adet)</c>); adet &gt; 1'de <c>UnitPrice × Quantity</c> ile
/// <c>ItemAmount</c> arasında yarım-kuruş × adet fark olabilir — satır toplamı DAİMA
/// <c>ItemAmount</c>'tan alınır, birim fiyattan hesaplanmaz.
///
/// <para><c>SaleItem</c> yalnız <b>Türkiye'de</b> gönderilir; Avrupa'da alan HİÇ yoktur
/// (boş dizi değil, yok) — o pazarda <see cref="PaymentDetail.Items"/> boş kalır.</para>
/// </summary>
public sealed record SaleLine(
    int ItemId,
    string ProductCode,
    string ProductLabel,
    int Quantity,
    /// <summary>
    /// <b>SATIR TOPLAMI</b> (kuruş), birim fiyat DEĞİL. <see cref="FiscalLineBuilder"/> bunu
    /// <see cref="Quantity"/>'ye bölerek birim fiyatı türetir ve tam bölünmezse satırı ikiye ayırır.
    /// </summary>
    long ItemAmountMinor,
    string TaxCode,
    string? CategoryId,
    string? LineId,

    /// <summary>
    /// <c>RestomenumExt.DiscountAmount</c> — ham hâliyle. <b><c>null</c> = alan YOK</b> (indirim yok).
    ///
    /// <para><b>Neden ham ve neden burada yargılanmıyor:</b> ayrıştırıcının işi okumak, yargılamak
    /// değil. "Alan yok", "alan var ama bozuk" ve "alan var, değeri geçersiz" ÜÇ AYRI durum;
    /// ayrıştırıcı üçünü ayırt edilebilir bırakır, kararı <see cref="LocalSaleHandler"/> verir ve
    /// gerekirse cihaza GİTMEDEN reddeder.</para>
    ///
    /// <para>⚠️ <b>Birim asimetrisi:</b> <c>ItemAmount</c> ANA birimde gelir (9.90) ve
    /// <c>Money.ToMinor</c> ile kuruşa çevrilir; sözleşmeye göre <c>DiscountAmount</c> ise doğrudan
    /// <b>KURUŞ tam sayısı</b>. Aynı nesnede iki farklı birim var; bu yüzden burada çevrilmiyor ve
    /// tam sayı olması ayrıca doğrulanıyor.</para>
    /// </summary>
    decimal? DiscountAmountRaw = null,

    /// <summary><c>DiscountAmount</c> alanı VAR ama sayı DEĞİL (string, null, nesne…). "Alan yok"tan
    /// ayrı tutuluyor: yokluk indirimsiz satıştır, bozukluk ise reddedilmesi gereken bir sözleşme
    /// ihlali. İkisini eşitlemek, bozuk bir gövdeyi sessizce "indirim yok" saymak olurdu.</summary>
    bool DiscountMalformed = false);

/// <summary>GET reddi sebepleri. Her biri farklı bir <c>SaleToPOIResponse</c> davranışına eşlenir.</summary>
public enum PaymentRejectReason
{
    /// <summary>401 — cihaz oturum token'ı geçersiz. Kimlik yenile.</summary>
    Unauthorized,
    /// <summary>404 — bilinmeyen paymentId VEYA bu cihazın değil (ayrım YOK, oracle koruması). Sürme.</summary>
    NotFound,
    /// <summary>409 — deneme süresi doldu (dağıtım TTL / expirySweeper). Sürme.</summary>
    Expired,
    /// <summary>409 — deneme bu durumda işlenemez.</summary>
    NotActionable,
    /// <summary>409 — ilk GET'ten 60 sn geçti; tutar bir daha verilmez. O kart çekilmez (kasıtlı).</summary>
    AmountWindowClosed,
    /// <summary>409 — kalem dökümü alınamıyor (TR'de zorunlu).</summary>
    SaleItemsUnavailable,
    /// <summary>429 — hız sınırı.</summary>
    RateLimited,
    /// <summary>Beklenmedik/eşlenemeyen yanıt.</summary>
    Unknown,
}

/// <summary>GET sonucu: ya sürülebilir <see cref="Ok"/> detay, ya da <see cref="Rejected"/> (sürme).</summary>
public abstract record PaymentDetailResult
{
    private PaymentDetailResult() { }

    public sealed record Ok(PaymentDetail Detail) : PaymentDetailResult;

    public sealed record Rejected(PaymentRejectReason Reason, string Message, int StatusCode) : PaymentDetailResult;
}

/// <summary>Tutar/kalem detayını platformdan çeken istemci (kimlik = cihaz oturum JWT'si, Bearer).</summary>
public interface IPaymentDetailClient
{
    Task<PaymentDetailResult> FetchAsync(string paymentId, CancellationToken ct = default);
}
