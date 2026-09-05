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
    string? ProviderPluginId = null,

    /// <summary>
    /// Mali kalem dökümü (§20.2 `fiscal` bloğu). <b>TR ÖKC'de ZORUNLU</b> — cihaz kalemsiz komut
    /// kabul etmez (açık soru H). Yurt dışındaki banka terminallerinde kullanılmaz ve <c>null</c>
    /// kalır; bu yüzden sözleşmede opsiyoneldir, ama TR taşımasında yokluğu **fail-closed** ret
    /// sebebidir — tahmin edilmiş bir kalem dökümü yanlış departmana mali kayıt yazardı.
    /// </summary>
    IReadOnlyList<FiscalLine>? FiscalLines = null,

    /// <summary>
    /// Ödeme türü. <b>Platform yalnız KART gönderir</b> (§20-I, ürün kararı 2026-09-05): ödeme
    /// komutu sözleşmesinde tip alanı yoktur, dolayısıyla varsayılan tek geçerli değerdir.
    ///
    /// <para>Alan yine de duruyor ve <b>kaldırılmamalı</b>: fişte nakit ya da karekod ödemesi
    /// <i>bulunabilir</i> (kasiyer terminalden eklemiş olabilir) ve iptal yolu ödeme tipine göre
    /// ayrılır — banka bacağı yoksa <c>VoidAll</c> doğrudan temizler, varsa ters işlem gerekir
    /// (§8.3d). Yani platformun ne istediği ile fişte ne bulunduğu farklı sorulardır.</para>
    /// </summary>
    int PaymentType = GmpPaymentTypes.Card);

/// <summary>
/// Mali fişe yazılacak tek satır (§20.2). Departman numarası <b>burada yok</b>: o eşleme cihaz
/// kurulumuna ait ve eklenti tarafında yaşıyor — platform kararlı kimliği (<see cref="ProductId"/>)
/// gönderir, eşlemeyi taşıma katmanı yapar. Ada göre eşleme yasak: ad değişince eşleme sessizce
/// kopar ve yanlış departmana yazar.
/// </summary>
public sealed record FiscalLine(
    string ProductId,
    string Name,
    int Quantity,
    long UnitPriceMinor,
    /// <summary>KDV oranı (yüzde). Bizim ürün verimizden gelir, cihaz kurulumundan değil.</summary>
    decimal VatRate,
    long LineDiscountMinor = 0);

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

/// <summary>
/// Terminalin ödeme modeli — <b>pazara göre değişir ve belirsizlik çözümünü değiştirir.</b>
///
/// <para><b>Türkiye:</b> kalemler girildikten sonra ödeme <b>parça parça</b> eklenebilir
/// (20 ₺ nakit + 30 ₺ kart …); tutar tamamlanınca fiş kapanır. Dolayısıyla <b>kısmen ödenmiş açık
/// bir fiş NORMAL bir aradurumdur</b>, arıza değil.</para>
///
/// <para><b>Yurt dışı:</b> böyle bir şey yok. Tutarın tamamı tek seferde karta gönderilir ve
/// alınır. Kısmen ödenmiş fiş burada <b>anomalidir</b>.</para>
///
/// <para><b>Neden sözleşmede duruyor:</b> "fiş tamamen ödendi mi" sorusu Türkiye'de yanlış sorudur
/// ve yanlış cevap verir — kasiyerin ikinci ödemeyi ekleyeceği bir fişte "ödeme gerçekleşmedi"
/// denirse aynı tahsilat ikinci kez denenir. Doğru soru her iki pazarda da aynıdır:
/// <b>"BENİM ödemem işlendi mi?"</b></para>
/// </summary>
public enum PaymentModel
{
    /// <summary>Türkiye — aynı fişe birden çok ödeme eklenir.</summary>
    Incremental,

    /// <summary>Yurt dışı — tek ödeme, tam tutar, fiş kapanır.</summary>
    SingleShot,
}

/// <summary>Belirsiz kalan <b>tek bir ödemenin</b> akıbeti.</summary>
public enum ProbeVerdict
{
    /// <summary>Ödeme terminalde <b>işlendi</b>. Para hareket etti.</summary>
    Landed,

    /// <summary>Ödeme <b>işlenmedi</b> — kanıtlandı, varsayılmadı. Güvenle tekrarlanabilir.</summary>
    NotLanded,

    /// <summary>Cevap alınamadı ya da yorumlanamadı. <b>Tekrar YASAK</b>, insana gider.</summary>
    Indeterminate,
}

/// <summary>
/// Bir ödemenin akıbeti sorgusunun sonucu.
///
/// <para><see cref="RemainingMinor"/> <b>arıza göstergesi değildir</b>: artımlı modelde sıfırdan
/// büyük kalması beklenen durumdur (kasiyer kalanı ekleyecek). Tek-seferlik modelde ise sıfırdan
/// büyük kalması incelenmesi gereken bir durumdur.</para>
/// </summary>
public sealed record PaymentProbe(
    ProbeVerdict Verdict,
    long? ApprovedAmountMinor = null,
    long RemainingMinor = 0,
    string? Rrn = null,
    string? CardLast4 = null,
    string? Note = null);

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
    int PaymentCount = 0)
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

    /// <summary>Terminaldeki açık fişi okur — ham durum.</summary>
    Task<TicketState> ReadTicketAsync(CancellationToken ct = default);

    /// <summary>
    /// <b>Belirsizliğin tek çözüm yolu:</b> "BENİM ödemem işlendi mi?" diye sorar.
    ///
    /// <para>Bilerek <see cref="ReadTicketAsync"/>'ten ayrıdır. "Fiş tamamen ödendi mi" sorusu
    /// Türkiye'de <b>yanlış sorudur</b>: kasiyerin ikinci ödemeyi ekleyeceği kısmen ödenmiş bir
    /// fişte "ödeme gerçekleşmedi" cevabı üretir ve aynı tahsilat ikinci kez denenir. Cevabı
    /// taşıma katmanı verir çünkü <b>kendi ödeme modelini</b> ve varsa ödeme öncesi anlık
    /// görüntüsünü yalnız o bilir.</para>
    /// </summary>
    Task<PaymentProbe> ProbeAsync(SaleRequest request, CancellationToken ct = default);

    /// <summary>
    /// Canlılık kontrolü (<c>FP3_Echo</c>). <b>Ping KULLANILMAZ</b> — cihaz ICMP'ye cevap vermiyor
    /// (§8.3c, ölçüldü). Seyrek çağrılmalı: cihaz tek oturumlu.
    /// </summary>
    Task<bool> EchoAsync(CancellationToken ct = default);
}
