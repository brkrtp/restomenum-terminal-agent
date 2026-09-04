namespace Restomenum.Agent.Core;

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

    /// <summary>Terminalde <b>açık fiş var</b>. Cihazın tek koruması budur.</summary>
    public const uint AlreadyDone = 2331;

    /// <summary>
    /// <b>"Fişte BANKA ödemesi var"</b> — "ödeme var" değil. Nakit ödemeli fiş `VoidAll` ile
    /// sorunsuz iptal edilir (canlı ölçüm: 1986 ms). 2069 yalnız kart bacağında çıkar ve banka
    /// ters işlemi (`VoidPayment`) gerektirir.
    /// </summary>
    public const uint PaymentFound = 2069;

    /// <summary>`PrintBeforeMF` sonrası fiş mali hafızada — artık iptal edilemez.</summary>
    public const uint CannotVoid = 2357;

    /// <summary>Kart okutulmadı / işlem erken sonlandı. Canlı ölçüm: ~37 sn, fiş otomatik iptal.</summary>
    public const uint NoCard = 2085;

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
    string? CardLast4 = null)
{
    public bool IsFullyPaid => TotalAmountMinor > 0 && PaidAmountMinor >= TotalAmountMinor;
    public long RemainingMinor => Math.Max(0, TotalAmountMinor - PaidAmountMinor);

    /// <summary>
    /// Fişte banka bacağı var mı? <b>İptal yolunu bu seçer:</b> nakit doğrudan `VoidAll`, kart
    /// önce `VoidPayment` ister. Yanlış seçim, kart ödemeli fişi asla temizleyememektir.
    /// </summary>
    public bool HasCardLeg => LastPaymentType == GmpPaymentTypes.Card;
}

/// <summary>Ödeme tipleri — <b>DLL seviyesi</b>.</summary>
public static class GmpPaymentTypes
{
    public const int Cash = 1;

    /// <summary>
    /// <b>4, 2 DEĞİL.</b> Proje API'si "1=nakit, 2=banka kartı" der ama DLL'in `ST_PAYMENT`'ı 4
    /// kullanır. İki kodlama karıştırılırsa kart işlemi nakit sanılır ve iptal yolu yanlış seçilir.
    /// Canlı terminalde ve saha loglarında (`typeOfPayment:4`) doğrulandı.
    /// </summary>
    public const int Card = 4;
}

/// <summary>Fişe eklenecek kalem.</summary>
public readonly record struct GmpItem(string Name, long UnitPriceMinor, int Quantity, int DepartmentNo);

/// <summary>Ödeme isteği.</summary>
public readonly record struct GmpPaymentRequest(long AmountMinor, int PaymentType);

/// <summary><see cref="IGmpWrapper.OptionFlags"/> bayrakları.</summary>
[Flags]
public enum GmpEchoFlags : ulong
{
    None = 0,
    Printer = 1 << 0,
    ItemDetails = 1 << 1,
    PaymentDetails = 1 << 2,

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
}
