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
    int PaymentType = GmpPaymentTypes.Card,

    /// <summary>
    /// Satış OTURUM kimliği — açık fişin BU satışa mı ait olduğunu belirler (kısmi tahsilat).
    /// <c>null</c> ise devam yolu KAPALIDIR (eski platform): açık fiş farklı satış sayılır.
    /// Bkz. <see cref="SaleToPoiRequest.SaleSessionId"/>.
    /// </summary>
    string? SaleSessionId = null,

    /// <summary>
    /// Satışın (adisyonun) TOPLAM tutarı — platformun <c>SaleTotalAmountMinor</c>'ı.
    /// Kısmi tahsilatta açık fişe devam etmeden önce cihazdaki fiş toplamıyla KARŞILAŞTIRILIR:
    /// eşit değilse kasiyer kısmi ödemeden sonra kalem eklemiş/silmiş demektir ve devam edilirse
    /// fişe yanlış tutarda ödeme eklenir. <c>null</c> ise doğrulanamaz → devam yolu kapalı.
    /// </summary>
    long? SaleTotalMinor = null,

    /// <summary>Kasiyerin seçtiği bankanın BKM kimliği; <c>null</c> ise cihaz seçer.</summary>
    int? BankBkmId = null);

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
    /// <summary>KDV oranı (yüzde). Bilgi amaçlı taşınır; cihaz oranı departmandan türetir.</summary>
    decimal VatRate,
    /// <summary>
    /// Cihazdaki departman numarası. <b>Eklenti ÇÖZER, agent çözmez</b> (§7.2b).
    ///
    /// <para>Önce agent'ta bir <c>IDepartmentMap</c> vardı ve ürün kimliğini departmana çeviriyordu.
    /// O tasarım yanlıştı: departman eşlemesi <b>cihaz kurulumuna</b> ait ve cihaz kurulumu
    /// sağlayıcının işi (§20.2). Agent'ta tutmak, her sağlayıcının eşlemesini bizim taşımamız
    /// demekti — üstelik <c>IDepartmentMap</c>'in hiçbir uygulaması yoktu ve satış kritik yolda
    /// çözümsüz kalıyordu.</para>
    ///
    /// <para><b>Negatif değer = eşlenmemiş.</b> Terminale gitmeden reddedilir; tahmin edilmiş bir
    /// departman yanlış mali kayıt yazar ve geri alınamaz.</para>
    /// </summary>
    int DepartmentNo,
    /// <summary>
    /// <b>BİRİM BAŞINA</b> indirim (kuruş) — <see cref="UnitPriceMinor"/> ile aynı eksende, çünkü
    /// cihazın <c>ST_ITEM.promotion</c> alanı da birim fiyatın yanında duruyor.
    ///
    /// <para><b>Platform SATIR TOPLAMI gönderir</b> (<c>DiscountAmount ≤ ItemAmount</c>, ikisi de
    /// satır); <see cref="FiscalLineBuilder"/> onu birimlere <b>tam</b> dağıtır — fiyatta olduğu gibi
    /// kalan kuruş kaybolmaz, gerekirse satır bölünür. Dağıtım toplamı korumazsa fişteki indirim
    /// platformdakinden birkaç kuruş sapar ve bu geri alınamaz bir mali kayıttır.</para>
    ///
    /// <para>⚠️ <b>Bugün cihaza GÖNDERİLMİYOR.</b> W63 alanı taşıyor ve doğruluyor;
    /// <c>GmpItem.DiscountMinor</c>'a bağlanması <c>--discount-probe</c> (W60) cihazın
    /// <c>promotion.amount</c>'ı birim mi satır mı saydığını ölçtükten SONRA yapılacak. Ölçmeden
    /// bağlamak, indirimi iki katına çıkarma ya da yarıya düşürme riskidir.</para>
    /// </summary>
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
    string? ProviderResultCode = null,

    /// <summary>
    /// nexo <c>ErrorCondition</c> — <b>kaynağında</b> belirlenir (<see cref="GmpErrorMap"/>), sonuç
    /// sınıfından türetilmez.
    ///
    /// <para><b>Neden ayrı alan:</b> <see cref="TransportOutcome"/> dört kovadır; nexo koşulu ondan
    /// çok daha ince ("host'a ulaşılamadı" ile "sonuç belirsiz" ikisi de <c>Unknown</c> kovasına
    /// düşer ama kasaya ve deftere FARKLI şey söylerler). Sınıftan türetince bu ayrım kaybolur ve
    /// 2086 "belirsiz" diye kaydedilir; oysa ölçülen gerçek "banka hattına ulaşılamadı"dır.</para>
    ///
    /// <para><c>null</c> ise <see cref="SaleToPoiResponseBuilder.MapOutcome"/> kovaya göre bir
    /// varsayılan üretir.</para>
    /// </summary>
    string? ErrorCondition = null,

    /// <summary>
    /// <c>FP3_Payment</c> çağrıldı mı? nexo'da bunu taşıyacak alan yok, ama <b>kasiyere ne
    /// söyleneceğini belirleyen soru bu</b>: çağrılmadıysa "kart çekilmedi" kesindir.
    ///
    /// <para><b>Varsayılan <c>true</c> — bilerek.</b> Yanlış tarafa düşmek serbest değil: birisi
    /// yeni bir dal eklerken bu alanı yazmayı unutursa, "ödeme çağrılmadı" (yani "kart kesinlikle
    /// çekilmedi") diye YANLIŞ bir kesinlik yaymaktansa "çağrıldı" deyip belirsiz kalmak güvenlidir.</para>
    /// </summary>
    bool PaymentInvoked = true,

    /// <summary><see cref="RestomenumReasons"/> sözlüğünden makine-okunur sebep. Serbest metin YASAK.</summary>
    string? Reason = null,

    /// <summary>
    /// Sonucu DEĞİŞTİRMEYEN ama denetim izi bırakan bilgi (<c>Restomenum.info</c>).
    /// <see cref="Reason"/>'dan ayrı tutulur: <c>reason</c> "neden başarısız" der, <c>info</c>
    /// "yolda ne oldu" der. Karıştırılsaydı başarılı bir satış, temizlik yaptı diye bir "sebep"
    /// taşır ve platformda hata gibi görünürdü.
    /// </summary>
    string? Info = null,

    /// <summary>
    /// Cihazdaki fişin toplamı — YALNIZ <c>TICKET_SALE_MISMATCH</c> / <c>AMOUNT_EXCEEDS_REMAINING</c>
    /// retlerinde doldurulur. Başka gövdeye sızmaz: bu iki ret, panelin gördüğü kalan ile cihazın
    /// gördüğü kalan ayrıştığında çıkıyor ve kasiyerin CİHAZIN sayısını görmesi gerekiyor.
    /// </summary>
    long? DeviceTicketTotalMinor = null,

    /// <summary>Cihazdaki fişin kalanı (toplam − tahsil). Bkz. <see cref="DeviceTicketTotalMinor"/>.</summary>
    long? DeviceRemainingMinor = null,

    /// <summary>
    /// <b>W144 — bu satışın aldığı MALİ FİŞ NUMARASI.</b> <c>null</c> = bilinmiyor
    /// (fiş kapanmadı, ya da okuma başarısız oldu).
    ///
    /// <para><b>Neden gerekli:</b> bir denetim ya da uzlaştırma "şu satış hangi mali fiş?" diye
    /// sorduğunda bugüne kadar cevabımız YOKTU. <c>tkt_…</c> bizim ürettiğimiz bir etikettir ve
    /// cihaz onu bilmez; kâğıttaki numara cihazındır ve ikisi arasında hiçbir bağ kaydedilmiyordu.</para>
    ///
    /// <para><b>Nasıl elde ediliyor — ve neden ÇIKARMA var:</b> <c>FP3_Echo</c>'nun <c>FisNo</c>
    /// alanı <b>SONRAKİ</b> fiş numarasıdır, sonuncu değil. Ölçüldü (2026-09-12 01:05→01:08):
    /// satıştan önce 3, kâğıda basılan numara 3, satıştan sonra 4. Dolayısıyla kesilen fişin
    /// numarası <c>okunan − 1</c>.</para>
    ///
    /// <para><b>Neden fiş KAPANDIKTAN SONRA okunuyor:</b> numara mali kesinleşmede tüketiliyor
    /// (boş <c>Start</c>→<c>Close</c> yoklaması tüketmiyor — 2026-09-12 00:58'de ölçüldü).
    /// Okumayı baskı zincirinin İÇİNE koymak, kasanın 180 sn'lik kapısına yeni bir başarısızlık
    /// noktası eklerdi; kapanıştan sonra okumak o riski taşımıyor.</para>
    ///
    /// <para>⚠️ Okuma cihaz kilidi HÂLÂ BİZDEYKEN yapılır. Bırakılsaydı araya giren başka bir
    /// fiş numarayı kaydırır ve yanlış numara kaydedilirdi.</para>
    ///
    /// <para>⚠️ Okuma başarısızsa alan <c>null</c> kalır. Tahmin edilmez: yanlış bir mali numara
    /// kaydetmek, hiç kaydetmemekten kötüdür.</para>
    ///
    /// <para><b>W151 — numara TEK BAŞINA kimlik değil.</b> Fiş numarası her Z raporunda
    /// sıfırlanır (ölçüldü: Z=87'de 5'e ulaştı, Z=92'de 3 okundu, arada onlarca fiş kesildi).
    /// Kimlik <see cref="FiscalReceiptId"/> üçlüsüdür; üçü aynı <c>FP3_Echo</c> yanıtından
    /// gelir ve ya HEP BİRLİKTE vardır ya hiç yoktur.</para>
    /// </summary>
    FiscalReceiptId? FiscalReceipt = null,

    /// <summary>
    /// <b>W79 — kasanın İSTEDİĞİ tutar.</b> <see cref="ApprovedAmountMinor"/> ile aynı değerdir;
    /// ayrı alan olarak yazılır ki okuyanın hangi sayının ne olduğunu TAHMİN etmesi gerekmesin.
    /// </summary>
    long? RequestedAmountMinor = null,

    /// <summary>
    /// <b>W79 — cihazın GERÇEKTEN tahsil ettiği tutar.</b> Ölçüm 2026-09-09: banka uygulaması fişe
    /// "İŞYERİ İND." basıp toplamı ~%5 düşürdü (7150→6793, 990→941, 1390→1321); dördünde de kasa
    /// istenen tutarı doğru sandı ve <b>defter şişik kaldı</b>.
    ///
    /// <para>⚠️ <see cref="ApprovedAmountMinor"/> İSTENEN olarak KALIR — platform onaylı sonuçta
    /// bildirilen tutar denemenin tutarına eşit değilse sonucu HİÇ kaydetmiyor (ölçüldü:
    /// <c>statusSink.js:236-240</c>). Tahsil edileni oraya yazmak ıraksamayı düzeltmez, sonucu
    /// tamamen kaybettirirdi.</para>
    ///
    /// <para><c>null</c> = güvenilir biçimde okunamadı (iki kaynak çelişti) — o hâlde hiçbir tutar
    /// İDDİA EDİLMEZ.</para>
    /// </summary>
    long? CollectedAmountMinor = null,

    /// <summary>
    /// <b>W79 — üye işyeri (banka) indirimi</b> = istenen − tahsil, YALNIZ pozitifse konur.
    /// Negatif fark (tahsil > istenen) ayrı ve daha ciddi bir olaydır: alan konmaz, uyarı yazılır.
    /// </summary>
    long? MerchantDiscountMinor = null,

    /// <summary>
    /// Ödemeden SONRA fişin durumu: <c>"OPEN"</c> (kısmi — fiş açık kaldı) ya da <c>"CLOSED"</c>
    /// (tam ödeme → baskı → <c>FP3_Close</c> rc=0).
    ///
    /// <para><b>Neden yanıtta:</b> platform satırları fiş KAPANINCA tek seferde yazacak. Kısmi
    /// ödemede satır yazmak, sonradan iptal edilen bir fişin satırlarını geri almayı gerektirirdi;
    /// kasa da "bu ödeme deftere geçti mi" sorusunu ancak bu alanla cevaplayabilir.</para>
    ///
    /// <para><c>CLOSED</c> YALNIZ <c>Close</c> başarılıysa yazılır — baskı/kapatma yarım kalmışsa
    /// fiş hâlâ açıktır ve öyle bildirilir.</para>
    /// </summary>
    string? TicketState = null,

    /// <summary>
    /// Fişin kalıcı kimliği (<c>Restomenum.ticketId</c>) — fiş açılışında üretilir, ömrü boyunca
    /// sabittir. Platform bir fişin ödemelerini kapanış bildirimiyle bununla eşleştirir.
    /// Bağ yoksa (ör. cihazda başkasından kalmış fiş) <c>null</c> — uydurulmaz.
    /// </summary>
    string? TicketId = null,

    /// <summary>
    /// Fiş KAPANDIYSA o fişin ödemeleri — <b>bizim <c>paymentId</c>'lerimizle</b>, kendi
    /// defterimizden. Cihazın listesi değil: cihaz bizim kimliğimizi bilmez ve başarısız
    /// denemeleri de kayıt olarak tutar.
    ///
    /// <para><c>null</c> = fiş kapanmadı ya da kimlik yok. Boş liste = kapandı ama kayıtlı
    /// ödememiz yok — ikisi AYRI beyandır.</para>
    /// </summary>
    IReadOnlyList<TicketPaymentRow>? ClosedTicketPayments = null,

    /// <summary>
    /// Ödemede GERÇEKTEN kullanılan bankanın BKM kimliği — cihazın yankısından okunur.
    ///
    /// <para><b>Ölçülemezse alan KONMAZ</b>: banka bacağı hiç oluşmadıysa (bugünkü 2086 gibi) ya
    /// da yankıdaki satırın tutarı bizimkiyle tutmuyorsa <c>null</c> kalır. "Banka bilinmiyor" ile
    /// "banka Garanti" ayrı beyanlardır; 0 ya da tahmini bir değer koymak paneli yanlış bankaya
    /// inandırırdı.</para>
    /// </summary>
    int? UsedBankBkmId = null,

    /// <summary>
    /// Fiş kapanırken cihazın gördüğü TAHSİL toplamı. Bizim satırlarımızın toplamı DEĞİL: fark
    /// varsa deftere yazılmayan bir tahsilat var demektir ve bunu platform ancak iki sayıyı
    /// karşılaştırarak görebilir.
    /// </summary>
    long? DevicePaidMinor = null,

    /// <summary>
    /// İptalde: kaç ödeme geri alındı. Fiş bazlı iptalde <c>TicketVoidResult</c> bunu taşıyordu;
    /// ÖDEME bazlı iptalde kasa aynı bilgiyi göremiyordu ve "ne iptal edildi" sorusu cevapsız
    /// kalıyordu. Tek üretici, iki hedef: aynı sayı hem kasaya hem deftere gider.
    /// </summary>
    int? VoidedPaymentCount = null,

    /// <summary>İptalde: geri alınan toplam tutar (kuruş).</summary>
    long? VoidedAmountMinor = null,

    /// <summary>
    /// İptalde: cihazda GERÇEKTEN iptal edilen fişin sahibi satış oturumu. İsteği yapan oturum
    /// DEĞİL — kasiyer başka bir kasadan kalmış fişi iptal etmiş olabilir ve platform doğru
    /// oturumun satırlarını düşürmeli. Bağ yoksa <c>null</c>: "bilmiyorum" da bir cevaptır.
    /// </summary>
    string? CancelledSaleSessionId = null,

    /// <summary>
    /// <b>W104</b> — baskı/kapatma zincirinde hangi adımda kalındı (<c>"PrintBeforeMF"</c>,
    /// <c>"PrintMF"</c>, <c>"Close"</c>…). Cihaz çağrısının ADI, olduğu gibi; çevrilmez.
    /// <c>null</c> = zincir tamamlandı.
    ///
    /// <para><b>Neden <see cref="Reason"/>'dan ayrı:</b> <c>reason</c> sözlükten gelen kapalı bir
    /// küme (serbest metin YASAK) ve "hangi adım" bilgisini oraya sıkıştırmak sözlüğü çağrı adı
    /// kadar dallandırırdı. Kasa <c>reason</c> ile <b>ne yapacağını</b>, bu alanla <b>neyi
    /// söyleyeceğini</b> bilir.</para>
    /// </summary>
    string? PrintFailedStep = null,

    /// <summary>
    /// <b>W104</b> — o adımın HAM dönüş kodu (ör. <c>"0xF003"</c>). Yorumlanmaz.
    /// Üreticinin kendi tavsiyesi: <i>"Always show return code and corresponding message"</i>.
    /// </summary>
    string? PrintFailedCode = null);

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

    /// <summary>
    /// Ödeme <b>cihazın fişine yazılmadı</b> — kanıtlandı, varsayılmadı.
    ///
    /// <para>⚠️ <b>"Güvenle tekrarlanabilir" HER ZAMAN doğru değil.</b> Önceden öyle yazıyordu; ölçüm
    /// bunu daralttı (2026-09-07). İki ayrı durum var ve ayrımı <see cref="PaymentProbe.ErrorCondition"/>
    /// taşır:</para>
    /// <list type="bullet">
    ///   <item><c>Refusal</c> — banka açıkça reddetti (ör. 2202 "İŞLEM ONAYLANMADI"). Para hiç
    ///   hareket etmedi; tekrar GÜVENLİ.</item>
    ///   <item><c>UnreachableHost</c> — bankadan cevap gelmedi ("NO RESPONSE"). Fişe yazılmadığı
    ///   KESİN ama bankada yetim provizyon kalmış olabilir; tekrar güvenli DEĞİL, insan bakmalı.</item>
    /// </list>
    /// <para>İkisini "işlenmedi" diye tek torbaya koymak, kasiyere "gönül rahatlığıyla tekrar çek"
    /// dedirtip çift tahsilat üretebilirdi.</para>
    /// </summary>
    NotLanded,

    /// <summary>Cevap alınamadı ya da yorumlanamadı. <b>Tekrar YASAK</b>, insana gider.</summary>
    Indeterminate,
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

    /// <summary>
    /// Açık fişi/ödemeyi iptal eder. <b>Yol ödeme tipine göre ayrılır</b>: nakit ve mobil doğrudan
    /// <c>VoidAll</c>; kart bacağı banka ters işlemi ister.
    ///
    /// <para>Arayüze taşındı çünkü kasadaki "Fiş İptal" düğmesi buraya bağlanıyor. Uygulamalar
    /// kendi korumalarını kendileri taşır: <b>tahsil edilmiş parayı kör iptal eden bir uygulama
    /// sözleşmeyi ihlal eder.</b></para>
    /// </summary>
    Task<TransportResult> VoidAsync(CancellationToken ct = default);

    /// <summary>
    /// Cihazdaki AÇIK FİŞİN TAMAMINI iptal eder — kasiyerin "Fiş İptal" düğmesi.
    ///
    /// <para><b>Ödeme bazlı iptalden farkı:</b> referans aranmaz. Kasiyer cihazın başında ve
    /// ekranda gördüğü fişi iptal ediyor; o fiş başka bir kasadan kalmış olabilir. Gerçekten
    /// iptal edilen fişin sahibi sonuçta bildirilir ki platform DOĞRU oturumun satırlarını
    /// düşürsün.</para>
    ///
    /// <para>Açık fiş yoksa hiçbir şeye DOKUNULMAZ ve bu bir hata değildir.</para>
    /// </summary>
    Task<TicketVoidResult> VoidTicketAsync(string terminalId, CancellationToken ct = default);
}

/// <summary>
/// <b>W151 — mali fişin KİMLİĞİ.</b> Türk ÖKC'de bir fiş <c>(EKÜ No, Z No, Fiş No)</c> üçlüsüyle
/// tekildir; fiş numarası her Z raporunda 1'den başlar (ölçüldü 2026-09-15). Üçü aynı
/// <c>FP3_Echo</c> yanıtından okunur: <c>FisNo − 1</c> (alan SONRAKİ numaradır), <c>ZNo</c>,
/// <c>EkuNo</c>. Cihaz serisiyle birlikte platformun tekilleştirme anahtarıdır.
/// </summary>
/// <param name="ReceiptNo">Kesilen fişin numarası (Z içinde).</param>
/// <param name="ZNo">Fişin ait olduğu Z dönemi.</param>
/// <param name="EkuNo">Elektronik kayıt ünitesi numarası.</param>
public sealed record FiscalReceiptId(uint ReceiptNo, uint ZNo, uint EkuNo);
