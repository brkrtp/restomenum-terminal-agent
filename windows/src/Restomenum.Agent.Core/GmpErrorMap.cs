namespace Restomenum.Agent.Core;

/// <summary>
/// Terminal çağrısının hangi <b>adımda</b> olduğunu söyler. Eşleme buna bakar çünkü
/// "para hareket etmiş olabilir mi" sorusunun cevabını hata kodu değil, <b>adım</b> belirler.
/// </summary>
public enum GmpStep
{
    /// <summary><c>FP3_Payment</c> HENÜZ çağrılmadı (Start/TicketHeader/OptionFlags/ItemSale).</summary>
    BeforePayment,

    /// <summary><c>FP3_Payment</c> çağrıldı; sonucu ne olursa olsun <b>para hareket etmiş olabilir</b>.</summary>
    Payment,

    /// <summary>Ödeme sonrası (baskı/kapatma/iptal). Para <b>zaten</b> hareket etmiştir.</summary>
    AfterPayment,
}

/// <summary>
/// GMP-3 dönüş kodu → nexo <c>ErrorCondition</c> eşlemesinin <b>TEK</b> yeri.
///
/// <para><b>Neden tek yer:</b> bu eşleme iki yerde yaşadığı sürece biri düzeltilir, öteki kalır.
/// Ölçülen zarar tam olarak buydu: <c>GmpTerminalTransport</c>'ta 2086 için doğru (belirsiz) kural
/// vardı ama <b>kapsanmayan her kod</b> için son satır <c>Declined</c> diyordu; canlı terminal
/// <c>FP3_Payment</c>'tan <b>2085</b> döndürdü, kural yakalamadı ve deneme kasaya
/// <c>Refusal</c> = "kart reddedildi" diye kapandı.</para>
///
/// <para><b>Değişmez (W1):</b> "bilmiyorum" ASLA "kesin hayır"a çevrilmez. <c>Refusal</c> bu tabloda
/// <b>HİÇBİR</b> GMP-3 kodundan üretilmez — çünkü elimizdeki hiçbir kod "banka host'u cevap verdi ve
/// açıkça reddetti" demiyor. Üreticinin kendi belgesi de bunu söylüyor
/// (<c>GMP3_ErrorHandling_EN_v2.docx</c>): kapsamadığın her kodda <c>FP3_GetTicket</c> ile
/// <b>duruma bak</b>, sonucu varsayma.</para>
///
/// <para><b>Adım neden belirleyici:</b> <c>FP3_Payment</c> çağrılmadan önce dönen bir hatada para
/// hareket EDEMEZ — bunu GMP-3 belgesi değil, kendi çağrı sıramız kanıtlar (ödeme fonksiyonu hiç
/// çağrılmadı). <c>FP3_Payment</c>'tan dönen hiçbir hata kodu ise "para hareket etmedi" GARANTİSİ
/// vermez; belge 2085/2086 için yalnız "başarısız" der, "cihazda ödeme oluşmadı" demez.</para>
/// </summary>
public static class GmpErrorMap
{
    /// <summary>Eşlemenin tek satırı: sonuç sınıfı + nexo koşulu + para riski.</summary>
    public readonly record struct Entry(
        TransportOutcome Outcome, string ErrorCondition, MoneyRisk Money, string? Reason = null);

    /// <summary>"Para hareket etmiş olabilir mi?"</summary>
    public enum MoneyRisk
    {
        /// <summary>Hayır — <c>FP3_Payment</c> hiç çağrılmadı (kendi çağrı sıramızdan kesin).</summary>
        No,
        /// <summary>Bilinmiyor — ayırt edilemez; kesin-ret DENMEZ, terminale sorulur.</summary>
        Unknown,
        /// <summary>Evet — ödeme cihazda oluşmuş.</summary>
        Yes,
    }

    /// <summary>
    /// Kod + adım → eşleme. <b>Kapsanmayan her kod belirsizdir</b> (üretici belgesinin açık kuralı):
    /// "If these errors <i>and other errors you don't cover</i> are returned, you must check current
    /// situation with FP3_GetTicket and act accordingly."
    /// </summary>
    public static Entry Map(uint code, GmpStep step)
    {
        // ── ADIM KURALI: ödeme çağrıldıysa sonuç ne olursa olsun para riski VARDIR ──────────
        // Belge, FP3_Payment'tan dönen HİÇBİR kod için "cihazda ödeme oluşmadı" demiyor; üstelik
        // 2022'yi anlatırken tersini söylüyor: "When an external application call FP3_Payment
        // function, it can be successful on Ingenico device, even though you don't get result
        // properly." Bu yüzden ödeme adımında kesin-ret ÜRETİLMEZ.
        if (step == GmpStep.Payment)
        {
            return code switch
            {
                // Sınıf `Busy` DEĞİL `Unknown`: ödeme adımında "meşgul" ile "cevap yok" ayrımı
                // davranışı değiştirmiyor (ikisi de yoklamaya gider) ve mevcut çivi `Unknown`
                // bekliyor. Koşul yine de "Busy" — cihaz meşgul, bilinmezlik değil.
                GmpCodes.RecvBusy => new(TransportOutcome.Unknown, "Busy", MoneyRisk.Unknown),

                // 2086 = "ödeme başarısız VE banka uygulamasından özel bir mesaj var". Bu mesaj
                // canlı terminalde banka hattı yokken "BAĞLANTI HATASI" / "NO RESPONSE" oldu.
                // Yani kod, "host'a ulaşılamadı" ile "issuer reddetti"yi AYNI değerin altında
                // topluyor; ayırt edemediğimiz için host'a ulaşılamamış sayarız (nexo 07).
                // Refusal (nexo 08) DEMEK, ulaşılamamış bir host'u "reddetti" diye raporlamaktır.
                GmpCodes.PaymentFailedWithBankCode
                    => new(TransportOutcome.Unknown, "UnreachableHost", MoneyRisk.Unknown),

                // 2085 = "ödeme başarısız VE banka uygulamasından mesaj YOK". Mesaj yoksa issuer
                // yanıtı da yoktur → kesin-ret için gereken kanıt hiç oluşmamıştır. Belgede
                // "cihazda ödeme oluşmadı" ibaresi YOK; bu yüzden belirsiz.
                GmpCodes.PaymentFailed
                    => new(TransportOutcome.Unknown, "InProgress", MoneyRisk.Unknown),

                _ => new(TransportOutcome.Unknown, "InProgress", MoneyRisk.Unknown),
            };
        }

        // ── ÖDEME SONRASI: para zaten hareket etti; sınıf belirsiz, risk "evet" ─────────────
        if (step == GmpStep.AfterPayment)
            return new(TransportOutcome.Unknown, "InProgress", MoneyRisk.Yes);

        // ── ÖDEME ÖNCESİ: FP3_Payment hiç çağrılmadı → para hareket EDEMEZ ─────────────────
        // Buradaki kesinlik GMP-3 belgesinden değil, kendi çağrı sıramızdan gelir. Yine de
        // Refusal ÜRETİLMEZ: "kart reddedildi" mesajı yanlış olurdu — banka hiç devrede değildi.
        return code switch
        {
            // Cihazda açık fiş var → `FP3_Start` reddedildi, yani BU denemede ödeme hiç başlamadı.
            // Bu yüzden cevap ANINDA ve KESİN: `PaymentRestriction`. Belirsiz demek yanlış olurdu —
            // açık fişin içindeki para ÖNCEKİ denemenin sorusudur, bu denemenin değil. Belirsiz
            // deseydik her yeni deneme 24 saat çözüm döngüsünde asılı kalır, kasiyer art arda
            // "operatöre danışın" görürdü (ölçüldü: 2026-09-06 gecesi iki nakit denemesi).
            GmpCodes.AlreadyDone => new(TransportOutcome.Declined, "PaymentRestriction",
                MoneyRisk.No, RestomenumReasons.TicketAlreadyOpen),

            GmpCodes.RecvBusy => new(TransportOutcome.Busy, "Busy", MoneyRisk.Unknown),

            // Belge: "getting this error does not mean that function call was not successful."
            _ when GmpCodes.IsTimeout(code) => new(TransportOutcome.Unknown, "InProgress", MoneyRisk.Unknown),

            // Hat/kablo/GMP.XML yapılandırması — komut PC'den hiç çıkmadı.
            GmpCodes.PortNotOpen or GmpCodes.AckNotReceived
                => new(TransportOutcome.Declined, "UnavailableDevice", MoneyRisk.No),

            // Eşleşme istendi; başka hiçbir fonksiyon çalışmaz.
            GmpCodes.PairingRequired => new(TransportOutcome.Declined, "UnavailableService", MoneyRisk.No),

            // Kasiyer girişi / Z raporu / TSM izni — cihaz komutu işlemedi.
            GmpCodes.CashierEntryRequired or GmpCodes.ZRequired or GmpCodes.NotAllowed
                or GmpCodes.InvalidDateTime or GmpCodes.TransactionPending
                => new(TransportOutcome.Declined, "NotAllowed", MoneyRisk.No),

            // Parametre hatası — bizim gönderdiğimiz gövde yanlış.
            GmpCodes.InvalidEntry => new(TransportOutcome.Declined, "MessageFormat", MoneyRisk.No),

            // Fiş limiti (ItemSale) — kalem eklenemedi, ödeme aşamasına hiç gelinmedi.
            GmpCodes.ReceiptLimitExceeded => new(TransportOutcome.Declined, "PaymentRestriction", MoneyRisk.No),

            // Tanıtıcı geçersiz / aktif işlem yok: belge "FP3_GetTicket çağır, ona göre davran"
            // diyor — yani durum okunmadan sonuç söylenmez.
            GmpCodes.InvalidHandle or GmpCodes.NoHandle
                => new(TransportOutcome.Unknown, "InProgress", MoneyRisk.Unknown,
                    RestomenumReasons.InvalidHandle),

            // KAPSANMAYAN HER KOD → belirsiz. Belgenin açık talimatı; eski `_ => Declined`
            // satırının ölçülen zararı da tam buradaydı.
            _ => new(TransportOutcome.Unknown, "InProgress", MoneyRisk.Unknown),
        };
    }
}

/// <summary>
/// <c>SaleToPOIResponse.Restomenum.reason</c> sözlüğü — <b>sabit, ASCII, makine okunur</b>.
///
/// <para>nexo'da "ödeme fonksiyonu çağrıldı mı" diye bir alan yok; <c>ErrorCondition</c> bunu
/// taşıyamaz. Tasarım §22.8 nexo-dışı alanların ayrı ad alanında taşınmasını şart koştuğu için
/// bilgi <c>Restomenum</c> bloğunda gider. Serbest metin YASAK — platform sözlükle eşleştirir.</para>
/// </summary>
public static class RestomenumReasons
{
    public const string FiscalLinesRequired = "FISCAL_LINES_REQUIRED";
    public const string ProductUnmapped     = "PRODUCT_UNMAPPED";
    public const string AlreadyFiscalized   = "ALREADY_FISCALIZED";
    public const string TicketAlreadyOpen   = "TICKET_ALREADY_OPEN";
    public const string InvalidHandle       = "INVALID_HANDLE";

    /// <summary>Kasanın dikte ettiği ödeme yöntemi cihazda eşlenmemiş; ödeme başlamadı.</summary>
    public const string PaymentMethodUnmapped = "PAYMENT_METHOD_UNMAPPED";

    /// <summary>Eklenti/cihaz yapılandırması eksik ya da çelişkili; ödeme başlamadı.</summary>
    public const string ProviderConfigIncomplete = "PROVIDER_CONFIG_INCOMPLETE";

    /// <summary>Tutar GET'i başarısız (bulunamadı / süresi geçmiş / yetki / ağ); ödeme başlamadı.</summary>
    public const string AmountFetchFailed = "AMOUNT_FETCH_FAILED";

    /// <summary>
    /// Açık fişin toplamı, satışın toplamıyla UYUŞMUYOR — kısmi ödemeden sonra kalem eklenmiş ya da
    /// silinmiş. Devam edilirse fişe yanlış tutarda ödeme yazılır; mali kayıt bozulur, geri alınamaz.
    /// </summary>
    public const string TicketSaleMismatch = "TICKET_SALE_MISMATCH";

    /// <summary>
    /// İstenen tutar fişin KALANINI aşıyor (kalan = toplam − tahsil). Cihaz da reddederdi; biz
    /// terminale gitmeden reddediyoruz ki fiş yarım bir işlemle kilitlenmesin.
    /// </summary>
    public const string AmountExceedsRemaining = "AMOUNT_EXCEEDS_REMAINING";

    /// <summary>
    /// Terminal defterinde bu komut için işlem YOK — <b>kanıtlandı</b> (fiş okundu, ödeme sayacı
    /// kıpırdamadı). Yalnız gerçek okumayla verilir; çıkarımla değil.
    /// <b><c>paymentInvoked</c> burada <c>true</c>'dur</b>: `FP3_Payment` çağrılmıştı, cihazda
    /// oluşmadığı sonradan kanıtlandı. İkisi farklı sorular.
    /// </summary>
    public const string NotLanded = "NOT_LANDED";

    /// <summary>
    /// <b>info</b> (sebep DEĞİL): önceki denemeden kalan ÖDEMESİZ fiş, kanıtla temizlendi ve satışa
    /// devam edildi. Sonuç normal akıştır; bu satır yalnız denetim izidir.
    /// </summary>
    public const string StaleTicketCleared = "STALE_TICKET_CLEARED";

    /// <summary><b>info:</b> fiş iptal edildi (VoidAll → Close tamamlandı).</summary>
    public const string TicketCancelled = "TICKET_CANCELLED";

    /// <summary>
    /// <b>info:</b> "Fiş İptal"e basıldı ama cihazda AÇIK FİŞ YOKTU. Başarısızlık DEĞİL —
    /// iptal edilecek bir şey yoktu ve cihaza dokunulmadı.
    /// </summary>
    public const string TicketNotOpen = "TICKET_NOT_OPEN";

    /// <summary>
    /// <b>info:</b> daha önce ONAYLANDI diye bildirilen bir sonuç GERİ ÇEKİLİYOR.
    ///
    /// <para>2026-09-07'de böyle bir vaka yaşandı: bir komut, kurtarma turunda BAŞKA bir satışın
    /// ödemesini görüp kendini onaylı ilan etti ve deftere olmayan bir tahsilat yazıldı (W14 bunu
    /// kapattı). Onay bir kez deftere girdiğinde sessizce düzeltilemez — platformun bunu çelişki
    /// olarak işleyip operatöre çıkarması gerekir.</para>
    ///
    /// <para><c>paymentInvoked</c> burada <c>true</c>: <c>FP3_Payment</c> gerçekten çağrılmıştı;
    /// geri çekilen şey "para hareket etti" iddiası, "çağırdık mı" değil.</para>
    /// </summary>
    public const string LandedRetracted = "LANDED_RETRACTED";

    /// <summary>
    /// <b>info:</b> ödeme cihazın FİŞİNE yazılmadı — deneme satırı okundu, tutarı 0.
    ///
    /// <para>⚠️ <b><c>reason</c> alanına YAZILMAZ, yalnız <c>info</c>'ya.</b> <c>reason:NOT_LANDED</c>
    /// kurtarma yoklamasının cevabıdır ve platformda <c>notStarted</c> ("kart hiç çekilmedi, gönül
    /// rahatlığıyla tekrar dene") üretir. Burada ise <c>FP3_Payment</c> ÇAĞRILDI ve bankadan cevap
    /// gelmemiş olabilir; aynı kutuya koymak çift tahsilat üretirdi. Sebep ayrı alanda:
    /// <see cref="NoResponse"/> / <see cref="BankDeclined"/>.</para>
    /// </summary>
    public const string NotLandedOnTicket = "NOT_LANDED";

    /// <summary>
    /// <b>reason:</b> bankadan cevap gelmedi (cihaz satırında <c>ErrorMsg "NO RESPONSE"</c>,
    /// kullanılabilir uygulama kodu yok). Fişte para YOK ama bankada yetim provizyon KALABİLİR —
    /// bu yüzden "tekrar dene" DENMEZ; nexo koşulu <c>UnreachableHost</c>.
    /// </summary>
    public const string NoResponse = "NO_RESPONSE";

    /// <summary>
    /// <b>reason:</b> banka açıkça reddetti (uygulama kodu var, ör. 2202 "İŞLEM ONAYLANMADI").
    /// Para hiç hareket etmedi; nexo koşulu <c>Refusal</c>, tekrar güvenli.
    /// </summary>
    public const string BankDeclined = "BANK_DECLINED";

    /// <summary>
    /// İptal YARIM KALDI: ödeme geri alınamadı ya da fiş kapanamadı. Kasiyere söylenmesi gereken
    /// cümle "iptal olmadı" DEĞİL, <b>"durum belirsiz, fişe dokunmayın, yönetici çağırın"</b>dır —
    /// tekrar denemek yarım kalmış bir ters işlemin üstüne ikinciyi bindirebilir.
    /// </summary>
    public const string VoidIncomplete = "VOID_INCOMPLETE";
}
