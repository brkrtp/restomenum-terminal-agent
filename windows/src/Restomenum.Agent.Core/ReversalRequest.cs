namespace Restomenum.Agent.Core;

/// <summary>
/// Kasadan gelen <b>fiş/ödeme iptali</b> isteği (<c>MessageCategory: "Reversal"</c>).
///
/// <para><b>İki referansla gelir ve ikisi FARKLI vakadır:</b></para>
/// <list type="bullet">
///   <item><b>Tamamlanmış ödeme</b> — <see cref="OriginalPoiTransactionId"/> dolu. Para alınmıştır;
///   iptal, banka ters işlemi demektir.</item>
///   <item><b>Açık/ödenmemiş fiş</b> — <see cref="OriginalPoiTransactionId"/> YOK, referans
///   <see cref="OriginalServiceId"/>. Para alınmamıştır; iptal, fişi kapatmak demektir. Bugünkü
///   saha vakası budur: başarısız kart denemesi cihazda açık fiş bırakıyor ve kasiyerin onu
///   temizleyecek bir düğmesi yok.</item>
/// </list>
///
/// <para><b>Neden ayrı tip:</b> ödeme isteğiyle aynı tipe sıkıştırmak, "tutar taşınmaz" gibi
/// ödemeye özgü kuralları iptale de bulaştırırdı. İptalin kendi kuralları var: tutar zaten yok,
/// referans zorunlu, ve <b>hangi referansın geldiği hangi cihaz yolunu seçeceğimizi belirliyor</b>.</para>
/// </summary>
public sealed record ReversalRequest(
    /// <summary>Bu iptal çağrısının tekrar anahtarı. Ödemenin ServiceID'sinden FARKLIDIR.</summary>
    string ServiceId,
    string SaleId,
    string PoiId,
    /// <summary>
    /// Defterin anahtarı — iptal edilecek ödemenin kaydı (<c>pay_</c> + 40 hex).
    ///
    /// <para><b><c>scope:"ticket"</c>'ta <c>null</c> olabilir.</b> Fiş iptali bir denemeye ait
    /// değildir: cihazda başka kasadan kalmış, bu kasanın hiç paymentId'si olmayan bir fiş
    /// durabilir. O yolda eşleştirme anahtarı <c>ticketCancelId</c>'dir. Ödeme kapsamında
    /// ZORUNLUDUR — orada hangi ödemenin iptal edildiği paymentId ile belirlenir.</para>
    /// </summary>
    string? PaymentId,
    /// <summary>Tamamlanmış ödemenin cihaz referansı. <c>null</c> ise vaka "açık fiş iptali".</summary>
    string? OriginalPoiTransactionId,
    /// <summary>Orijinal ödeme çağrısının ServiceID'si (açık fiş dalının referansı).</summary>
    string? OriginalServiceId,
    /// <summary>nexo <c>ReversalReason</c> (ör. <c>MerchantCancel</c>). Bilgi amaçlı.</summary>
    string? ReversalReason,
    DateTimeOffset TimeStamp,

    /// <summary>
    /// İptalin KAPSAMI (<c>SaleToPOIRequest.Restomenum.scope</c>).
    /// <c>"ticket"</c> = kasiyerin "Fiş İptal" düğmesi: cihazdaki AÇIK FİŞİN TAMAMI iptal edilir.
    /// <c>null</c>/<c>"payment"</c> = eski davranış (tek ödeme referansıyla).
    ///
    /// <para><b>Fiş kapsamında bağ eşleşmesi ARANMAZ.</b> Kasiyer cihazın başında ve gördüğü fişi
    /// iptal ediyor; o fiş başka bir kasadan kalmış olabilir. Sahiplik kapısı ÖDEME yolunun kuralı
    /// (orada başkasının parasını sahiplenme riski var); burada tam tersi, kasiyerin cihazdaki
    /// gerçek fişe müdahale edebilmesi gerekiyor. Gerçekten iptal edilen fişin sahibi yanıtta
    /// <c>cancelledSaleSessionId</c> ile bildirilir ki platform DOĞRU oturumun satırlarını düşürsün.</para>
    /// </summary>
    string? Scope = null,

    /// <summary>İsteği yapan satış oturumu (<c>Restomenum.saleSessionId</c>). Yanıtta aynen döner.</summary>
    string? SaleSessionId = null,

    /// <summary>
    /// Fiş iptali komutunun kimliği (<c>Restomenum.ticketCancelId</c>) — platform üretir, yanıtta
    /// AYNEN döner. Sonuç ucunda paymentId YOK: fiş iptali bir denemeye ait değil (deneme yokken de
    /// meşru), komutu bu alan tanımlar.
    /// </summary>
    string? TicketCancelId = null);

/// <summary>Fiş bazlı iptalin sonucu — ödeme sonucundan AYRI tip: farklı soruya cevap veriyor.</summary>
public sealed record TicketVoidResult(
    TransportOutcome Outcome,
    /// <summary>Cihazda AÇIK fiş var mıydı? <c>false</c> ise hiçbir şeye dokunulmadı.</summary>
    bool TicketWasOpen,
    /// <summary>İptal edilen fişteki ödeme adedi (iptalden ÖNCE okundu).</summary>
    int VoidedPaymentCount = 0,
    /// <summary>İptal edilen fişte TAHSİL EDİLMİŞ toplam (iptalden ÖNCE okundu).</summary>
    long VoidedAmountMinor = 0,
    /// <summary>Gerçekten iptal edilen fişin sahibi; bağ yoksa <c>null</c>.</summary>
    string? CancelledSaleSessionId = null,

    /// <summary>
    /// İptal edilen FİŞİN kimliği (<c>tkt_</c>+32hex). Bağ yoksa ya da fiş zaten yoksa <c>null</c>.
    ///
    /// <para><b>Neden lazım (W37):</b> platform iptal sonucunda hangi fişin iptal edildiğini
    /// bilmiyordu ve oturum+terminal ile arıyordu; aynı oturumda önce KAPANMIŞ bir fişin tahsilatı
    /// da ters kayda gidebiliyordu. K-33 ile o satırlar para taşıdığı için bu artık görünür bir
    /// hata olurdu.</para>
    ///
    /// <para><b>Bağ SİLİNMEDEN ÖNCE okunur.</b> Silindikten sonra okumak her zaman <c>null</c>
    /// verirdi — alan var ama hep boş, yani sessizce işe yaramaz.</para>
    /// </summary>
    string? CancelledTicketId = null,

    string? ErrorCondition = null,
    string? Reason = null,
    string? ProviderResultCode = null);

/// <summary>İptal isteği ayrıştırma sonucu.</summary>
public abstract record ReversalParseResult
{
    private ReversalParseResult() { }

    public sealed record Ok(ReversalRequest Request) : ReversalParseResult;

    public sealed record Invalid(SaleToPoiRejectReason Reason, string Detail) : ReversalParseResult;
}
