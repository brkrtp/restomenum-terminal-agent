namespace Restomenum.Agent.Core;

/// <summary>
/// Ödeme öncesi fiş görüntüsünün <b>kalıcı</b> deposu.
///
/// <para>Bellekte tutmak yetmez: süreç kart penceresinde ölürse görüntü de ölür ve yeniden
/// başlatmada "benim ödemem işlendi mi" sorusu cevaplanamaz — çözülebilir bir vaka gereksiz yere
/// insana çıkar. <see cref="CommandStore"/> bunu uygular.</para>
/// </summary>
public interface ITicketSnapshotStore
{
    void SaveSnapshot(string commandId, long totalMinor, long paidMinor, int paymentCount, long? now = null);
    (long TotalMinor, long PaidMinor, int PaymentCount)? ReadSnapshot(string commandId);
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
    /// Fişte <b>banka bacağı</b> var mı? İptal yolunu bu seçer — soru "kart mı" DEĞİL.
    ///
    /// <para>Canlı terminal ölçtü: nakit (1) <b>ve mobil/karekod (16)</b> <c>VoidAll</c> ile
    /// doğrudan temizleniyor (~2 sn, 2069 yok); yalnız kart (4) banka ters işlemi istiyor. Ayrımı
    /// "kart mı" diye kurmak, mobil ödemeyi <c>REVERSAL_FAILED</c> riski taşıyan ve sahada hiç
    /// çalışmamış bir yola sokardı.</para>
    /// </summary>
    public bool HasBankLeg => GmpPaymentTypes.HasBankLeg(LastPaymentType);
}

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

    /// <summary>Kurulu departmanları okur — kurulum doğrulaması ve teşhis için.</summary>
    GmpResult GetDepartments(out string departmentsJson);

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
