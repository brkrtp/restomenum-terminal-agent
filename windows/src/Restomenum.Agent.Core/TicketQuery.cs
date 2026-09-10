using System.Text.Json;
using System.Text.Json.Nodes;

namespace Restomenum.Agent.Core;

/// <summary>
/// Kasadan gelen <b>SALT-OKUNUR fiş durumu sorgusu</b> (<c>MessageCategory: "TicketQuery"</c>).
///
/// <para><b>Neden var (2026-09-10, kasa ölçtü):</b> W104 yarım kalmış fişi kapattıktan sonra
/// masa kapandı, ama köprünün KENDİ kaydı bayat kaldı (<c>ticketState "open"</c>,
/// <c>approvedOpenCount 1</c>). Sonuç: kasiyerin karşısına <b>YANLIŞ</b> bir uyarı çıktı —
/// <i>"Cihazda kapanmamış fiş var; bu kasadan alınan 1 ödeme henüz deftere işlenmedi."</i> —
/// oysa fiş kapanmış, ödeme deftere işlenmişti (990/990, <c>COMPLETED</c>). "İptal"e basan bir
/// kasiyer masayı boş yere açık bırakırdı.</para>
///
/// <para><b>Neden CİHAZA GİTMEZ — ve bu bir tercih değil:</b> "cihazı oku" görünüşte doğru cevap
/// ama <c>FP3_Start</c> açık fiş YOKKEN <b>YENİ FİŞ AÇAR</b>; kapatılsa bile mali numarayı
/// tüketir. Her masa kapanışında bir sorgu, her sorguda bir mali numara demekti. Üstelik cihaza
/// gitmek terminal kilidini alır ve satışı geciktirir. Bu yüzden cevap TAMAMEN DEFTERDEN
/// veriliyor: cihaz I/O yok, kilit yok, yan etki yok, anında.</para>
///
/// <para><b>Defter bu soruya cevap vermeye YETKİLİDİR:</b> "fiş kapandı mı" ve "ödeme deftere
/// işlendi mi" sorularının kaynağı zaten burası — kapanış kaydını yazan da, bildirimi outbox'a
/// devreden de bu defter.</para>
/// </summary>
/// <param name="TicketId">
/// Sorulan fiş. <c>null</c> = "bu terminalde ŞU AN açık fiş var mı" sorusu.
/// Dolu = "BU fişe ne oldu" sorusu — kasa bayat kaydını bununla tazeler.
/// </param>
public sealed record TicketQueryRequest(
    string ServiceId, string SaleId, string PoiId, string? TicketId, DateTimeOffset TimeStamp);

/// <summary>
/// Defterin fiş hakkında bildiği her şey. <b>Her alan "bilmiyorum"u ayrı taşır</b> — bu katmanda
/// boş ile bilinmeyen karıştırılırsa kasa yanlış uyarı üretir, ki zaten olan tam olarak buydu.
/// </summary>
/// <param name="Durum">
/// <list type="bullet">
///   <item><c>"OPEN"</c> — defterde bu terminale bağlı açık fiş var.</item>
///   <item><c>"CLOSED"</c> — bu fişin kapanış kaydı var.</item>
///   <item><c>"NO_OPEN_TICKET"</c> — <b>YALNIZ kimliksiz sorguda:</b> bu ajanın defterinde bu
///   terminale bağlı AÇIK FİŞ YOK.</item>
///   <item><c>"UNKNOWN"</c> — BELİRLİ bir fiş soruldu ve defterde izi YOK (kapanıp saklama
///   süresi dolmuş ya da bu ajanın hiç görmediği bir fiş olabilir).</item>
/// </list>
///
/// <para><b>İkisi neden AYRI değer (W113):</b> ikisi de eskiden <c>UNKNOWN</c>'dı ve kasa haklı
/// olarak sordu: "kimliksiz UNKNOWN, açık fiş yok demek mi?" Tek kelimeyle iki farklı beyanı
/// taşımak, okuyanın hangisi olduğunu TAHMİN etmesini gerektiriyordu — ve yanlış tahminin bedeli
/// ağır: gerçekten açık bir fişi "kapandı" sayıp adisyonu kapatmak.</para>
///
/// <para>⚠️ <b><c>NO_OPEN_TICKET</c> "CİHAZDA açık fiş yok" DEMEK DEĞİLDİR</b>; "BU AJANIN
/// DEFTERİNDE bağlı açık fiş yok" demektir. Sorgu cihaza GİTMEZ (gitse mali numara tüketirdi).
/// Cihazda ajanın bilmediği bir fiş durabilir — cihaz menüsünden açılmış ya da bağı düşmüş.
/// O vaka <c>BayatFisiTemizle</c>'nin işidir ve bir sonraki satışta yakalanır.</para>
/// </param>
/// <param name="LedgerWritten">
/// Kapanış bildirimi outbox'a DEVREDİLDİ mi (<c>notified_at</c> dolu). Kasanın "ödeme deftere
/// işlendi mi" sorusunun tek doğru cevabı bu. <c>null</c> = fiş kapanmamış ya da bilinmiyor.
/// </param>
/// <param name="PendingFinishAttempts">
/// Fiş baskı hatası yüzünden TAMAMLANMAYI BEKLİYORSA kaçıncı denemede. <c>null</c> = beklemiyor.
/// Kasa buradan "arıza sürüyor ama üzerinde çalışılıyor" diyebilir — sessiz bir bekleme değil.
/// </param>
/// <param name="DeviceBusy">
/// <b>W115 — ŞU AN cihazda uçuşta bir iş var mı?</b> (satış, iptal, tamamlama…)
///
/// <para><b>Neden var — bu gecenin kök sebebi:</b> 2026-09-10 02:05'te ajanı yayım için
/// durdurdum ve tam o anda cihazda bir NAKİT ödeme uçuştaydı. Yanıt kayboldu, kasa
/// "bilinmiyor"da kaldı, uzlaştırma turu YENİ bir paymentId ile tekrar denedi ve sonuçta
/// aynı masaya <b>iki ayrı mali fiş, toplam 19,80 TL</b> kesildi. Kasa da o gece kendini
/// altı kez yeniden başlatmış ve hiçbirinde uçuşta iş var mı diye bakmamış.</para>
///
/// <para>Ne olduğunu "kasa boşta görünüyordu" diye TAHMİN ederek anladık; oysa sorulabilir
/// bir olguymuş. Bu alan onu sorulabilir yapıyor: yeniden başlatmadan önce bak, meşgulse
/// bekle. Cihaza gitmez — yalnız işlem kilidinin tutulup tutulmadığına bakar.</para>
///
/// <para>⚠️ <b>Anlık bir fotoğraftır.</b> Sorgudan bir milisaniye sonra iş başlayabilir;
/// "meşgul değil" bir garanti değil, bir gözlemdir. Ama körlemesine durdurmaktan iyidir.</para>
/// </param>
public sealed record TicketQueryResult(
    string Durum, string? TicketId, string? SaleSessionId,
    long? TotalMinor, long? PaidMinor, bool? LedgerWritten,
    int? PendingFinishAttempts, string? PendingFinishStep, string? PendingFinishCode,
    bool DeviceBusy = false,

    /// <param name="PaperWarningAt">
    /// <b>W120 — KÂĞIT UYARISI, fişten BAĞIMSIZ.</b> Cihazın <c>0x0020</c> dediği ANIN damgası
    /// (unix ms). <c>null</c> = uyarı YOK.
    ///
    /// <para><b>Neden ayrı ve kalıcı:</b> kâğıt durumu bir DENEMENİN özelliği değil, CİHAZIN
    /// durumudur. 2026-09-10 03:23'te ölçüldü: baskı <c>0x0020</c> ile patladı, kasaya
    /// <c>OUT_OF_PAPER</c> GİTTİ (kasa gövdeyi gösterdi) — ama süpürme fişi <b>6,1 saniyede</b>
    /// kapattı ve uyarı ekrandan silindi. Kullanıcı kâğıdın bittiğini HİÇ görmedi ve ruloyu
    /// ancak elle müdahale ederken fark etti.</para>
    ///
    /// <para><b>İLK görülme saatidir, sonuncu değil</b> — "ne zamandır kâğıtsız" sorusu, "en son
    /// ne zaman patladı"dan daha değerli.</para>
    ///
    /// <para><b>Yalnız BAŞARILI bir baskı düşürür.</b> Fişin kapanması, denemenin çözülmesi ya da
    /// zamanın geçmesi düşürmez — düşürselerdi uyarı yine kaybolurdu.</para>
    /// </param>
    long? PaperWarningAt = null,

    /// <param name="PaperWarningStep">Uyarının doğduğu çağrı adı (<c>"Start"</c>, <c>"PrintBeforeMF"</c>…).</param>
    string? PaperWarningStep = null);

/// <summary><c>MessageCategory: "TicketQuery"</c> zarfının ayrıştırılması.</summary>
public static class TicketQueryParser
{
    public const string Category = "TicketQuery";

    public static TicketQueryRequest? Parse(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("SaleToPOIRequest", out var env) || env.ValueKind != JsonValueKind.Object
                || !env.TryGetProperty("MessageHeader", out var hdr) || hdr.ValueKind != JsonValueKind.Object)
                return null;

            if (Str(hdr, "MessageCategory") != Category) return null;

            var serviceId = Str(hdr, "ServiceID");
            var poiId = Str(hdr, "POIID");
            if (string.IsNullOrEmpty(serviceId) || string.IsNullOrEmpty(poiId)) return null;

            // `ticketId` isteğe bağlı ve `Restomenum` uzantısında taşınır — zarfın nexo alanlarına
            // bizim kimliğimizi sokmuyoruz (uzantı alanları için ayrılmış yer orası).
            string? ticketId = null;
            if (env.TryGetProperty("Restomenum", out var ek) && ek.ValueKind == JsonValueKind.Object)
                ticketId = Str(ek, "ticketId");

            return new TicketQueryRequest(serviceId!, Str(hdr, "SaleID") ?? "", poiId!, ticketId,
                DateTimeOffset.UtcNow);
        }
    }

    private static string? Str(JsonElement o, string ad) =>
        o.TryGetProperty(ad, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Sorguyu DEFTERDEN cevaplar. <b>Cihaza dokunmaz, kilit almaz.</b></summary>
public static class TicketQueryHandler
{
    /// <summary>
    /// Kimliksiz sorgu: bu ajanın defterinde bu terminale bağlı AÇIK FİŞ YOK.
    /// <b>"Cihazda fiş yok" DEĞİL</b> — sorgu cihaza gitmez.
    /// </summary>
    public const string AcikFisYok = "NO_OPEN_TICKET";

    public static TicketQueryResult Cevapla(CommandStore defter, TicketQueryRequest istek,
        bool cihazMesgul = false)
    {
        // W120: kâğıt uyarısı fişten BAĞIMSIZ — hangi dala düşersek düşelim taşınır.
        var kagit = defter.ReadPaperWarning(istek.PoiId);
        // ── 1. BU TERMİNALDE AÇIK BAĞ VAR MI ──────────────────────────────────────
        var acikFis = defter.ReadOpenTicketId(istek.PoiId);
        var acikOturum = defter.ReadOpenTicketBinding(istek.PoiId);

        // Fiş belirtilmediyse soru "şu an açık fiş var mı".
        var hedef = istek.TicketId ?? acikFis;
        if (hedef is null)
            // Kimliksiz soru + defterde bağ yok = "bağlı açık fiş YOK". Bunu `UNKNOWN` ile aynı
            // kelimeye koymak, kasanın iki ayrı olguyu ayırt edememesi demekti.
            return new TicketQueryResult(AcikFisYok, null, null, null, null, null, null, null, null,
                cihazMesgul, kagit?.GorulmeMs, kagit?.Adim);

        var bekleyen = defter.PendingFinishes(long.MaxValue).FirstOrDefault(x => x.TicketId == hedef);
        int? denemeler = bekleyen.TicketId == hedef ? bekleyen.Attempts : null;

        // ── 2. AÇIK MI ────────────────────────────────────────────────────────────
        if (hedef == acikFis)
            return new TicketQueryResult("OPEN", hedef, acikOturum, null, null, null,
                denemeler, bekleyen.LastStep, bekleyen.LastCode, cihazMesgul,
                kagit?.GorulmeMs, kagit?.Adim);

        // ── 3. KAPANDI MI ─────────────────────────────────────────────────────────
        var kapanis = defter.ReadClosedTicket(hedef);
        if (kapanis is { } k)
            return new TicketQueryResult("CLOSED", hedef, k.Row.SaleSessionId,
                k.Row.TotalMinor, k.Row.PaidMinor, k.Notified,
                denemeler, bekleyen.LastStep, bekleyen.LastCode, cihazMesgul,
                kagit?.GorulmeMs, kagit?.Adim);

        // ── 4. DEFTERDE İZ YOK ────────────────────────────────────────────────────
        // ⚠️ Bu "AÇIK" DEĞİLDİR. Kapanmış ve saklama süresi (7 gün) dolmuş olabilir, ya da bu
        // ajanın hiç görmediği bir fiş olabilir. "Bilmiyorum" demek, yanlış bir kesinlik
        // yaymaktan iyidir — kasanın yanlış uyarısı tam olarak öyle bir kesinlikten doğdu.
        return new TicketQueryResult("UNKNOWN", hedef, null, null, null, null,
            denemeler, bekleyen.LastStep, bekleyen.LastCode, cihazMesgul,
            kagit?.GorulmeMs, kagit?.Adim);
    }

    /// <summary>Yanıt gövdesi — <c>SaleToPOIResponse</c> zarfı, veriler <c>Restomenum</c> altında.</summary>
    public static string Govde(TicketQueryRequest istek, TicketQueryResult sonuc, DateTimeOffset now)
    {
        var ek = new JsonObject
        {
            ["ticketState"] = sonuc.Durum,
            // W115: HER ZAMAN konur (ölçülmeyen değil, ölçülen bir olgu — kilit ya tutuluyordur
            // ya tutulmuyordur). Yeniden başlatmadan ÖNCE buna bakılmalı.
            ["deviceBusy"] = sonuc.DeviceBusy,
            // W120: HER ZAMAN konur — `false` "kâğıt var" demek ve bu da bir beyandır.
            // Alanı yalnız uyarı varken koymak, kasanın "temizlendi"yi görmesini engellerdi.
            ["paperWarning"] = sonuc.PaperWarningAt is not null,
        };
        if (sonuc.PaperWarningAt is long pw) ek["paperWarningAt"] = pw;
        if (sonuc.PaperWarningStep is string pws) ek["paperWarningStep"] = pws;
        // Ölçülmeyen alan KONMAZ. Alanın yokluğu "bilinmiyor" demektir; 0 ya da false koymak
        // kasaya olmayan bir kesinlik satmak olurdu.
        if (sonuc.TicketId is not null) ek["ticketId"] = sonuc.TicketId;
        if (sonuc.SaleSessionId is not null) ek["saleSessionId"] = sonuc.SaleSessionId;
        if (sonuc.TotalMinor is long tp) ek["deviceTicketTotalMinor"] = tp;
        if (sonuc.PaidMinor is long od) ek["devicePaidMinor"] = od;
        if (sonuc.LedgerWritten is bool d) ek["ledgerWritten"] = d;
        if (sonuc.PendingFinishAttempts is int n)
        {
            ek["pendingFinish"] = true;
            ek["pendingFinishAttempts"] = n;
            if (sonuc.PendingFinishStep is not null) ek["pendingFinishStep"] = sonuc.PendingFinishStep;
            if (sonuc.PendingFinishCode is not null) ek["pendingFinishCode"] = sonuc.PendingFinishCode;
        }

        return new JsonObject
        {
            ["SaleToPOIResponse"] = new JsonObject
            {
                ["MessageHeader"] = new JsonObject
                {
                    ["ProtocolVersion"] = "3.0",
                    ["MessageClass"] = "Service",
                    ["MessageCategory"] = TicketQueryParser.Category,
                    ["MessageType"] = "Response",
                    ["ServiceID"] = istek.ServiceId,
                    ["SaleID"] = istek.SaleId,
                    ["POIID"] = istek.PoiId,
                },
                // Sorgu hiçbir şey DENEMEZ; başarısı "cevap verebildim"dir. Cihaz durumu kötü
                // olsa bile bu çağrı başarılıdır — durum `ticketState`'te taşınır.
                ["Response"] = new JsonObject
                {
                    ["Result"] = "Success",
                    ["ErrorCondition"] = null,
                },
                ["Restomenum"] = ek,
            },
        }.ToJsonString();
    }
}
