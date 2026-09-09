namespace Restomenum.Agent.Core;

/// <summary>
/// Ödeme öncesi fiş görüntüsünün <b>kalıcı</b> deposu.
///
/// <para>Bellekte tutmak yetmez: süreç kart penceresinde ölürse görüntü de ölür ve yeniden
/// başlatmada "benim ödemem işlendi mi" sorusu cevaplanamaz — çözülebilir bir vaka gereksiz yere
/// insana çıkar. <see cref="CommandStore"/> bunu uygular.</para>
/// </summary>
/// <summary>
/// Ürünün beyan edilen KDV oranı ile fişe basılan bölüm oranının AYRIŞTIĞI bir kalem (K-30).
///
/// <para><b>Bu bir ret değil, BEYAN.</b> Kullanıcı kararı (2026-09-07): <i>"Eklenti ürünlerin kdv
/// oranını değiştirmesin zaten, terminal uygulaması bizim beyanımıza göre işlesin."</i> Mali belge
/// ÖKC fişidir ve fişin oranını bölüm belirler; katalogdaki oran menü verisidir. İşletme "bu bölüm
/// %20" diyorsa fiş %20 basılır.</para>
///
/// <para><b>Ama sapma SESSİZ kalmaz:</b> ayrışan her kalem burada sayılır ve yanıtla birlikte
/// platforma gider. Önceki davranış (satışı reddetmek) fişle defteri hizada tutuyordu ama
/// işletmeyi çalıştırmıyordu; sessizce geçirmek ise hizasızlığı görünmez kılardı. Üçüncü yol:
/// geçir ve BİLDİR.</para>
///
/// <para>İki oran da <b>baz puan</b> (2000 = %20). Birim alan adında yazılı — bugün üç ayrı yerde
/// birim karışıklığı yaşandı, adın kendisi belge olsun.</para>
/// </summary>
public readonly record struct TaxMismatch(
    string ProductCode, int ProductRateBasisPoints, int DepartmentRateBasisPoints, int DepartmentIndex);

/// <summary>Bir fişe düşmüş ödeme kaydı — cihazın tablosu değil, BİZİM defterimiz.</summary>
/// <param name="MethodType">DLL ödeme tipi: 1=nakit, 4=kart, 16=mobil/QR.</param>
public readonly record struct TicketPaymentRow(
    string PaymentId, long AmountMinor, int MethodType, int? BankBkmId);

/// <summary>Kapanmış ama bildirimi henüz outbox'a devredilmemiş fiş.</summary>
public readonly record struct ClosedTicketRow(
    string TicketId, string TerminalId, string? SaleSessionId, long? TotalMinor, long? PaidMinor);

public interface ITicketSnapshotStore
{
    /// <param name="saleSessionId">
    /// Bu komutun satis oturumu. <b>Sahiplik kaniti:</b> yoklama, cihazdaki acik fisin BU komuta
    /// ait oldugunu ancak bununla dogrulayabilir. Yoksa "sayac artmis" gozlemi baskasinin
    /// odemesine ait olabilir.
    /// </param>
    void SaveSnapshot(string commandId, long totalMinor, long paidMinor, int paymentCount,
        string? saleSessionId = null, long? now = null);

    (long TotalMinor, long PaidMinor, int PaymentCount, string? SaleSessionId)? ReadSnapshot(string commandId);

    /// <summary>
    /// Cihazda AÇIK bırakılan fişin hangi satış oturumuna ait olduğunu yazar.
    ///
    /// <para><b>Neden diskte:</b> kısmi ödemeden sonra ajan yeniden başlarsa bağ bellekte kalsaydı
    /// kaybolurdu ve kalan tahsilat yapılamazdı — fiş açık, kimse sahiplenemiyor. Bu bağ olmadan
    /// "açık fiş benim satışıma mı ait" sorusu cevaplanamaz ve tek güvenli cevap "hayır"dır.</para>
    /// </summary>
    /// <returns>
    /// Fişin <b>kalıcı kimliği</b>. Fiş açılışında üretilir, ömrü boyunca sabittir ve yeniden
    /// başlatmaya dayanır — platform bir fişin ödemelerini kapanış bildirimiyle bununla eşleştirir.
    /// Aynı fişe ikinci kez bağlanmak kimliği değiştirmez.
    /// </returns>
    string BindOpenTicket(string terminalId, string saleSessionId, long? now = null);

    /// <summary>Açık fişin sahibi satış oturumu; bağ yoksa <c>null</c>.</summary>
    string? ReadOpenTicketBinding(string terminalId);

    /// <summary>Açık fişin kimliği; bağ yoksa <c>null</c>.</summary>
    string? ReadOpenTicketId(string terminalId);

    /// <summary>
    /// Bir fişe düşen ödemeyi <b>bizim <c>paymentId</c>'mizle</b> kaydeder.
    ///
    /// <para><b>Neden gerekli:</b> fiş kapanınca deftere hangi denemelerin yazılacağı sorusunu
    /// cihaz cevaplayamaz — cihaz tutarı ve tipi bilir, bizim kimliğimizi bilmez. Sıraya bakıp
    /// eşleştirmek de olmaz: cihaz BAŞARISIZ denemeleri de fişte kayıt olarak tutuyor (ölçüldü
    /// 2026-09-07: banka hattı yokken kart bacağı <c>payAmount=0</c> ile fişte duruyor), yani
    /// "cihazın n'inci ödemesi = bizim n'inci denememiz" sessizce kayardı.</para>
    /// </summary>
    void RecordTicketPayment(string ticketId, string paymentId, long amountMinor,
        int methodType, int? bankBkmId, long? now = null);

    /// <summary>Bir fişin kaydedilmiş ödemeleri — kaydedilme sırasına göre.</summary>
    IReadOnlyList<TicketPaymentRow> ReadTicketPayments(string ticketId);

    /// <summary>
    /// Fişin KAPANDIĞINI, bildirimi henüz gitmemiş olarak yazar. <b>Bağ silinmeden ÖNCE.</b>
    ///
    /// <para><b>Neden gerekli:</b> fiş kapanınca bağ silinmek zorunda — bırakılırsa aynı oturumun
    /// bir sonraki satışı kapanmış fişin kimliğini devralır. Ama bildirim gövdesi üst katmanda
    /// kuruluyor; silme ile outbox'a yazma arasında süreç ölürse geriye replay edecek KİMLİK
    /// kalmaz ve o fişin ödemeleri deftere hiç yazılmaz. Bu kayıt tam o aralığı kapatır.</para>
    /// </summary>
    void MarkTicketClosed(string ticketId, string terminalId, string? saleSessionId,
        long? totalMinor, long? paidMinor, long? now = null);

    /// <summary>Kapanmış ama bildirimi outbox'a düşmemiş fişler — açılışta kurtarılır.</summary>
    /// <summary>Kapanış bildirimi HİÇ yazılamamış fişler. <paramref name="enGecKapanis"/> verilirse
    /// yalnız o damgadan ESKİ olanlar (W76 yaş kapısı; gerekçe <c>CommandStore</c>'da).</summary>
    IReadOnlyList<ClosedTicketRow> PendingClosedTickets(long? enGecKapanis = null);

    /// <summary>Kapanış gövdesi outbox'a yazıldı; gönderim garantisi artık outbox'ta.</summary>
    void ConfirmTicketClosed(string ticketId, long? now = null);

    /// <summary>Bağı siler — fiş kapandığında (tam ödeme) ya da iptal edildiğinde.</summary>
    void ClearOpenTicketBinding(string terminalId);
}

/// <summary>
/// GMP-3 dönüş kodları. <b>Yalnız davranışı değiştirenler</b> burada — tam katalog sarmalayıcıda
/// değil, hata kataloğunda yaşar.
/// </summary>
public static class GmpCodes
{
    public const uint Ok = 0x0000;

    /// <summary>Yanıt gelmedi (`CommTimeOut`, GMP.XML'de 90 sn). <b>Para hareket etmiş OLABİLİR.</b></summary>
    public const uint Timeout = 0xF003;
    /// <summary>Timeout'un ikinci biçimi — aynı muamele.</summary>
    public const uint Timeout2 = 0xF007;

    /// <summary>
    /// "Sana şu an cevap veremiyorum." <b>"Hiçbir şey olmadı" DEĞİL</b> — sahada tam olarak kart
    /// ödemesi tamamlandıktan sonra geldi (§8.3d).
    /// </summary>
    public const uint RecvBusy = 0xF01C;

    public const uint PairingRequired = 0xF020;
    public const uint IncorrectDevice = 2334;
    public const uint ChecksumMismatch = 2338;

    /// <summary>
    /// Terminalde <b>açık fiş var</b>. Cihazın tek koruması budur.
    ///
    /// <para>⚠️ Bu değer önce <c>2331</c> diye <b>tahmin edilmişti ve YANLIŞTI.</b> Kaynakta
    /// (<c>GMPSmartDLL.cs</c> Defines) ve saha loglarında doğrulanan değer <b>2080 (0x820)</b>.
    /// Yanlış kalsaydı <c>ReadTicket</c>'in tanıtıcısız yolu sessizce çalışmaz, yani agent yeniden
    /// başladıktan sonra belirsizlik çözümü <b>tam da en gerekli anda</b> çökerdi.</para>
    /// </summary>
    public const uint AlreadyDone = 2080;

    /// <summary>Seri/TCP portu açılamadı.</summary>
    public const uint PortNotOpen = 0xF000;

    /// <summary>
    /// <b>"Fişte BANKA ödemesi var"</b> — "ödeme var" değil. Nakit ödemeli fiş `VoidAll` ile
    /// sorunsuz iptal edilir (canlı ölçüm: 1986 ms). 2069 yalnız kart bacağında çıkar ve banka
    /// ters işlemi (`VoidPayment`) gerektirir.
    /// </summary>
    public const uint PaymentFound = 2069;

    /// <summary>`PrintBeforeMF` sonrası fiş mali hafızada — artık iptal edilemez.</summary>
    public const uint CannotVoid = 2357;

    /// <summary>
    /// <b>Ödeme başarısız, ek hata kodu YOK</b> (<c>APP_ERR_PAYMENT_NOT_SUCCESSFUL_AND_NO_MORE_ERROR_CODE</c>).
    ///
    /// <para>İsmi "kart yok" sanmak yanıltıcıdır — anlamı "işlem başarısız ve elimde ayrıntı yok".
    /// Canlı terminalde kart hiç okutulmadığında bu kod geldi (~37 sn sonra, fiş otomatik iptal).</para>
    /// </summary>
    public const uint PaymentFailed = 2085;

    /// <summary>
    /// <b>Ödeme başarısız ve BANKA hata kodu var</b>
    /// (<c>APP_ERR_PAYMENT_NOT_SUCCESSFUL_AND_MORE_ERROR_CODE</c>). Canlı terminalde banka hattı
    /// yokken alındı: "BAĞLANTI HATASI", "NO RESPONSE", "İŞLEM ONAYLANMADI".
    ///
    /// <para>2085'ten ayrı tutulur çünkü <b>sebep farklıdır</b> ve kasiyere gösterilecek mesaj da
    /// farklı olmalı. İkisinde de para <b>hareket etmemiştir</b>.</para>
    /// </summary>
    public const uint PaymentFailedWithBankCode = 2086;

    // ── Aşağıdakiler yalnız HATA EŞLEMESİNİ (GmpErrorMap) besler; akışı dallandırmazlar.
    //     Anlamları GMP3_ErrorHandling_EN_v2.docx'ten birebir alındı — tahmin yok.

    /// <summary>Parametre hatalı doldurulmuş (`APP_ERR_FISCAL_INVALID_ENTRY`).</summary>
    public const uint InvalidEntry = 2009;

    /// <summary>Ödeme bitmeden fiş kapatılmaya çalışıldı (`APP_ERR_APL_COMPLETE_PAYMENT`).</summary>
    public const uint CompletePaymentRequired = 2021;

    /// <summary>Kasiyer girişi yapılmamış (`APP_ERR_CASHIER_ENTRY_REQUIRED`).</summary>
    public const uint CashierEntryRequired = 2053;

    /// <summary>TSM izni yok (`APP_ERR_NOT_ALLOWED`).</summary>
    public const uint NotAllowed = 2064;

    /// <summary>Fiş limiti aşıldı — `ItemSale`'den döner (`APP_ERR_FIS_LIMITI_ASILAMAZ`).</summary>
    public const uint ReceiptLimitExceeded = 2067;

    /// <summary>Cihazda aktif fiş varken mali olmayan fonksiyon çağrıldı (`..._GMP3_TRANSACTION_IS_PENDING`).</summary>
    public const uint TransactionPending = 2097;

    /// <summary>Cihaz tarihi dış sistemle uyumsuz (`APP_ERR_GMP3_INVALID_DATE_TIME`).</summary>
    public const uint InvalidDateTime = 2310;

    /// <summary>Fiş tanıtıcısı geçersiz (`APP_ERR_GMP3_INVALID_HANDLE`). Belge: `FP3_GetTicket` ile duruma bak.</summary>
    public const uint InvalidHandle = 2317;

    /// <summary>Cihazda aktif işlem yok (`APP_ERR_GMP3_NO_HANDLE`).</summary>
    public const uint NoHandle = 2341;

    /// <summary>Satış için Z raporu gerekiyor (`APP_ERR_GMP3_Z_REQUIRED`).</summary>
    public const uint ZRequired = 2417;

    /// <summary>Hat/kablo/GMP.XML yapılandırması eksik (`DLL_RETCODE_ACK_NOT_RECEIVED`).</summary>
    public const uint AckNotReceived = 0xF01B;

    public static bool IsTimeout(uint c) => c == Timeout || c == Timeout2;
}

/// <summary>Ham dönüş. Sarmalayıcı <b>yorumlamaz</b>; yorum çağıranındır.</summary>
public readonly record struct GmpResult(uint Code)
{
    public bool Ok => Code == GmpCodes.Ok;
    public static implicit operator GmpResult(uint c) => new(c);
    public override string ToString() => $"0x{Code:X4}";
}

/// <summary>Fişin cihazdaki hâli (`ST_TICKET`'ın bizi ilgilendiren alanları).</summary>
public readonly record struct GmpTicket(
    long TotalAmountMinor,
    long PaidAmountMinor,
    int PaymentCount,
    /// <summary>Son ödemenin tipi. <b>1 = nakit, 4 = kart</b> (canlı terminalde doğrulandı).</summary>
    int LastPaymentType,
    string? Rrn = null,
    string? CardLast4 = null,

    /// <summary>
    /// Son ödeme kaydına cihazın YAZDIĞI hata kodu (<c>ST_PaymentErrMessage</c>).
    /// Canlı ölçüm (2026-09-07): banka hattı yokken <c>FP3_Payment</c> <b>2086</b> döndürdü ama
    /// fişteki kayda <b>"2085"</b> yazıldı — yani dönüş kodu ile fişe yazılan kod AYNI DEĞİL.
    /// Bu alan issuer yanıt kodu DEĞİLDİR; uygulama kodudur.
    /// </summary>
    string? LastPaymentErrorCode = null,

    /// <summary>
    /// Son ödeme kaydının hata METNİ. Canlı ölçüm: <c>"ÖDEME İSTEĞİ İLETİLMEDİ"</c> — cihazın
    /// kendi defterinde "istek host'a gitmedi" beyanı. Kasiyere gösterilecek tek doğru cümle budur;
    /// üreticinin tavsiyesi de bu (<i>"Always show return code and corresponding message"</i>).
    /// </summary>
    string? LastPaymentErrorText = null,

    /// <summary>
    /// <c>ST_PaymentErrMessage.AppErrorCode</c> — <see cref="LastPaymentErrorCode"/>'dan AYRI
    /// tutuluyor çünkü cihazın hangi alanı doldurduğu ÖLÇÜLMEDİ. Birleştirseydik "kod boş" ile
    /// "iki alan da boş" ayırt edilemez ve teşhis kör kalırdı.
    /// </summary>
    string? LastPaymentAppErrorCode = null,

    /// <summary><c>ST_PaymentErrMessage.AppErrorMsg</c> — ayrı tutuluyor, birleştirme teşhisi kör eder.</summary>
    string? LastPaymentAppErrorText = null,

    /// <summary>
    /// Cihazın DOLDURDUĞU ödeme satırları — tamamı DEĞİL, <c>numberOfPaymentsInThis</c> kadarı.
    /// <see cref="PaymentsAreComplete"/> bu listenin fişin TAMAMI olup olmadığını söyler.
    /// </summary>
    IReadOnlyList<GmpPaymentLine>? Payments = null,

    /// <summary>
    /// <see cref="Payments"/> fişin TAMAMINI mı taşıyor (<c>numberOfPaymentsInThis ==
    /// totalNumberOfPayments</c>)?
    ///
    /// <para><b>Ölçüm (2026-09-07 18:00, GMP izi):</b> <c>FP3_Payment</c>'ın döndürdüğü fişte
    /// <c>totalNumberOfPayments=3</c> ama <c>numberOfPaymentsInThis=1</c>'di ve dizinin İLK İKİ
    /// kaydı SIFIRDI — cihaz yalnız SON kaydı doldurmuş. Aynı fişi <c>FP3_GetTicket</c> ile
    /// okuyunca <c>2/2</c> geldi ve ikisi de doluydu.</para>
    ///
    /// <para>Bu ayrım şart: doldurulmamış bir kaydı "0 TL'lik ödeme" saymak, deftere olmayan
    /// satırlar yazdırırdı. Boş kayıt "ödeme yok" değil, "bu çağrıda gelmedi" demek.</para>
    /// </summary>
    bool PaymentsAreComplete = false,

    /// <summary>
    /// <c>ST_TICKET.TotalReceiptDiscount</c> — fişteki indirim toplamı (kuruş).
    ///
    /// <para><b>Neden <see cref="TotalAmountMinor"/>'dan AYRI:</b> cihazın fiş toplamının indirimi
    /// düşülmüş (net) mi yoksa düşülmemiş (brüt) mi döndüğü belgede YOK. İkisini tek alana
    /// katlasaydık ölçemezdik. <c>DevamDogrula</c>'nın tam-eşitlik kıyası bu ayrıma bağlı:
    /// cihaz net, platform brüt gönderirse indirimli her adisyonda ikinci kısmi ödeme
    /// <c>TICKET_SALE_MISMATCH</c> ile reddedilirdi.</para>
    /// </summary>
    long TotalDiscountMinor = 0,

    /// <summary><c>ST_TICKET.TotalReceiptTax</c> — fişteki KDV toplamı (kuruş). İndirimin matrahı
    /// düşürüp düşürmediği yalnız bu alanın indirim öncesi/sonrası değerinden okunabilir.</summary>
    long TotalTaxMinor = 0,

    /// <summary>
    /// <b>Cihazın FİŞE BASTIĞI satırlar</b> (<c>ST_TICKET.stPrinterCopy</c>). <c>null</c> = gelmedi.
    ///
    /// <para><b>Neden var (2026-09-09):</b> "indirim fişte çıktı mı" sorusunu saatlerce
    /// cevaplayamadık; elimizde yalnız toplamlar vardı, kâğıt ise kullanıcıdaydı. Oysa cihaz
    /// bastığı satırları <b>zaten gönderiyordu</b> — <c>OptionFlags(Reload)</c> içindeki
    /// <c>ECHO_PRINTER</c> biti sayesinde veri her fişte geliyor ve biz onu <b>atıyorduk</b>.
    /// Sahadan okunan gerçek örnek:</para>
    /// <code>
    /// "Espresso               %10      *9,90 "
    /// "İNDİRİM                         *0,99-"
    /// </code>
    /// <para><b>Ek cihaz çağrısı YOK</b> — bayrak zaten gönderiliyordu, yalnız yanıtı okuyoruz.</para>
    /// </summary>
    IReadOnlyList<string>? PrinterLines = null,

    /// <summary><c>ST_TICKET.totalNumberOfPrinterLines</c> — cihazın bildirdiği TOPLAM satır sayısı.
    /// <see cref="PrinterLines"/> bundan kısaysa kopya EKSİKTİR; "iki satır bastı" ile "iki satırını
    /// gördük" ayrı şeyler (aynı ayrım ödeme satırlarında bir kez ısırdı).</summary>
    int PrinterLineCount = 0)
{
    public bool IsFullyPaid => TotalAmountMinor > 0 && PaidAmountMinor >= TotalAmountMinor;
    public long RemainingMinor => Math.Max(0, TotalAmountMinor - PaidAmountMinor);

    /// <summary>
    /// Fişte <b>banka bacağı</b> var mı? İptal yolunu bu seçer — soru "kart mı" DEĞİL.
    ///
    /// <para>Canlı terminal ölçtü: nakit (1) <b>ve mobil/karekod (16)</b> <c>VoidAll</c> ile
    /// doğrudan temizleniyor (~2 sn, 2069 yok); yalnız kart (4) banka ters işlemi istiyor. Ayrımı
    /// "kart mı" diye kurmak, mobil ödemeyi <c>REVERSAL_FAILED</c> riski taşıyan ve sahada hiç
    /// çalışmamış bir yola sokardı.</para>
    /// </summary>
    public bool HasBankLeg => GmpPaymentTypes.HasBankLeg(LastPaymentType);
}

/// <summary>
/// Fişteki tek bir ödeme kaydı — cihazın gördüğü hâliyle.
///
/// <para><b><c>AmountMinor == 0</c> BAŞARISIZ denemedir, "sıfır liralık ödeme" değil.</b> Ölçüldü
/// (2026-09-07 18:00): banka hattı yokken kart denemesi fişte kayıt AÇIYOR — tip 4, banka adı ve
/// BKM kimliği dolu (GARANTİ BBVA/62, AKBANK/46), <c>payAmount 0</c>, hata alanları dolu
/// ("NO RESPONSE" / 2202 "TERMINAL KAPALI"). Cihazın <c>totalNumberOfPayments</c> sayacı bu
/// denemeleri de sayar. Deftere satır yazarken tutarı 0 olanlar DIŞARIDA bırakılmalı.</para>
/// </summary>
public readonly record struct GmpPaymentLine(
    /// <summary>Ödeme tipi: 1=nakit, 4=kart, 16=mobil/QR.</summary>
    int Type,
    /// <summary>Tahsil edilen tutar (kuruş). <b>0 = deneme başarısız.</b></summary>
    long AmountMinor,
    /// <summary>Bankanın BKM kimliği (kart bacağı); yoksa <c>null</c>. Başarısız denemede de dolabilir.</summary>
    int? BankBkmId = null,
    /// <summary>Cihazın yazdığı banka adı (ör. "GARANTİ BBVA"); yoksa <c>null</c>.</summary>
    string? BankName = null,
    /// <summary>Bu bacağın hata metni (ör. "NO RESPONSE", "TERMINAL KAPALI"); yoksa <c>null</c>.</summary>
    string? ErrorMessage = null,

    /// <summary>
    /// Banka uygulamasının hata kodu (<c>stPaymentErrMessage.AppErrorCode</c>).
    ///
    /// <para><b>Neden ayrı taşınıyor:</b> "banka açıkça reddetti" ile "bankadan cevap gelmedi"
    /// ayrımı buradan kuruluyor. Ölçüldü (2026-09-07, iki vaka): Akbank bacağında
    /// <c>"2202"</c> + <c>"İŞLEM ONAYLANMADI"</c> (açık ret), Garanti bacağında <c>"0000"</c> +
    /// <c>"(00000000)-DEFAULT"</c> + <c>ErrorMsg "NO RESPONSE"</c> (cevap yok). Yani
    /// <c>"0000"</c> "hata yok" DEĞİL, "kullanılabilir kod yok" demek.</para>
    /// </summary>
    string? AppErrorCode = null);

/// <summary>Ödeme tipleri — <b>DLL seviyesi</b>.</summary>
public static class GmpPaymentTypes
{
    public const int Cash = 1;

    /// <summary>
    /// Mobil / karekod ödeme (<c>PAYMENT_MOBILE</c>, 0x10). <b>Canlı terminalde doğrulandı:</b>
    /// tam satış 7940 ms; kısmi ödemeli fişte <c>VoidAll</c> <b>doğrudan OK</b> (1959 ms, 2069 YOK).
    /// Yani iptal davranışı nakde benzer, karta değil.
    /// </summary>
    public const int Mobile = 16;

    /// <summary>
    /// <b>4, 2 DEĞİL.</b> Proje API'si "1=nakit, 2=banka kartı" der ama DLL'in `ST_PAYMENT`'ı 4
    /// kullanır. İki kodlama karıştırılırsa kart işlemi nakit sanılır ve iptal yolu yanlış seçilir.
    /// Canlı terminalde ve saha loglarında (`typeOfPayment:4`) doğrulandı.
    /// </summary>
    public const int Card = 4;

    /// <summary>
    /// Ödemenin <b>banka bacağı</b> var mı? İptal yolunu belirleyen soru budur — "kart mı" değil.
    ///
    /// <para>Canlı terminal bunu ölçtü: nakit (1) <b>ve mobil/karekod (16)</b> <c>VoidAll</c> ile
    /// doğrudan temizleniyor (~2 sn, 2069 yok); yalnız kart (4) banka ters işlemi istiyor. Ayrımı
    /// "kart mı" diye kurmak, mobil ödemeyi gereksiz yere ters işlem yoluna sokardı — ki o yol
    /// sahada hiç çalışmamış ve <c>REVERSAL_FAILED</c> riski taşıyor.</para>
    /// </summary>
    public static bool HasBankLeg(int paymentType) => paymentType == Card;

    /// <summary>Cihazın tanıdığı bir ödeme tipi mi (1/4/16). Eşleme dosyasındaki uydurma bir değer
    /// (ör. 99) terminale gitmeden yakalanır — bilinmeyen tip yanlış işlem/iptal yolu üretir.</summary>
    /// <summary>Yemek çeki (<c>PAYMENT_YEMEKCEKI</c>, 0x08). ⚠️ DLL notu: <b>"Uygulama varsa"</b> — cihazda ilgili ödeme uygulaması kurulu olmalı.</summary>
    public const int MealVoucher = 8;

    /// <summary>Hediye çeki (<c>PAYMENT_HEDIYE_CEKI</c>, 0x20).</summary>
    public const int GiftVoucher = 32;

    /// <summary>Puan (<c>PAYMENT_PUAN</c>, 0x200).</summary>
    public const int Points = 512;

    /// <summary>Havale/banka transferi (<c>PAYMENT_BANKA_TRANSFERI</c>, 0x800).</summary>
    public const int BankTransfer = 2048;

    /// <summary>Çek (<c>PAYMENT_CEK</c>, 0x1000).</summary>
    public const int Cheque = 4096;

    /// <summary>
    /// Bu tip <b>ödeme olarak gönderilebilir mi?</b> (W54)
    ///
    /// <para>⚠️ <b>"Gönderilebilir" ≠ "iptal edilebilir".</b> İptal yolu tipe göre ayrılıyor ve
    /// ölçülmüş olanlar yalnız üçü: nakit → <c>VoidAll</c> doğrudan · kart → <c>2069</c>, banka
    /// ters işlemi gerekir · QR/mobil → nakde benzer (<c>VoidAll</c> doğrudan, 2026-09-07 ölçümü).
    /// <b>Yeni beş tipin iptal davranışı ÖLÇÜLMEDİ</b>; uygulama tabanlıysa <c>2069</c> gelip
    /// <c>VoidPayment</c> dalına düşebilirler ve o dal sahada hiç çalışmadı.</para>
    ///
    /// <para>Listede OLMAYANLAR bilinçli: <c>PAYMENT_IKRAM</c> (64), <c>PAYMENT_ODEMESIZ</c> (128),
    /// <c>PAYMENT_KAPORA</c> (256), <c>PAYMENT_ACIK_HESAP</c> (8192) — bunlar para tahsil etmiyor
    /// ya da ayrı karar bekliyor; <c>PAYMENT_CASH_CURRENCY</c> (2, döviz) ise ajan her tutarı
    /// <b>TL</b> gönderdiği için (949 sabit, kur profili bağlı değil) eklenirse yanlış mali kayıt
    /// üretirdi.</para>
    /// </summary>
    public static bool IsKnown(int paymentType) => paymentType
        is Cash or Card or Mobile
        or MealVoucher or GiftVoucher or Points or BankTransfer or Cheque;
}

/// <summary>
/// Fiş tipleri (<c>TTicketType</c>). <b>Satış için tek doğru değer <see cref="Sale"/>'dir.</b>
/// </summary>
public static class GmpTicketTypes
{
    /// <summary>
    /// ⚠️ <c>TTasnifDisi</c>. <b>SATIŞTA KULLANILAMAZ.</b> İlk uygulamam bunu gönderiyordu ve fiş
    /// <b>hiç açılamıyordu</b>: canlı terminalde aynı oturumda ölçüldü —
    /// <c>TicketHeader(0)</c> → <b>0x0008 EKÜ_PROBLEM</b>, <c>TicketHeader(1)</c> → <b>0x0000 OK</b>.
    ///
    /// <para>Sahte sarmalayıcıyla görünmedi çünkü sahte katman EKÜ'yü modellemiyor — bu hata
    /// yalnız gerçek donanımda ortaya çıkabilirdi.</para>
    /// </summary>
    public const int TasnifDisi = 0;

    /// <summary>
    /// <c>TProcessSale</c> — satış fişi. Mevcut sertifikalı <c>DLLController</c> da satış için
    /// bunu kullanıyor; canlı terminalde uçtan uca satış bununla sürüldü.
    /// </summary>
    public const int Sale = 1;
}

/// <summary>Fişe eklenecek kalem.</summary>
public readonly record struct GmpItem(string Name, long UnitPriceMinor, int Quantity, int DepartmentNo,
    /// <summary>
    /// Kalem indirimi (kuruş). <b>0 = indirim yok</b> ve bu durumda cihaza giden
    /// <c>ST_ITEM.promotion</c> alanı <c>null</c> kalır — bugünkü davranışın ta kendisi.
    ///
    /// <para>Neden kalemin İÇİNDE: cihazın <c>ST_ITEM</c> yapısında zaten
    /// <c>promotion { type, amount, ticketMsg }</c> alanı var (<c>GmpInterop.cs:762</c>) ve biz onu
    /// bugün <c>null</c> gönderiyoruz. Ayrı bir <c>FP3_Minus</c> turu kalem başına ~350-500 ms
    /// eklerdi; indirim zaten gönderdiğimiz gövdede taşınabiliyor.</para>
    /// </summary>
    long DiscountMinor = 0,
    /// <summary>Fişe basılacak indirim metni (<c>promotion.ticketMsg</c>). Yalnız
    /// <see cref="HasDiscount"/> iken anlamlı.</summary>
    string? DiscountText = null)
{
    /// <summary>
    /// Cihaza <c>ST_ITEM.promotion</c> nesnesi kurulacak mı?
    ///
    /// <para><b>Karar burada, sarmalayıcıda DEĞİL</b> — çünkü sarmalayıcı P/Invoke katmanı ve
    /// birim testi yok; oradaki bir kayma hiçbir teste yakalanmazdı (mutasyonla ölçüldü: koşulu
    /// <c>&gt;= 0</c> yapmak hiçbir testi kırmıyordu). Kural veriyle birlikte, testlenebilir
    /// yerde duruyor.</para>
    ///
    /// <para><b>Neden sıfırda kurulmuyor:</b> boş bir <c>promotion</c> göndermek cihaza
    /// "0 TL indirim var" demek olur ve fişe satır bastırabilir. İndirim yokken alan <c>null</c>
    /// kalmalı — bugünkü davranış (izde <c>Type:Null</c> olarak görünüyor).</para>
    /// </summary>
    public bool HasDiscount => DiscountMinor > 0;
}

/// <summary>
/// <b>W73 — cihazın fişe bastığı satırların okunması.</b> Pencere seçimi ve maskeleme burada,
/// sarmalayıcıda DEĞİL: <c>GmpWrapper</c> P/Invoke katmanı ve birim testi yok — orada duran bir
/// kural sessizce bozulabilir (mutasyonla ölçüldü: satırları düşürmek hiçbir testi kırmıyordu).
/// Aynı gerekçeyle <see cref="PaymentWindow"/> de Core'da.
/// </summary>
public static class PrinterCopy
{
    /// <summary>
    /// Cihazın DOLDURDUĞU satırlar. <paramref name="lines"/> dizisi bildirilen sayıdan uzun
    /// olabilir (kapasite); okunacak olan <paramref name="filledCount"/> kadarıdır.
    /// <c>null</c> girdi → <c>null</c> çıktı: "gelmedi" ile "boş geldi" ayrı şeyler.
    /// </summary>
    public static IReadOnlyList<string>? Read(IReadOnlyList<string?>? lines, int filledCount)
    {
        if (lines is null) return null;
        var adet = Math.Clamp(filledCount, 0, lines.Count);
        var liste = new List<string>(adet);
        for (var i = 0; i < adet; i++)
            if (lines[i] is string satir) liste.Add(Maskele(satir));
        return liste;
    }

    /// <summary>
    /// 6+ ardışık rakamı <c>***</c> ile maskeler.
    ///
    /// <para><b>Neden:</b> bu satırlar artık günlüğe gidiyor. Fiş satırlarında kart verisi
    /// beklenmiyor — ama "beklenmiyor" bir güvence değildir; banka uygulamasının fişe ne yazacağını
    /// biz belirlemiyoruz. Tutarlar ayraçlı olduğu için (<c>9,90</c>) etkilenmez.</para>
    /// </summary>
    public static string Maskele(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\d{6,}", "***");
}

/// <summary>
/// Cihaza giden kalem alanlarının sabitleri.
/// </summary>
public static class GmpItemDefaults
{
    /// <summary>
    /// <c>ST_ITEM.promotion.ticketMsg</c> — fişe basılacak indirim metni.
    ///
    /// <para><b>BOŞ, ve bilinçli.</b> Kullanıcı kararı (2026-09-09): <i>"Fişte ne yazacağını zaten
    /// sen belirleyemezsin, terminal otomatik çıkarır."</i> Platform da metin göndermiyor
    /// (sözleşmede <c>DiscountLabel</c> YOK). Buraya kendi uydurduğumuz bir metni koymak, mali
    /// belgeye bizim yazdığımız bir cümleyi bastırmak olurdu.</para>
    ///
    /// <para>Alan <c>null</c> değil <b>boş dize</b>: cihaz sözleşmesinde <c>promotion</c> nesnesi
    /// dolu olduğunda üç alanı da bekliyor; metin alanını hiç göndermemek ile boş göndermek
    /// arasındaki farkı ölçmedik, boş olan güvenli taraf.</para>
    /// </summary>
    public const string DiscountText = "";
}

/// <summary>Ödeme isteği.</summary>
/// <param name="BankBkmId">
/// Bankanın BKM kimliği. <c>null</c> / 0 = <b>cihaz seçsin</b> (DLL alanı 0 kabul ediyor ve
/// bugünkü davranış bu). Ölçüldü (2026-09-07): 0 geçildiğinde cihaz aynı fişte art arda iki
/// denemede FARKLI banka seçti (GARANTİ BBVA 62 → AKBANK 46).
/// </param>
public readonly record struct GmpPaymentRequest(long AmountMinor, int PaymentType, int? BankBkmId = null);

/// <summary><see cref="IGmpWrapper.OptionFlags"/> bayrakları.</summary>
[Flags]
public enum GmpEchoFlags : ulong
{
    None = 0,

    /// <summary><c>GMP3_OPTION_ECHO_PRINTER</c> — cihazın FİŞE BASTIĞI satırları geri döndürür
    /// (<see cref="GmpTicket.PrinterLines"/>).</summary>
    Printer = 1 << 0,

    /// <summary>
    /// <c>GMP3_OPTION_ECHO_PAYMENT_DETAILS</c> (<c>GmpInterop.cs</c>, bit 1).
    ///
    /// <para>⚠️ <b>Adlar bir zamanlar TERS eşlenmişti</b> (W74): bu bit <c>ItemDetails</c> diye
    /// adlandırılmıştı, bit 2 de <c>PaymentDetails</c>. Zararsızdı çünkü her yerde
    /// <see cref="Reload"/> (üçü birden) gönderiliyor — ama biri tek başına kullansaydı istediğinin
    /// tersini alırdı ve bunu hiçbir şey söylemezdi.</para>
    /// </summary>
    PaymentDetails = 1 << 1,

    /// <summary><c>GMP3_OPTION_ECHO_ITEM_DETAILS</c> (<c>GmpInterop.cs</c>, bit 2).</summary>
    ItemDetails = 1 << 2,

    /// <summary>
    /// Fişi <b>güvenilir</b> okumak için gereken küme. Tek başına <c>GetTicket</c> ödeme/kalem
    /// detayını eksik döndürebilir ve <see cref="GmpTicket.PaidAmountMinor"/> boş kalır — kurtarma
    /// mantığı tam da o alana dayandığı için eksik okuma <b>yanlış karar</b> demektir.
    /// </summary>
    Reload = Printer | ItemDetails | PaymentDetails,
}

/// <summary>
/// **Sertifikalı ince sarmalayıcı** (§8.3b, karar C). <b>Mali karar İÇERMEZ.</b>
///
/// <para>Her metot tek bir <c>FP3_*</c> çağrısıdır: ham dönüş kodu ve varsa fiş durumu döner,
/// <b>yorumlamaz</b>. Hangi çağrı, hangi sırayla, hangi kodda ne yapılacağı — hepsi çağıranda
/// (<see cref="GmpTerminalTransport"/>).</para>
///
/// <para><b>Neden bu kadar aptal:</b> sertifikasyon binary hash'ini kapsıyor, yani buradaki her
/// değişiklik yeniden sertifikasyon turu demek. Karar mantığı burada olsaydı bir kurtarma dalını
/// düzeltmek bile sertifikasyona takılırdı — mevcut uygulamada tam olarak bu oluyor (1374 satırlık
/// controller sertifikalı binary'nin içinde).</para>
///
/// <para><b>İki yasak:</b></para>
/// <list type="number">
///   <item><b>Gizli retry YOK.</b> <see cref="GmpCodes.RecvBusy"/> yutulup tekrar denenmez, yukarı
///   taşınır. İçeride tekrar denemek, paranın hareket ettiği anda çift tahsilattır.</item>
///   <item><b>Kilit YOK.</b> Seri hâle getirme agent'ın işi; burada olsaydı bir kilit hatası
///   düzeltmesi de sertifikasyon isterdi.</item>
/// </list>
///
/// <para>Çağrılar <b>senkrondur</b> çünkü P/Invoke gerçekten blokedir; <c>Payment</c> 90 saniyeye
/// kadar bloke eder. Sahte bir async yüzey bu gerçeği gizlerdi.</para>
/// </summary>
public interface IGmpWrapper
{
    GmpResult Start(out ulong handle);
    GmpResult TicketHeader(ulong handle, int ticketType);
    GmpResult OptionFlags(ulong handle, GmpEchoFlags flags);
    GmpResult ItemSale(ulong handle, GmpItem item, out GmpTicket ticket);

    /// <summary>Kartlı ödemede 20–32 sn bloke eder (saha ölçümü).</summary>
    GmpResult Payment(ulong handle, GmpPaymentRequest request, out GmpTicket ticket);

    GmpResult GetTicket(ulong handle, out GmpTicket ticket);
    GmpResult PrintTotalsAndPayments(ulong handle);
    GmpResult PrintBeforeMF(ulong handle);
    GmpResult PrintUserMessage(ulong handle);
    GmpResult PrintMF(ulong handle);
    GmpResult VoidAll(ulong handle, out GmpTicket ticket);

    /// <summary>
    /// Banka ters işlemi. ⚠️ <b>İMZA TAHMİNİDİR</b> — dokuz saha logunun hiçbirinde geçmiyor ve
    /// canlı terminalde fiziksel kart olmadan tetiklenemedi. Ölçülmüş bir süre bütçesi <b>yoktur</b>.
    /// Kart iptali testi yapılana kadar bu metodu ölçülmüş bir davranışmış gibi kullanma.
    /// </summary>
    GmpResult VoidPayment(ulong handle, int paymentIndex);

    GmpResult Close(ulong handle);
    GmpResult Echo();

    /// <summary>
    /// Cihazda KURULU odeme (banka) uygulamalarini okur — <c>FP3_GetPaymentApplicationInfo</c>.
    ///
    /// <para><b>HAM JSON doner.</b> Alanlarin anlami (ozellikle <c>Status</c>/<c>AppType</c>/
    /// <c>AppFlag</c>) ne uretici belgesinde ne de kodda tanimli; yorumlamak icin once olcmek
    /// gerekiyor. Bu yuzden sarmalayici cozumlemez, olculen seyi oldugu gibi verir.</para>
    /// </summary>
    GmpResult GetPaymentApplicationsRaw(out string json, out int total, out int received, byte requested = 20);

    // ── EŞLEŞTİRME (provisioning) ────────────────────────────────────────────
    //
    // ⚠️ Bu iki çağrı **önce sözleşme dışında bırakılmıştı** ("provisioning ayrı bir yaşam
    // döngüsü") ve o karar YANLIŞTI. Canlı ölçüm tersini gösterdi:
    //
    //  · Eşleşme **sürece bağlı**: sertifikasız yeni bir süreçten `Echo` OK dönerken `Start`
    //    `0xF020 PAIRING_REQUIRED` verdi — bağlantı var, yetki yok.
    //  · Eşleşme **tek slotlu**: harness eşleşince EXE 1'in `StartTicket`'i `2346` (durum
    //    çakışması) verdi. Kullanıcı da doğruladı: cihaz aynı anda tek programa eşli kalır.
    //  · Yeni süreç **kendi** `StartPairingInit`'ini çağırıp eşleşebildi (0x0).
    //
    // Sonuç: eşleştirmeyi YAPAN, işlem yapacak sürecin kendisi olmak zorunda. Dışarıdan
    // TETİKLENEBİLİR (agent "şimdi eşleş" der) ama çağrı sertifikalı sürecin içinde olmalı.
    //
    // **Sertifikasyondan ÖNCE eklenmesi şart:** sertifikalanan şey binary hash'idir; bu iki metodu
    // sonradan eklemek yeni bir sertifikasyon turu demektir.

    /// <summary>
    /// Eşleştirmeyi başlatır (<c>FP3_StartPairingInit</c>). Sahada ~8–9 sn sürüyor.
    ///
    /// <para><b>Parametreler sabit DEĞİL:</b> mevcut sertifikalı kod <c>ProcOrderNumber</c> ve
    /// <c>EcrSerialNumber</c>'ı istekten alıyor. Sarmalayıcıya sabitleseydik ve banka/TSM
    /// kurulum başına benzersiz değer isteseydi, düzeltme bir sertifikasyon turu olurdu.
    /// Marka/model sabit kalabilir — sarmalayıcı zaten Ingenico'ya özgü.</para>
    ///
    /// <para><paramref name="info"/> eşleşme yanıtından cihaz kimliğini döndürür;
    /// <c>REVERSAL_FAILED</c> vakasında elle iade referansı buradan toplanır.</para>
    /// </summary>
    GmpResult Pair(GmpPairingConfig config, out GmpDeviceInfo info);

    /// <summary>
    /// Eşleşme tamam mı (<c>FP3_IsGmpPairingDone</c>).
    /// <b>Dönüşü standart retcode DEĞİL</b>: 0 = eşleşme yok, ≠0 = var.
    /// </summary>
    GmpResult CheckPairing(out bool paired);

    // ── YÜZEY DONDURMA KARARLARI (2026-09-05) ────────────────────────────────
    //
    // Sertifikalanan şey binary hash'idir: **eksik bir metodu sonradan eklemek yeni bir tur ve
    // sahada yeniden eşleştirme demektir.** Karar ölçütü bu asimetri:
    //
    //   · Kullanmadığımız bir metodun maliyeti ≈ SIFIR (ince geçiş, mantık taşımıyor).
    //   · Eksik bir metodun maliyeti = bir sertifikasyon turu + saha kesintisi.
    //
    // Bu yüzden "gerekebilir" olanlar dahil edildi, "modelimiz bunu hiç yapmıyor" olanlar
    // gerekçesiyle dışarıda bırakıldı.

    /// <summary>
    /// Mali rapor (<c>FP3_FunctionReports</c>) — Z (gün kapatma) ve X (ara rapor).
    ///
    /// <para><b>Neden yüzeyde ZORUNLU:</b> <c>2417 Z_REQUIRED</c> mali gün devrinde
    /// <b>kaçınılmazdır</b> ve her satış Z alınana kadar reddedilir. Yüzeyde olmasaydı, gün
    /// devrinde restoran satış yapamaz ve çözüm ancak birinin terminale gidip elle Z almasıyla
    /// gelirdi — gece yarısı, en kötü anda.</para>
    ///
    /// <para><b>Yetenek burada, KARAR değil.</b> Z'yi ne zaman almanın doğru olduğu bir mali
    /// karardır ve karar katmanına aittir (§8.3b). Sarmalayıcı yalnız "al" der. Bu ayrım sayesinde
    /// politika (otomatik mi, kasiyere sorulacak mı) sertifikasyon turu olmadan değişebilir.</para>
    ///
    /// <para>⚠️ Açık fişle çakışır (<c>2097</c>) — çağıran fişin kapalı olduğundan emin olmalı.</para>
    /// </summary>
    GmpResult Report(GmpReportType type);

    /// <summary>
    /// Terminalin ağ adresini ayarlar.
    ///
    /// <para><b>Neden yüzeyde:</b> adres <b>dinamik ve cihaz başına farklı</b>. Yalnız dağıtım
    /// zamanı config'den okunsaydı, bir DHCP yenilemesi cihazı çalışmaz hâle getirir ve çözüm
    /// dosya düzenleyip yeniden başlatmak olurdu.</para>
    ///
    /// <para>⚠️ <b>ANINDA ETKİLİ DEĞİL.</b> Mevcut sertifikalı uygulama da yalnız <c>GMP.XML</c>'i
    /// düzenliyor, <c>FP3_UpdateInterfaceXmlDataByID</c>'yi <b>çağırmıyor</b> — yani yeni adres
    /// <b>bir sonraki bağlantıda</b> geçerli oluyor. Çağıran bunu bilmeli; "ayarladım, hemen
    /// bağlanır" varsayımı yanlış teşhise yol açar.</para>
    /// </summary>
    GmpResult SetIpAddress(string ipAddress, int port);

    /// <summary>
    /// Fatura bilgisi (<c>FP3_SetInvoice</c>) — VKN/TCKN ve fatura numarası.
    ///
    /// <para><b>Yalnız TR:</b> Türkiye'de fişe opsiyonel olarak vergi kimliği ve fatura numarası
    /// basılabiliyor; yurt dışında böyle bir alan yok (§7.2a). Değerler <b>müşteri kaydından</b>
    /// türetilir, kasiyer elle girmez.</para>
    /// </summary>
    GmpResult SetInvoice(ulong handle, GmpInvoice invoice);

    /// <summary>
    /// Departman/KDV kurulumu (<c>Json_FP3_SetDepartments</c>) — cihaz provisioning'i.
    ///
    /// <para>Ürün → departman eşlemesi <b>bizde değil</b> (§20.2, eklenti tutar); ama cihazın
    /// departmanlarının bir kez <b>kurulması</b> gerekiyor ve agent EXE 2'nin yerine geçtiğinde
    /// bu iş ona kalır. Kurulum sahada başarısız olursa her satış <c>PRODUCT_UNMAPPED</c> ile
    /// düşer — yüzeyde olmaması pahalı.</para>
    /// </summary>
    GmpResult SetDepartments(string departmentsJson, string supervisorPassword);

    /// <summary>
    /// <c>FP3_Minus</c> — TUTAR indirimi (kuruş). <paramref name="indexOfItem"/> kalemi adresler.
    ///
    /// <para><b>Yalnız TEŞHİS için</b> (<c>--discount-probe</c>, W60): satış yolu indirimi
    /// <see cref="GmpItem.DiscountMinor"/> ile kalemin İÇİNDE gönderiyor, ek tur atmıyor. Bu çağrı
    /// "indeks 0 bütün fişe mi uygulanır" gibi belgelenmemiş soruları ÖLÇMEK için var.</para>
    /// </summary>
    GmpResult Minus(ulong handle, long amountMinor, string text, int indexOfItem, out GmpTicket ticket);

    /// <summary>
    /// <c>FP3_Dec</c> — ORAN indirimi. <paramref name="ratePercent"/> cihazda <b><c>byte</c></b>
    /// (0-255), yani <b>tam sayı yüzde</b>; %12,5 gibi kesirli oran İFADE EDİLEMEZ. Cihazın
    /// hesapladığı tutar <paramref name="changedMinor"/> ile geri döner.
    ///
    /// <para><b>Yalnız TEŞHİS için</b> (W60). Satış yolu oranı DEĞİL tutarı gönderir — tam da bu
    /// <c>byte</c> sınırı yüzünden.</para>
    /// </summary>
    GmpResult Dec(ulong handle, int ratePercent, string text, int indexOfItem,
        out long changedMinor, out GmpTicket ticket);

    /// <summary>Kurulu departmanları okur — kurulum doğrulaması ve teşhis için.</summary>
    GmpResult GetDepartments(out string departmentsJson);

    /// <summary>Cihaz vergi oranı tablosu (indeks → baz puan). Departman <c>u8TaxIndex</c>'iyle birleşip
    /// oranı üretir; tabloda karşılık yoksa oran null (uydurma yok).</summary>
    GmpResult GetTaxRates(out string taxRatesJson);

    // ── BİLİNÇLİ OLARAK DIŞARIDA ────────────────────────────────────────────
    //
    // `FP3_VoidItem` (tek kalem iptali): komut modelimiz **atomik** — platform "şu kalemi iptal
    // et" diye bir komut göndermiyor; kalem düzeltmesi POS tarafında yapılır ve terminale
    // düzeltilmiş fiş gider. Modelimiz değişirse (kısmi kalem iptali gereken bir akış çıkarsa)
    // bu bir sertifikasyon turu gerektirir — **bilinçli kabul edilen risk**.
}

/// <summary>
/// Eşleştirme parametreleri. Marka/model sarmalayıcıda sabit (Ingenico'ya özgü); bu ikisi
/// <b>kurulum başına</b> değişebildiği için dışarıdan gelir.
/// </summary>
public sealed record GmpPairingConfig(string ProcOrderNumber, string EcrSerialNumber);

/// <summary>Eşleşme yanıtından cihaz kimliği — elle iade referansı ve teşhis.</summary>
public sealed record GmpDeviceInfo(string Brand, string Model, string Serial, string Version);

/// <summary>Vergi kimliği tipi. <b>Ayrı alanlara yazılır</b> (<c>tck_no</c> / <c>vk_no</c>).</summary>
public enum GmpTaxIdType
{
    /// <summary>Gerçek kişi — TC kimlik numarası.</summary>
    Tckn,

    /// <summary>Tüzel kişi — vergi kimlik numarası.</summary>
    Vkn,
}

/// <summary>
/// Mali fatura bilgisi (<c>FP3_SetInvoice</c>). <b>Yalnız TR.</b>
///
/// <para><b>Neden bu kadar alan:</b> ilk imza <c>(taxNumber, invoiceNo)</c>'ydu ve
/// <b>uygulanamazdı</b> — <c>FP3_SetInvoice</c> açık fişin işlem tanıtıcısını, kaynağı
/// (e-Arşiv/e-Fatura), tutarı, para birimini ve tarihi istiyor. Eksik imzayla sertifikalasaydık
/// metot sahada çalışmaz ve düzeltmesi yeni bir tur olurdu.</para>
///
/// <para><b>TCKN ve VKN ayrı alanlardır</b> — tek bir "taxNumber" hangisi olduğunu söylemiyordu.
/// Yanlış alana yazmak mali faturayı bozar ve geri alınamaz.</para>
///
/// <para>BCD kodlaması (numara alanları) <b>sarmalayıcının işidir</b>: marshalling'dir, karar
/// değil. Çağıran düz metin verir.</para>
/// </summary>
public sealed record GmpInvoice(
    int Source,
    string TaxId,
    GmpTaxIdType TaxIdType,
    string InvoiceNo,
    long AmountMinor,
    ushort Currency,
    DateTime Date);

/// <summary>Mali rapor tipi (<c>FP3_FunctionReports</c>).</summary>
public enum GmpReportType
{
    /// <summary>Ara rapor — mali günü KAPATMAZ.</summary>
    X = 1,

    /// <summary>Gün kapatma. <c>2417 Z_REQUIRED</c>'ın tek çözümü.</summary>
    Z = 2,
}
