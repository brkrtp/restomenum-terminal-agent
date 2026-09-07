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
    /// <summary>Defterin anahtarı — iptal edilecek ödemenin kaydı (<c>pay_</c> + 40 hex).</summary>
    string PaymentId,
    /// <summary>Tamamlanmış ödemenin cihaz referansı. <c>null</c> ise vaka "açık fiş iptali".</summary>
    string? OriginalPoiTransactionId,
    /// <summary>Orijinal ödeme çağrısının ServiceID'si (açık fiş dalının referansı).</summary>
    string? OriginalServiceId,
    /// <summary>nexo <c>ReversalReason</c> (ör. <c>MerchantCancel</c>). Bilgi amaçlı.</summary>
    string? ReversalReason,
    DateTimeOffset TimeStamp);

/// <summary>İptal isteği ayrıştırma sonucu.</summary>
public abstract record ReversalParseResult
{
    private ReversalParseResult() { }

    public sealed record Ok(ReversalRequest Request) : ReversalParseResult;

    public sealed record Invalid(SaleToPoiRejectReason Reason, string Detail) : ReversalParseResult;
}
