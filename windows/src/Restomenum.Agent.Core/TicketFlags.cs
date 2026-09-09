namespace Restomenum.Agent.Core;

/// <summary>
/// <b><c>ST_TICKET.TransactionFlags</c>'in ÇÖZÜMÜ</b> — cihazın fişi hangi baskı aşamasına kadar
/// götürdüğünü söyleyen tek kanal.
///
/// <para><b>Neden var (W104):</b> 2026-09-09 23:07'de <c>PrintBeforeMF</c> zaman aşımına uğradı ve
/// fiş 990 kuruş GERÇEK tahsilatla açık kaldı. Üreticinin kurtarma akışı
/// (<c>GMP3OlumsuzDurumYönetimi_v1.2</c> §2) "hangi adımdan devam edilecek" sorusunu tam bu
/// bayraklardan okutuyor. Bayrak olmadan o soruya ancak ÇAĞRI SIRASINDAN çıkarımla cevap
/// verilebilirdi — doğru çıkarımdı ama çıkarımdı; cihazın kendi beyanı değil.</para>
///
/// <para><b>Bit değerleri <c>GmpInterop.cs:811-814</c>'ten (<c>ETransactionFlags</c>) BİREBİR
/// kopyalandı.</b> Neden kopya: <c>Restomenum.Agent.Core</c> interop katmanına (Windows'a bağlı
/// <c>Restomenum.Agent.Gmp</c>) referans VERMEZ — bağımlılık yönü tersine dönerdi ve Core'un
/// testleri Windows'a bağlanırdı. Kopya olduğu için <see cref="Bitler"/> testinde sabit
/// değerlerle KİLİTLENDİ: interop'ta bir bit kayarsa test kırmızıya döner.</para>
///
/// <para>⚠️ <b>Bayrak YOKLUĞU kanıt DEĞİLDİR.</b> <c>FP3_VoidPayment</c> hata dönerken tüm
/// <c>ST_TICKET</c>'ı sıfırlarla döndürdü (W102) — orada <c>TransactionFlags == 0</c> "hiçbir şey
/// basılmadı" demiyordu, "bu yanıtta alan dolmadı" diyordu. Bu yüzden karar YALNIZ <b>SET olan</b>
/// bitlere dayanır; hiçbir dal "şu bit yok, öyleyse şunu yap" demez.</para>
/// </summary>
public static class TicketFlags
{
    /// <summary>Fiş başlığı basıldı (<c>FLG_XTRANS_TICKET_HEADER_PRINTED</c>, <c>1 &lt;&lt; 17</c>).</summary>
    public const uint HeaderPrinted = 1u << 17;

    /// <summary>Toplamlar + ödemeler basıldı (<c>..._TOTALS_AND_PAYMENTS_PRINTED</c>, <c>1 &lt;&lt; 18</c>).</summary>
    public const uint TotalsAndPaymentsPrinted = 1u << 18;

    /// <summary>Mali dip öncesi bölüm basıldı (<c>..._FOOTER_BEFORE_MF_PRINTED</c>, <c>1 &lt;&lt; 19</c>).</summary>
    public const uint BeforeMfPrinted = 1u << 19;

    /// <summary>Mali dip (MF) basıldı (<c>..._FOOTER_MF_PRINTED</c>, <c>1 &lt;&lt; 20</c>).</summary>
    public const uint MfPrinted = 1u << 20;

    public static bool Var(uint bayraklar, uint bit) => (bayraklar & bit) != 0;

    /// <summary>
    /// Günlüğe/rapora yazılacak HAM + çözülmüş gösterim, ör.
    /// <c>"262146 (0x00040002) [GMP3|TOTALS_AND_PAYMENTS_PRINTED]"</c>.
    ///
    /// <para>Ham değer BAŞTA duruyor bilerek: tanımadığımız bir bit çıkarsa çözülmüş liste onu
    /// göstermez ama ham sayı gösterir. "Çözemediğimiz bit yok sayılsın" demek, ölçümü kendi
    /// sözlüğümüzle kırpmak olurdu.</para>
    /// </summary>
    public static string Yazdirilabilir(uint bayraklar)
    {
        var adlar = new List<string>();
        if (Var(bayraklar, 1u << 1)) adlar.Add("GMP3");
        if (Var(bayraklar, HeaderPrinted)) adlar.Add("TICKET_HEADER_PRINTED");
        if (Var(bayraklar, TotalsAndPaymentsPrinted)) adlar.Add("TOTALS_AND_PAYMENTS_PRINTED");
        if (Var(bayraklar, BeforeMfPrinted)) adlar.Add("FOOTER_BEFORE_MF_PRINTED");
        if (Var(bayraklar, MfPrinted)) adlar.Add("FOOTER_MF_PRINTED");
        var liste = adlar.Count == 0 ? "(set bit tanınmadı)" : string.Join("|", adlar);
        return $"{bayraklar} (0x{bayraklar:X8}) [{liste}]";
    }
}

/// <summary>Kurtarmada cihazda YAPILACAK adımlar. <b><c>VoidAll</c> hiçbir değerde YOK.</b></summary>
public enum FisBitirmeAdimi
{
    /// <summary>Cihaza DOKUNULMAZ. Tek güvenli varsayılan; sebebi <c>Neden</c> taşır.</summary>
    Dokunma = 0,

    /// <summary>Üretici §2 adım 3 — <c>MF_PRINTED</c> set: yalnız <c>Close</c>.</summary>
    YalnizKapat,

    /// <summary>Üretici §2 adım 4/5 — <c>BEFORE_MF</c> ya da <c>TOTALS</c> set: <c>PrintMF</c> + <c>Close</c>.</summary>
    MfVeKapat,

    /// <summary>Hiçbir baskı bayrağı SET değil: olağan kapanış zinciri (üç baskı + <c>Close</c>).</summary>
    TumBaskiVeKapat,
}

/// <summary>Kurtarma kararı — <b>saf</b>, cihaza dokunmaz, testlenebilir.</summary>
/// <param name="Adim">Yapılacak iş.</param>
/// <param name="Neden">Makine-okunur gerekçe; <see cref="FisBitirmeAdimi.Dokunma"/>'da RET sebebidir.</param>
/// <param name="UreticiAdimi">Üretici belgesindeki adım numarası (izlenebilirlik); bilinmiyorsa 0.</param>
public readonly record struct FisBitirmePlani(FisBitirmeAdimi Adim, string Neden, int UreticiAdimi);

/// <summary>
/// <b>YARIM KALMIŞ FİŞİ TAMAMLAMA KARARI</b> — üreticinin kendi kurtarma akışı
/// (<c>GMP3OlumsuzDurumYönetimi_v1.2</c> §2, 9 adım), kod hâlinde.
///
/// <para><b><c>VoidAll</c> HİÇBİR DALDA YOK — ve bu bir tercih değil, şart.</b> Üretici <c>VoidAll</c>'ı
/// yalnız adım 9'da, yani <b>ÖDEME YOKKEN</b> öneriyor. Bu sınıfın işlediği fişlerde ise tanım gereği
/// TAHSİLAT VARDIR (yoksa hiç buraya gelinmez). Böyle bir fişte <c>VoidAll</c> çağırmak müşteriden
/// alınmış parayı geri vermek olurdu — geri alınamaz ve sessiz bir kayıp. Cihaz zaten <c>2069</c> ile
/// reddederdi, ama korumayı cihazın hata koduna bırakmak "yanlış şeyi istemek ama tutulmak"tır.</para>
///
/// <para><b>Ödemesi TAMAMLANMAMIŞ fişe de dokunulmaz.</b> Üretici adım 7 böyle bir fiş için
/// "kalan ödemeler TAMAMLANIR, sonra <c>PrintMF</c>+<c>Close</c>" diyor — yani kapatmadan önce
/// PARA alınmalı. Kalanı almadan basıp kapatmak, ödenmemiş bir tutarı ödenmiş gibi mali hafızaya
/// yazmak olurdu. Kalanı bu sınıf ALAMAZ (kart/nakit kararı kasanındır), o yüzden
/// <c>TICKET_NOT_COMPLETE</c> ile durur ve kararı insana/kasaya bırakır.</para>
/// </summary>
public static class TicketFinish
{
    public const string OdemeTamamlanmamis = "TICKET_NOT_COMPLETE";
    public const string OdemeYok           = "TICKET_HAS_NO_PAYMENT";
    public const string Tamamlanabilir     = "TICKET_COMPLETE";

    public static FisBitirmePlani Planla(GmpTicket fis)
    {
        // ── SERT TABAN 1: OKUNAMAYAN FİŞE DOKUNULMAZ ──────────────────────────────
        // `PaymentCount < 0` sarmalayıcının "bu okuma bozuk" beyanı (sayaç struct kapasitesini
        // aştı). Bozuk bir okumanın üstüne baskı/kapatma yapmak, ne olduğunu bilmediğimiz bir
        // fişi mali hafızaya yazmaktır.
        if (fis.PaymentCount < 0)
            return new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "TICKET_READ_BROKEN", 0);

        // ── SERT TABAN 2: TAHSİLAT YOKSA BU SINIFIN İŞİ DEĞİL ─────────────────────
        // Ödemesiz açık fiş `BayatFisiTemizle`/`--void` işidir (orada `VoidAll` MEŞRU çünkü para
        // yok). Buradan basıp kapatmak, hiç para alınmamış bir fişi mali hafızaya yazardı.
        if (fis.PaidAmountMinor <= 0)
            return new FisBitirmePlani(FisBitirmeAdimi.Dokunma, OdemeYok, 9);

        // ── SERT TABAN 3: EKSİK ÖDEME → ÜRETİCİ ADIM 7, KALANI ÖNCE AL ────────────
        // Toplam okunamadıysa (0) da buraya düşülür: "kalan yok" ile "kalanı bilmiyorum" aynı
        // şey değildir ve ikincisinde kapatmak kör bir karardır.
        if (fis.TotalAmountMinor <= 0 || fis.PaidAmountMinor < fis.TotalAmountMinor)
            return new FisBitirmePlani(FisBitirmeAdimi.Dokunma, OdemeTamamlanmamis, 7);

        var b = fis.TransactionFlags;

        // Üretici §2, adım 3 → 4 → 5. SIRA ÖNEMLİ: en ileri aşama önce sorulur, yoksa zaten
        // basılmış bir bölüm ikinci kez basılırdı.
        if (TicketFlags.Var(b, TicketFlags.MfPrinted))
            return new FisBitirmePlani(FisBitirmeAdimi.YalnizKapat, Tamamlanabilir, 3);

        if (TicketFlags.Var(b, TicketFlags.BeforeMfPrinted))
            return new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, Tamamlanabilir, 4);

        // Adım 5 — 23:07 olayının tam yeri: `PrintTotalsAndPayments` rc 0 döndü, `PrintBeforeMF`
        // zaman aşımına uğradı. Üretici burada `PrintBeforeMF`'i TEKRAR DENEMİYOR, doğrudan
        // `PrintMF`+`Close` diyor. Biz de öyle yapıyoruz — belgeden sapmak, sahada hiç ölçülmemiş
        // bir sırayı gerçek parayla denemek olurdu.
        if (TicketFlags.Var(b, TicketFlags.TotalsAndPaymentsPrinted))
            return new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, Tamamlanabilir, 5);

        // Hiçbir baskı bayrağı SET değil ama fiş tam ödenmiş — üretici adım 6.
        //
        // ⚠️ BELGEDEN BİLEREK SAPILAN TEK NOKTA. Adım 6 harfiyen "`PrintMF` + `Close`" diyor.
        // Ama buraya YALNIZ hiçbir baskı yapılmamışken gelinir; `PrintMF`'i tek başına çağırmak
        // müşteriye TOPLAMLARI VE ÖDEMELERİ HİÇ BASILMAMIŞ bir fiş vermek olurdu — eksik bir mali
        // belge. Bunun yerine her başarılı satışta çalışan olağan zincir kullanılıyor
        // (`PrintTotalsAndPayments` → `PrintBeforeMF` → `PrintMF` → `Close`): sahada binlerce kez
        // ölçüldü ve fişin tamamını basar. Sapma DAR: yalnız bu dal, yalnız bu gerekçe.
        return new FisBitirmePlani(FisBitirmeAdimi.TumBaskiVeKapat, Tamamlanabilir, 6);
    }
}
