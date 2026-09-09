using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>W104 — yarım kalmış fişi tamamlama.</b>
///
/// Saha olayı (2026-09-09 23:07): <c>PrintTotalsAndPayments</c> rc 0 döndü, <c>PrintBeforeMF</c>
/// 10 113 ms'de zaman aşımına uğradı, <c>PrintMF</c>/<c>Close</c> HİÇ çağrılmadı. Fiş 990 kuruş
/// gerçek tahsilatla açık kaldı, masa kilitlendi.
///
/// <para>Bu dosyanın en önemli çivisi tek cümle: <b>hiçbir dalda <c>VoidAll</c>/<c>VoidPayment</c>
/// çağrılmaz.</b> Bu fişlerde tanım gereği para vardır; ters işlem müşterinin parasını geri
/// vermek olurdu ve geri alınamaz.</para>
/// </summary>
public class TicketFinishTests
{
    private static GmpTicket Fis(long toplam, long tahsil, uint bayrak, int odemeSayisi = 1) =>
        new(toplam, tahsil, odemeSayisi, GmpPaymentTypes.Cash, TransactionFlags: bayrak);

    // ── BAYRAK ÇÖZÜMÜ ────────────────────────────────────────────────────────────

    [Fact]
    public void Bitler_interop_degerlerine_KILITLI()
    {
        // ← ÇİVİ: bu sabitler `GmpInterop.cs`'teki `ETransactionFlags`'ten KOPYA (Core interop'a
        // referans veremez). Kopya sessizce kayarsa kurtarma yanlış adımı seçer — mesela
        // `MF_PRINTED`i `BEFORE_MF` sanıp zaten basılmış mali dibi ikinci kez basmaya çalışır.
        Assert.Equal(131072u, TicketFlags.HeaderPrinted);             // 1 << 17
        Assert.Equal(262144u, TicketFlags.TotalsAndPaymentsPrinted);  // 1 << 18
        Assert.Equal(524288u, TicketFlags.BeforeMfPrinted);           // 1 << 19
        Assert.Equal(1048576u, TicketFlags.MfPrinted);                // 1 << 20
    }

    [Fact]
    public void Yazdirilabilir_HAM_degeri_de_tanimadigi_biti_de_gosterir()
    {
        // Sahada ölçülen değer: 131074 = GMP3 | TICKET_HEADER_PRINTED.
        var m = TicketFlags.Yazdirilabilir(131074u);
        Assert.Contains("131074", m);
        Assert.Contains("0x00020002", m);
        Assert.Contains("GMP3", m);
        Assert.Contains("TICKET_HEADER_PRINTED", m);

        // ← ÇİVİ: TANIMADIĞIMIZ bir bit yok sayılmaz. Ham sayı başta durduğu için görünür;
        // çözülmüş listeyle kırpsaydık cihazın söylediğini kendi sözlüğümüzle susturmuş olurduk.
        var bilinmeyen = TicketFlags.Yazdirilabilir(1u << 30);
        Assert.Contains("1073741824", bilinmeyen);
        Assert.Contains("(set bit tanınmadı)", bilinmeyen);
    }

    // ── PLAN: ÜRETİCİNİN 9 ADIMLI AKIŞI ──────────────────────────────────────────

    [Fact]
    public void MF_basilmissa_YALNIZ_kapat()   // üretici §2 adım 3
    {
        var p = TicketFinish.Planla(Fis(990, 990, TicketFlags.MfPrinted | TicketFlags.BeforeMfPrinted));
        Assert.Equal(FisBitirmeAdimi.YalnizKapat, p.Adim);
        Assert.Equal(3, p.UreticiAdimi);
    }

    [Fact]
    public void BeforeMF_basilmissa_PrintMF_ve_kapat()   // üretici §2 adım 4
    {
        var p = TicketFinish.Planla(Fis(990, 990,
            TicketFlags.TotalsAndPaymentsPrinted | TicketFlags.BeforeMfPrinted));
        Assert.Equal(FisBitirmeAdimi.MfVeKapat, p.Adim);
        Assert.Equal(4, p.UreticiAdimi);
    }

    [Fact]
    public void SAHA_OLAYI_toplamlar_basildi_BeforeMF_patladi_PrintMF_ve_kapat()   // üretici §2 adım 5
    {
        // ← ÇİVİ: 23:07'nin tam hâli. `PrintBeforeMF` TEKRAR DENENMEZ — üretici adım 5 doğrudan
        // `PrintMF`+`Close` diyor. Belgeden sapmak, sahada hiç ölçülmemiş bir sırayı gerçek
        // parayla denemek olurdu.
        var p = TicketFinish.Planla(Fis(990, 990,
            (1u << 1) | TicketFlags.HeaderPrinted | TicketFlags.TotalsAndPaymentsPrinted));
        Assert.Equal(FisBitirmeAdimi.MfVeKapat, p.Adim);
        Assert.Equal(5, p.UreticiAdimi);
        Assert.Equal(TicketFinish.Tamamlanabilir, p.Neden);
    }

    [Fact]
    public void Hicbir_baski_bayragi_yoksa_TAM_zincir()   // üretici §2 adım 6 (bilinçli sapma)
    {
        var p = TicketFinish.Planla(Fis(990, 990, (1u << 1) | TicketFlags.HeaderPrinted));
        Assert.Equal(FisBitirmeAdimi.TumBaskiVeKapat, p.Adim);
        Assert.Equal(6, p.UreticiAdimi);
    }

    // ── SERT TABANLAR: DOKUNMA ───────────────────────────────────────────────────

    [Fact]
    public void Odeme_TAMAMLANMAMISSA_bayraklar_ne_olursa_olsun_DOKUNULMAZ()   // üretici §2 adım 7
    {
        // ← ÇİVİ: bütün baskı bayrakları set olsa BİLE kapatılmaz. Kalanı almadan basıp kapatmak,
        // ödenmemiş bir tutarı ödenmiş gibi MALİ HAFIZAYA yazmak olurdu — geri alınamaz.
        var hepsi = TicketFlags.HeaderPrinted | TicketFlags.TotalsAndPaymentsPrinted
                  | TicketFlags.BeforeMfPrinted | TicketFlags.MfPrinted;
        var p = TicketFinish.Planla(Fis(2380, 990, hepsi));
        Assert.Equal(FisBitirmeAdimi.Dokunma, p.Adim);
        Assert.Equal(TicketFinish.OdemeTamamlanmamis, p.Neden);
        Assert.Equal(7, p.UreticiAdimi);
    }

    [Fact]
    public void Tahsilat_YOKSA_bu_yolun_isi_degil()
    {
        // Ödemesiz açık fiş `--void`/`BayatFisiTemizle` işidir (orada `VoidAll` MEŞRU: para yok).
        // Buradan basıp kapatmak, hiç para alınmamış bir fişi mali hafızaya yazardı.
        var p = TicketFinish.Planla(Fis(990, 0, TicketFlags.HeaderPrinted, odemeSayisi: 0));
        Assert.Equal(FisBitirmeAdimi.Dokunma, p.Adim);
        Assert.Equal(TicketFinish.OdemeYok, p.Neden);
    }

    [Fact]
    public void Toplam_OKUNAMAMISSA_kapatilmaz()
    {
        // ← ÇİVİ: "kalan yok" ile "kalanı bilmiyorum" AYNI ŞEY DEĞİL. Toplam 0 iken tahsilat
        // varsa okuma güvenilmezdir; orada kapatmak kör bir karardır.
        var p = TicketFinish.Planla(Fis(0, 990, TicketFlags.TotalsAndPaymentsPrinted));
        Assert.Equal(FisBitirmeAdimi.Dokunma, p.Adim);
        Assert.Equal(TicketFinish.OdemeTamamlanmamis, p.Neden);
    }

    [Fact]
    public void BOZUK_okumaya_dokunulmaz()
    {
        // `PaymentCount < 0` = sarmalayıcının "bu okuma bozuk" beyanı.
        var p = TicketFinish.Planla(new GmpTicket(990, 990, -1, GmpPaymentTypes.Cash,
            TransactionFlags: TicketFlags.TotalsAndPaymentsPrinted));
        Assert.Equal(FisBitirmeAdimi.Dokunma, p.Adim);
        Assert.Equal("TICKET_READ_BROKEN", p.Neden);
    }

    // ── UYGULAMA: CİHAZDA HANGİ ÇAĞRILAR YAPILIR ────────────────────────────────

    private static GmpTerminalTransportTests.FakeGmp Cihaz(GmpTicket fis)
        => new() { Ticket = fis };

    [Fact]
    public void SAHA_OLAYI_uygulanir_YALNIZ_PrintMF_ve_Close()
    {
        var g = Cihaz(Fis(990, 990, TicketFlags.TotalsAndPaymentsPrinted));
        var s = TicketFinishRunner.Calistir(g, 42, uygula: true);

        Assert.True(s.Kapandi);
        Assert.Equal(new[] { "OptionFlags", "GetTicket", "PrintMF", "Close" }, g.Calls);
        // ← ÇİVİ: `PrintBeforeMF` TEKRAR ÇAĞRILMAZ (üretici adım 5) ve `PrintTotalsAndPayments`
        // ikinci kez basılmaz — zaten basılmış bir bölümü tekrar basmak çift fiş üretirdi.
        Assert.DoesNotContain("PrintBeforeMF", g.Calls);
        Assert.DoesNotContain("PrintTotalsAndPayments", g.Calls);
    }

    [Fact]
    public void HICBIR_bayrak_kombinasyonunda_VoidAll_ya_da_VoidPayment_CAGRILMAZ()
    {
        // ← EN ÖNEMLİ ÇİVİ. Kullanıcının açık talimatı: "VoidAll KULLANMA — para geri alınmış olur."
        // Tek bir örnek değil, dört baskı bitinin BÜTÜN 16 kombinasyonu × (tam/kısmi ödeme)
        // × (kuru prova/uygula) taranıyor. Yeni bir dal eklenirse ve içine ters işlem sızarsa
        // burada yakalanır.
        uint[] bitler = { TicketFlags.HeaderPrinted, TicketFlags.TotalsAndPaymentsPrinted,
                          TicketFlags.BeforeMfPrinted, TicketFlags.MfPrinted };
        for (var maske = 0; maske < 16; maske++)
        {
            uint b = 1u << 1;
            for (var i = 0; i < 4; i++) if ((maske & (1 << i)) != 0) b |= bitler[i];
            foreach (var tahsil in new long[] { 0, 500, 990 })
            foreach (var uygula in new[] { false, true })
            {
                var g = Cihaz(Fis(990, tahsil, b, odemeSayisi: tahsil > 0 ? 1 : 0));
                TicketFinishRunner.Calistir(g, 42, uygula);
                Assert.DoesNotContain("VoidAll", g.Calls);
                Assert.DoesNotContain("VoidPayment", g.Calls);
            }
        }
    }

    [Fact]
    public void KURU_PROVA_okur_ama_CIHAZI_DEGISTIRMEZ()
    {
        var g = Cihaz(Fis(990, 990, TicketFlags.TotalsAndPaymentsPrinted));
        var s = TicketFinishRunner.Calistir(g, 42, uygula: false);

        Assert.False(s.Kapandi);
        Assert.Equal(FisBitirmeAdimi.MfVeKapat, s.Plan.Adim);
        // ← ÇİVİ: ölçmek için değiştirmek zorunda kalmamak. Para taşıyan bir cihazda "ne yapacağını
        // görmek" tek başına bir yetenektir (aynı gerekçe `--ticket`i doğurmuştu).
        Assert.Equal(new[] { "OptionFlags", "GetTicket" }, g.Calls);
        Assert.Equal(TicketFlags.TotalsAndPaymentsPrinted, s.Bayraklar);
    }

    [Fact]
    public void Baski_patlarsa_CLOSE_DENENMEZ_fis_acik_birakilir()
    {
        var g = Cihaz(Fis(990, 990, TicketFlags.TotalsAndPaymentsPrinted));
        g.PrintMfFailures = 1;
        var s = TicketFinishRunner.Calistir(g, 42, uygula: true);

        Assert.False(s.Kapandi);
        Assert.Equal("PrintMF", s.BasarisizAdim);
        // ← ÇİVİ: basılamamış bir fişi kapatmak, EKSİK bir mali belgeyi kesinleştirmek olurdu.
        // Açık kalırsa aynı komut tekrar denenebilir; kapanırsa geri dönüş yok.
        Assert.DoesNotContain("Close", g.Calls);
    }

    [Fact]
    public void Fis_OKUNAMAZSA_hicbir_seye_dokunulmaz()
    {
        var g = Cihaz(Fis(990, 990, TicketFlags.TotalsAndPaymentsPrinted));
        g.Codes["GetTicket"] = GmpCodes.Timeout;
        var s = TicketFinishRunner.Calistir(g, 42, uygula: true);

        Assert.False(s.Kapandi);
        Assert.Equal(FisBitirmeAdimi.Dokunma, s.Plan.Adim);
        Assert.Equal("TICKET_READ_FAILED", s.Plan.Neden);
        Assert.Equal(new[] { "OptionFlags", "GetTicket" }, g.Calls);
    }

    [Fact]
    public void Odemesi_eksik_fise_uygula_dense_bile_dokunulmaz()
    {
        var g = Cihaz(Fis(2380, 990, TicketFlags.TotalsAndPaymentsPrinted));
        var s = TicketFinishRunner.Calistir(g, 42, uygula: true);

        Assert.False(s.Kapandi);
        Assert.Equal(TicketFinish.OdemeTamamlanmamis, s.Plan.Neden);
        // ← ÇİVİ: `uygula: true` bu korumayı GEVŞETMEZ. Onay bayrağı "riski üstlendim" demektir,
        // "ödenmemiş fişi mali hafızaya yaz" değil.
        Assert.Equal(new[] { "OptionFlags", "GetTicket" }, g.Calls);
    }

    [Fact]
    public void Bayraksiz_tam_odenmis_fis_TAM_zinciri_kosar()
    {
        var g = Cihaz(Fis(990, 990, (1u << 1) | TicketFlags.HeaderPrinted));
        var s = TicketFinishRunner.Calistir(g, 42, uygula: true);

        Assert.True(s.Kapandi);
        Assert.Equal(new[] { "OptionFlags", "GetTicket", "PrintTotalsAndPayments",
            "PrintBeforeMF", "PrintMF", "Close" }, g.Calls);
    }

    [Fact]
    public void MF_basilmissa_YALNIZ_Close_cagrilir()
    {
        var g = Cihaz(Fis(990, 990, TicketFlags.MfPrinted));
        var s = TicketFinishRunner.Calistir(g, 42, uygula: true);

        Assert.True(s.Kapandi);
        Assert.Equal(new[] { "OptionFlags", "GetTicket", "Close" }, g.Calls);
        // ← ÇİVİ: zaten basılmış mali dip İKİNCİ KEZ basılmaz.
        Assert.DoesNotContain("PrintMF", g.Calls);
    }
}
