namespace Restomenum.Agent.Core;

/// <summary>Bir aday ödeme tipinin ölçüm sonucu. <see cref="Rc"/> cihazın döndürdüğü kodun ta kendisi —
/// burada yorumlanmaz.</summary>
public readonly record struct CapabilityProbeResult(int PaymentType, string Op, string Name, uint Rc, bool Accepted);

/// <summary>
/// Bir cihaz yoklamasının nerede durduğu. <b>Yoklamaların ORTAK kümesi</b> (<c>--capability-probe</c>,
/// <c>--discount-probe</c>): her yoklamaya ayrı bir enum yazmak, aynı durumların iki yerde
/// tanımlanıp zamanla ıraksaması demek olurdu.
/// </summary>
public enum ProbeStatus
{
    /// <summary>Fiş açıldı, adaylar ölçüldü, fiş temizlendi.</summary>
    Completed,
    /// <summary>Cihazda BAŞKA bir açık fiş var — dokunulmadı.</summary>
    TicketAlreadyOpen,
    /// <summary>Fiş açılamadı (<c>Start</c>/<c>TicketHeader</c>/<c>OptionFlags</c>/<c>ItemSale</c>).</summary>
    SetupFailed,
    /// <summary>Ölçüm sırasında tanıtıcı geçersizleşti; kalan tipler ölçülmedi.</summary>
    HandleLost,
}

/// <summary>Yoklamanın tümü: durum + ölçülenler + temizliğin sonucu.</summary>
public readonly record struct CapabilityProbeReport(
    ProbeStatus Status,
    IReadOnlyList<CapabilityProbeResult> Results,
    string? FailedStep,
    uint FailedRc,
    uint VoidAllRc,
    uint CloseRc,
    bool CleanedUp);

/// <summary>
/// <b>W57 — yetenek yoklaması.</b> Cihazın hangi ödeme tiplerini KABUL ettiğini ÖLÇER; beyan
/// (<see cref="AgentCapabilities"/>) tahmine değil buna dayansın diye.
///
/// <para><b>Neden gerekli:</b> <c>--payment-apps</c> çıktısı <b>banka kartı uygulamalarının</b>
/// listesidir, "desteklenen ödeme tipleri" listesi değil. DLL'in kendi tablosunda
/// (<c>EPaymentTypes</c>) <c>YEMEKCEKI</c> ve <c>MOBILE</c>'ın yanında "(Uygulama varsa)" yazıyor;
/// <c>HEDIYE_CEKI</c>/<c>PUAN</c>/<c>BANKA_TRANSFERI</c>/<c>CEK</c>'in yanında böyle bir koşul YOK —
/// bunlar fişe yazılan muhasebe ödeme tipleri, banka bacağı istemiyorlar. Uygulama listesiyle
/// kesişim almak tam da bu dördünü yanlışlıkla elerdi.</para>
///
/// <para><b>⚠️ MALİ FİŞ AÇAR</b> — ama <b>ASLA YAZDIRMAZ</b>. Fiş açılır, tek kalem yazılır, her
/// aday için kısmi ödeme denenir, sonra <c>VoidAll</c>+<c>Close</c>. Yazdırma çağrısı
/// (<c>PrintTotalsAndPayments</c>/<c>PrintBeforeMF</c>/<c>PrintMF</c>/<c>PrintUserMessage</c>)
/// <b>hiç yapılmaz</b>; bu, bugün her <c>ItemSale</c> hatasında zaten koşan temizlik yoludur.
/// Yine de fiş AÇILDIĞI için bir mali fiş numarası tüketebilir — FR07'de ölçüldü: iptal edilen boş
/// fişe <c>FNo 2</c> verildi.</para>
///
/// <para><b>Yorum yok, ölçüm var:</b> "şu rc şu demektir" denmez; dönen kod ne ise o taşınır.</para>
/// </summary>
public static class CapabilityProbe
{
    /// <summary>
    /// Aday tipler ve platform op adları — <b>yalnız uygulama koşulu OLMAYAN dördü</b>.
    ///
    /// <para><c>YEMEKCEKI</c> (8) ve <c>MOBILE</c> (16) burada YOK: bu terminalde kurulu yemek çeki /
    /// mobil uygulaması olmadığı ölçüldü (<c>--payment-apps</c>: 4 kayıt, dördü de banka kartı), yani
    /// yoklamaları "cihaz desteklemiyor"u değil "uygulama yok"u ölçerdi. <c>CASH_CURRENCY</c> (2) de
    /// yok: ajan her tutarı TL gönderiyor (<c>CurrencyTl = 949</c> sabit), döviz kabul etmek dövizli
    /// bir ödemeyi TL tutarıyla deftere yazmak olurdu.</para>
    /// </summary>
    public static readonly IReadOnlyList<(int Type, string Op, string Name)> Candidates = new[]
    {
        (GmpPaymentTypes.GiftVoucher, "sale.giftVoucher", "HEDIYE_CEKI"),
        (GmpPaymentTypes.Points, "sale.points", "PUAN"),
        (GmpPaymentTypes.BankTransfer, "sale.bankTransfer", "BANKA_TRANSFERI"),
        (GmpPaymentTypes.Cheque, "sale.cheque", "CEK"),
    };

    /// <summary>Her aday 1 kuruş ödeyeceği için kalem tutarı aday sayısından BÜYÜK: fiş hiçbir zaman
    /// tamamlanmasın. Tam ödenmiş fiş kapanmaya hazır hâle gelirdi; kısmi ödeme onu açık ve iptal
    /// edilebilir bırakır.</summary>
    public static long ItemAmountMinor => Candidates.Count + 1;

    public static CapabilityProbeReport Run(IGmpWrapper gmp, Action<string, object?>? log = null)
    {
        var yaz = log ?? ((_, _) => { });
        var sonuclar = new List<CapabilityProbeResult>();

        // AÇIK FİŞ VARSA DOKUNMA. 2080 = cihazda başka bir fiş var; onu temizlemek bu aracın işi
        // DEĞİL (üzerinde tahsilat olabilir). Yüksek sesle dur.
        var sr = gmp.Start(out var handle);
        if (sr.Code == GmpCodes.AlreadyDone)
        {
            yaz("[yoklama] terminalde AÇIK fiş var — DOKUNULMADI", new { handle });
            return new(ProbeStatus.TicketAlreadyOpen, sonuclar, "Start", sr.Code, 0, 0, false);
        }
        if (!sr.Ok)
            return new(ProbeStatus.SetupFailed, sonuclar, "Start", sr.Code, 0, 0, false);

        // Fiş buradan itibaren AÇIK: ölçüm nerede biterse bitsin temizlik AYNI yerden geçer.
        var (durum, hataAdim, hataRc) = Olc(gmp, handle, sonuclar, yaz);

        // TEMİZLİK HER HÂLÜKÂRDA. Yarım açılmış fiş bırakılırsa bir sonraki satış onu SESSİZCE
        // iptal eder ve üzerindeki (yoklamanın koyduğu) ödeme kaybolur. Burada yazdırma YOK: fiş
        // mali hafızaya girmez, kâğıda bir şey çıkmaz.
        var vr = gmp.VoidAll(handle, out _);
        var cr = gmp.Close(handle);
        yaz("[yoklama] temizlik", new { voidAll = vr.Code, close = cr.Code, yazdirma = "YOK" });

        return new(durum, sonuclar, hataAdim, hataRc, vr.Code, cr.Code, vr.Ok && cr.Ok);
    }

    /// <summary>Fiş açıkken yapılan ölçüm. Ayrı metot: temizlik çağrısı TEK yerde kalsın, erken
    /// dönüşlerden biri onu atlayamasın.</summary>
    private static (ProbeStatus Durum, string? Adim, uint Rc) Olc(
        IGmpWrapper gmp, ulong handle, List<CapabilityProbeResult> sonuclar, Action<string, object?> yaz)
    {
        var r = gmp.TicketHeader(handle, GmpTicketTypes.Sale);
        if (!r.Ok) return (ProbeStatus.SetupFailed, "TicketHeader", r.Code);

        // `GetTicket`in ödeme detayını eksik döndürmemesi için — satış yolundaki ile aynı sözleşme.
        r = gmp.OptionFlags(handle, GmpEchoFlags.Reload);
        if (!r.Ok) return (ProbeStatus.SetupFailed, "OptionFlags", r.Code);

        // Departman 0: her cihazda var olan ilk bölüm. Fiş nasılsa iptal edilecek.
        r = gmp.ItemSale(handle, new GmpItem("YOKLAMA", ItemAmountMinor, 1, 0), out _);
        if (!r.Ok) return (ProbeStatus.SetupFailed, "ItemSale", r.Code);

        foreach (var (tip, op, ad) in Candidates)
        {
            var pr = gmp.Payment(handle, new GmpPaymentRequest(1, tip), out var sonra);
            sonuclar.Add(new CapabilityProbeResult(tip, op, ad, pr.Code, pr.Ok));
            yaz("[yoklama] ödeme tipi denendi", new
            {
                ad, tip, rc = pr.Code, sonuc = pr.Ok ? "KABUL" : "RET",
                tahsil = sonra.PaidAmountMinor, adet = sonra.PaymentCount,
            });

            // Tanıtıcı geçersizleştiyse devam etmek ölçüm değil gürültü üretir.
            if (pr.Code is GmpCodes.InvalidHandle or GmpCodes.NoHandle)
                return (ProbeStatus.HandleLost, "Payment", pr.Code);
        }

        return (ProbeStatus.Completed, null, 0u);
    }
}
