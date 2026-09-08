namespace Restomenum.Agent.Core;

/// <summary>
/// Yoklamanın tek adımı — cihazdan okunan ham sayılar. <b>Burada yorum YOK:</b> hangi alanın ne
/// anlama geldiği zaten bilinseydi bu yoklamaya gerek olmazdı.
/// </summary>
public readonly record struct DiscountProbeStep(
    string Name,
    uint Rc,
    /// <summary><c>ST_TICKET.TotalReceiptAmount</c> — asıl soru bu: indirim düşülmüş mü?</summary>
    long TotalMinor,
    /// <summary><c>ST_TICKET.TotalReceiptDiscount</c> — cihaz indirimi ayrıca sayıyor mu?</summary>
    long DiscountMinor,
    /// <summary><c>ST_TICKET.TotalReceiptTax</c> — indirim MATRAHI düşürüyor mu?</summary>
    long TaxMinor,
    /// <summary><c>FP3_Dec</c>'in hesapladığı tutar; yalnız oran adımında dolu.</summary>
    long? ChangedMinor = null);

/// <summary>Yoklamanın tümü.</summary>
public readonly record struct DiscountProbeReport(
    ProbeStatus Status,
    IReadOnlyList<DiscountProbeStep> Steps,
    string? FailedStep,
    uint FailedRc,
    uint VoidAllRc,
    uint CloseRc,
    bool CleanedUp);

/// <summary>
/// <b>W60 — indirim yoklaması.</b> "Fişte indirim gözükmesi zorunlu" (kullanıcı kararı, P86-B) için
/// cihaz sözleşmesinin <b>belgelenmemiş</b> dört sorusunu ölçer:
///
/// <list type="number">
///   <item><b>Kalem indirimi çalışıyor mu:</b> <c>ST_ITEM.promotion</c> dolu gelen bir
///     <c>ItemSale</c> kabul ediliyor mu (rc), fişe nasıl yansıyor?</item>
///   <item><b>Fiş toplamı NET mi BRÜT mü:</b> <c>TotalReceiptAmount</c> indirimi düşülmüş mü
///     döner? <b>Bu sorunun cevabı satış yolunu doğrudan etkiliyor</b> — <c>DevamDogrula</c>
///     (<c>GmpTerminalTransport.Fis.cs:44</c>) cihazın toplamı ile platformun
///     <c>SaleTotalAmount</c>'ını <b>tam eşitlikle</b> karşılaştırıyor. Cihaz net, platform brüt
///     gönderirse indirimli her adisyonda ikinci kısmi ödeme <c>TICKET_SALE_MISMATCH</c> ile
///     reddedilir ve kasa manuel ödemeye düşer.</item>
///   <item><b>KDV matrahı düşüyor mu:</b> <c>TotalReceiptTax</c> indirimli tutar üzerinden mi
///     hesaplanıyor? (F70'te <b>P86-A yolunda</b> düştüğü ölçüldü: 891 → KDV 81 = 891×10/110.
///     <c>promotion</c> yolunda aynı mı, bilinmiyor — iki farklı mali kayıt.)</item>
///   <item><b><c>IndexOfItem = 0</c> "bütün fiş" mi:</b> <c>FP3_Minus</c>/<c>FP3_Dec</c> ara toplam
///     indirimi yapabiliyor mu? Belgede yok; varsayılamaz.</item>
///   <item><b>Tam ikram:</b> kalem tutarına EŞİT indirim (990 → 0) kabul ediliyor mu? Kabul
///     edilmiyorsa ikram, indirim yoluyla değil <c>PAYMENT_IKRAM</c> ödeme tipiyle yapılmalı —
///     ikisi mali olarak farklı (biri matrahı düşürür, öteki tahsilat satırı yazar).</item>
/// </list>
///
/// <para><b>⚠️ MALİ FİŞ AÇAR</b> — ama <b>ASLA YAZDIRMAZ</b>, <see cref="CapabilityProbe"/> ile
/// birebir aynı güvenlik kuralları geçerli. Fiş açıldığı için <b>bir mali fiş numarası tüketir</b>
/// (2026-09-09'da üç kez ölçüldü). Bu yüzden tek fiş, tek koşum, operatör onayı.</para>
/// </summary>
public static class DiscountProbe
{
    /// <summary>Yoklama kaleminin brüt tutarı (kuruş) ve üzerine denenecek indirim.</summary>
    public const long ItemAmountMinor = 990;
    public const long DiscountAmountMinor = 100;
    public const int DiscountRatePercent = 10;

    /// <summary>Departman 0: her cihazda var olan ilk bölüm. Fiş nasılsa iptal edilecek.</summary>
    private const int Dept = 0;

    public static DiscountProbeReport Run(IGmpWrapper gmp, Action<string, object?>? log = null)
    {
        var yaz = log ?? ((_, _) => { });
        var adimlar = new List<DiscountProbeStep>();

        // AÇIK FİŞ VARSA DOKUNMA: üzerinde tahsilat olabilir; onu iptal etmek bu aracın işi değil.
        var sr = gmp.Start(out var handle);
        if (sr.Code == GmpCodes.AlreadyDone)
        {
            yaz("[indirim] terminalde AÇIK fiş var — DOKUNULMADI", new { handle });
            return new(ProbeStatus.TicketAlreadyOpen, adimlar, "Start", sr.Code, 0, 0, false);
        }
        if (!sr.Ok)
            return new(ProbeStatus.SetupFailed, adimlar, "Start", sr.Code, 0, 0, false);

        var (durum, hataAdim, hataRc) = Olc(gmp, handle, adimlar, yaz);

        // TEMİZLİK HER HÂLÜKÂRDA — yarım açılmış fiş bırakılırsa bir sonraki satış onu SESSİZCE
        // iptal eder. Yazdırma YOK: fiş mali hafızaya girmez, kâğıda bir şey çıkmaz.
        var vr = gmp.VoidAll(handle, out _);
        var cr = gmp.Close(handle);
        yaz("[indirim] temizlik", new { voidAll = vr.Code, close = cr.Code, yazdirma = "YOK" });

        return new(durum, adimlar, hataAdim, hataRc, vr.Code, cr.Code, vr.Ok && cr.Ok);
    }

    private static (ProbeStatus, string?, uint) Olc(
        IGmpWrapper gmp, ulong handle, List<DiscountProbeStep> adimlar, Action<string, object?> yaz)
    {
        var r = gmp.TicketHeader(handle, GmpTicketTypes.Sale);
        if (!r.Ok) return (ProbeStatus.SetupFailed, "TicketHeader", r.Code);

        r = gmp.OptionFlags(handle, GmpEchoFlags.Reload);
        if (!r.Ok) return (ProbeStatus.SetupFailed, "OptionFlags", r.Code);

        // ── 1. Referans: indirimSİZ kalem ────────────────────────────────────────────
        // Önce indirimsiz bir kalem yazılır ki sonraki adımların farkı OKUNABİLİR olsun. Referans
        // olmadan "toplam net mi brüt mü" sorusu tek ölçümle cevaplanamaz.
        r = gmp.ItemSale(handle, new GmpItem("YOKLAMA-DUZ", ItemAmountMinor, 1, Dept), out var t1);
        adimlar.Add(Adim("ItemSale(indirimsiz 990)", r.Code, t1));
        yaz("[indirim] indirimsiz kalem", Ozet(r.Code, t1));
        if (!r.Ok) return (ProbeStatus.SetupFailed, "ItemSale", r.Code);

        // ── 2. Kalem indirimi (`ST_ITEM.promotion`) ──────────────────────────────────
        r = gmp.ItemSale(handle,
            new GmpItem("YOKLAMA-IND", ItemAmountMinor, 1, Dept, DiscountAmountMinor, "%10 İndirim"),
            out var t2);
        adimlar.Add(Adim($"ItemSale(990 + promotion {DiscountAmountMinor})", r.Code, t2));
        yaz("[indirim] promotion'lı kalem", Ozet(r.Code, t2));
        if (Kayip(r.Code)) return (ProbeStatus.HandleLost, "ItemSale(promotion)", r.Code);

        // ── 3. `FP3_Minus`, IndexOfItem = 0 (ara toplam mı?) ─────────────────────────
        var mr = gmp.Minus(handle, DiscountAmountMinor, "YOKLAMA-MINUS", 0, out var t3);
        adimlar.Add(Adim($"Minus({DiscountAmountMinor}, IndexOfItem=0)", mr.Code, t3));
        yaz("[indirim] Minus indeks 0", Ozet(mr.Code, t3));
        if (Kayip(mr.Code)) return (ProbeStatus.HandleLost, "Minus", mr.Code);

        // ── 4. `FP3_Dec`, oran — cihaz kuruşu KENDİ hesaplıyor ───────────────────────
        var dr = gmp.Dec(handle, DiscountRatePercent, "YOKLAMA-DEC", 0, out var degisen, out var t4);
        adimlar.Add(Adim($"Dec(%{DiscountRatePercent}, IndexOfItem=0)", dr.Code, t4) with { ChangedMinor = degisen });
        yaz("[indirim] Dec indeks 0", new { rc = dr.Code, degisen, t4.TotalAmountMinor, t4.TotalDiscountMinor, t4.TotalTaxMinor });
        if (Kayip(dr.Code)) return (ProbeStatus.HandleLost, "Dec", dr.Code);

        // ── 5. TAM İKRAM: indirim kalem tutarına EŞİT (990 → 0) ──────────────────────
        r = gmp.ItemSale(handle,
            new GmpItem("YOKLAMA-IKRAM", ItemAmountMinor, 1, Dept, ItemAmountMinor, "İKRAM"), out var t5);
        adimlar.Add(Adim("ItemSale(990 + promotion 990 = TAM İKRAM)", r.Code, t5));
        yaz("[indirim] tam ikram", Ozet(r.Code, t5));
        if (Kayip(r.Code)) return (ProbeStatus.HandleLost, "ItemSale(ikram)", r.Code);

        return (ProbeStatus.Completed, null, 0u);
    }

    private static DiscountProbeStep Adim(string ad, uint rc, GmpTicket t)
        => new(ad, rc, t.TotalAmountMinor, t.TotalDiscountMinor, t.TotalTaxMinor);

    private static object Ozet(uint rc, GmpTicket t)
        => new { rc, toplam = t.TotalAmountMinor, indirim = t.TotalDiscountMinor, kdv = t.TotalTaxMinor };

    /// <summary>Tanıtıcı geçersizleştiyse devam etmek ölçüm değil GÜRÜLTÜ üretir: kalan adımların
    /// "RET"i aslında "sorulamadı"dır ve karar ona dayanamaz.</summary>
    private static bool Kayip(uint rc) => rc is GmpCodes.InvalidHandle or GmpCodes.NoHandle;
}
