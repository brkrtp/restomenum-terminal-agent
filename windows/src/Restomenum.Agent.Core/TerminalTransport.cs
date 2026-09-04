namespace Restomenum.Agent.Core;

/// <summary>Terminale gönderilecek satış isteği.</summary>
public sealed record SaleRequest(
    string CommandId,
    string PaymentId,
    string TerminalId,
    long AmountMinor,
    string Currency,
    /// <summary>Kuruş basamağı. <b>Varsayılanı YOKTUR</b> — 2 varsaymak JPY gibi kuruşsuz para
    /// birimlerinde tutarı 100 katına çıkarır. Değer komutun payload'ından gelir.</summary>
    int Exponent,
    /// <summary>Denemenin sahibi sağlayıcı; sonuç raporunda geri gönderilir.</summary>
    string? ProviderPluginId = null);

/// <summary>
/// Terminalden dönen sonucun **sınıfı**. Ayrım para güvenliğinin merkezinde: hangi sınıfın güvenle
/// tekrarlanabileceği, hangisinin tekrarlanırsa çift tahsilat üreteceği buradan okunur.
/// </summary>
public enum TransportOutcome
{
    /// <summary>Ödeme alındı. Kesin.</summary>
    Approved,

    /// <summary>Terminal reddetti (yetersiz bakiye, iptal, kart hatası). Para HAREKET ETMEDİ, kesin.</summary>
    Declined,

    /// <summary>
    /// Cihaz meşgul — "sana şu an cevap veremiyorum" (`RECV_BUSY`, sahada 42 kez ölçüldü).
    ///
    /// <para><b>Bu "hiçbir şey olmadı" DEMEK DEĞİLDİR.</b> İlk okumada bunu "komut ulaşmadı, güvenle
    /// tekrarla" diye yorumlamıştık; <b>saha kanıtı bunu çürüttü.</b> GMPDLL_2026_04_17_103039.TXT'de
    /// RECV_BUSY tam olarak kart ödemesi terminalde <b>BAŞARIYLA TAMAMLANDIKTAN</b> sonra geldi:
    /// fiş 3000, tahsil edilmiş 1000, `typeOfPayment:4` (kart). Terminal parayı almış, işlemi
    /// bitirmekle meşguldü ve bu yüzden cevap veremiyordu.</para>
    ///
    /// <para>Bu yüzden <see cref="Unknown"/> ile <b>aynı</b> muamele görür: tekrar gönderilmez,
    /// terminale sorulur. "Meşgul" cevabını güvenli sayan bir agent tam da paranın hareket ettiği
    /// anda ikinci kez tahsilat yapar.</para>
    /// </summary>
    Busy,

    /// <summary>
    /// Terminalde **açık fiş var** (`APP_ERR_ALREADY_DONE`, sahada 6 kez — hepsi `Start`'tan).
    /// Terminalin tek koruması budur: ikinci FİŞ açılamaz. Aynı ÖDEMENİN tekrarına karşı koruma
    /// değildir. Çözüm: açık fişi oku, bize mi ait karar ver.
    /// </summary>
    TicketAlreadyOpen,

    /// <summary>
    /// Sonuç belirsiz — timeout veya bağlantı kopması. <b>Para hareket etmiş OLABİLİR.</b>
    /// Tekrarlamak YASAK; yalnız terminale sorulur (<see cref="ITerminalTransport.ReadTicketAsync"/>).
    ///
    /// <para>✅ <b>Kurtarma yolu sahada DOĞRULANDI</b> — iki gerçek vaka, ikisi de doğru sonuç verdi
    /// (04_17_103039, 90 sn timeout sonrası). Vaka 1: ödeme kısmi (3000 fişe 1000 tahsil) → fiş açık
    /// bırakıldı, kalan ikinci ödemeyle kapandı, <b>çift tahsilat olmadı</b>. Vaka 2: ödeme tam →
    /// doğrudan baskı+kapatma. Doğrulanmamış tek yol 2069 → `VoidPayment` (kart ters işlemi).</para>
    /// </summary>
    Unknown,
}

/// <summary>Terminal sonucu. Kart verisi TAŞIMAZ (§12.3) — yalnız maskeli/referans alanlar.</summary>
public sealed record TransportResult(
    TransportOutcome Outcome,
    long? ApprovedAmountMinor = null,
    string? Rrn = null,
    string? ApprovalCode = null,
    string? CardLast4 = null,
    string? Scheme = null,
    string? ProviderResultCode = null);

/// <summary>Terminaldeki açık fişin durumu — `UNKNOWN` çözümünün tek otoritesi.</summary>
public sealed record TicketState(
    bool HasOpenTicket,
    long TotalAmountMinor,
    long PaidAmountMinor,
    string? Rrn = null,
    string? CardLast4 = null)
{
    /// <summary>Fiş tamamen ödenmiş mi? Sahadaki kurtarma mantığının aynısı.</summary>
    public bool IsFullyPaid => TotalAmountMinor > 0 && PaidAmountMinor >= TotalAmountMinor;
}

/// <summary>
/// Terminale konuşan katman. **Uygulaması sertifikalı sarmalayıcıya delege eder** (§8.3b): mali akış
/// mantığı burada DEĞİL, çağıran tarafta. Bu arayüz yalnız "yap ve sonucu söyle" der.
///
/// Test için <see cref="SimulatorTransport"/> vardır; gerçek uygulama sertifikalı bileşeni çağırır.
/// </summary>
public interface ITerminalTransport
{
    /// <summary>Satış. Kartlı işlemde sahada ölçülen süre <b>20–32 sn</b>; tek bloklayan adım budur.</summary>
    Task<TransportResult> SaleAsync(SaleRequest request, CancellationToken ct = default);

    /// <summary>
    /// Terminaldeki açık fişi okur. <c>UNKNOWN</c> çözümü buradan gelir — yerel duruma değil,
    /// <b>cihazın kendisine</b> sorulur.
    /// </summary>
    Task<TicketState> ReadTicketAsync(CancellationToken ct = default);

    /// <summary>
    /// Canlılık kontrolü (<c>FP3_Echo</c>). <b>Ping KULLANILMAZ</b> — cihaz ICMP'ye cevap vermiyor
    /// (§8.3c, ölçüldü). Seyrek çağrılmalı: cihaz tek oturumlu.
    /// </summary>
    Task<bool> EchoAsync(CancellationToken ct = default);
}
