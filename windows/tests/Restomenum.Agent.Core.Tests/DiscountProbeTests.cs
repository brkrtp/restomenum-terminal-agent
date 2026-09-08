using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W60 — indirim yoklaması. "Fişte indirim gözükmesi zorunlu" kararının (P86-B) cihaz sözleşmesini
/// ölçer.
///
/// <para><b>Bu testlerin asıl işi güvenlik:</b> araç mali fiş AÇIYOR ve bir fiş numarası TÜKETİYOR.
/// Açtığı fişi yazdırmadan iptal etmesi çivilenmiş bir kural olmalı — bir <c>PrintBeforeMF</c>
/// çağrısı fişi mali hafızaya sokar, kâğıda basar ve bir daha iptal edilemez (2357).</para>
/// </summary>
public class DiscountProbeTests
{
    private static readonly string[] YazdirmaCagrilari =
        { "PrintTotalsAndPayments", "PrintBeforeMF", "PrintMF", "PrintUserMessage" };

    [Fact]
    public void ASLA_yazdirma_cagrilmaz()
    {
        // ← ÇİVİ: aracın çalıştırılabilir olmasının TEK nedeni bu.
        var gmp = new GmpTerminalTransportTests.FakeGmp();

        DiscountProbe.Run(gmp);

        foreach (var c in YazdirmaCagrilari)
            Assert.DoesNotContain(c, gmp.Calls);
    }

    [Fact]
    public void Fis_HER_YOLDA_VoidAll_ve_Close_ile_kapanir()
    {
        // ← ÇİVİ: yarım açılmış fiş bırakılırsa bir SONRAKİ satış onu sessizce iptal eder.
        var gmp = new GmpTerminalTransportTests.FakeGmp();
        gmp.Codes["Minus"] = 1234;   // ortada bir adım patlasın

        var r = DiscountProbe.Run(gmp);

        Assert.Contains("VoidAll", gmp.Calls);
        Assert.Contains("Close", gmp.Calls);
        Assert.True(r.CleanedUp);
    }

    [Fact]
    public void Fis_ACILAMAZSA_da_temizlik_kosar()
    {
        var gmp = new GmpTerminalTransportTests.FakeGmp();
        gmp.Codes["TicketHeader"] = 8;

        var r = DiscountProbe.Run(gmp);

        Assert.Equal(ProbeStatus.SetupFailed, r.Status);
        Assert.Contains("VoidAll", gmp.Calls);
        Assert.Contains("Close", gmp.Calls);
    }

    [Fact]
    public void ACIK_FIS_varsa_HIC_dokunulmaz()
    {
        // ← ÇİVİ: 2080 = cihazda BAŞKA bir fiş var; üzerinde tahsilat olabilir.
        var gmp = new GmpTerminalTransportTests.FakeGmp();
        gmp.Codes["Start"] = GmpCodes.AlreadyDone;

        var r = DiscountProbe.Run(gmp);

        Assert.Equal(ProbeStatus.TicketAlreadyOpen, r.Status);
        Assert.DoesNotContain("VoidAll", gmp.Calls);
        Assert.DoesNotContain("TicketHeader", gmp.Calls);
        Assert.Empty(r.Steps);
    }

    [Fact]
    public void TEK_fis_uzerinde_olculur()
    {
        // ← ÇİVİ: her soru için ayrı fiş açmak beş mali fiş numarası tüketirdi. 2026-09-09'da üç kez
        // ölçüldü: yazdırılmadan iptal edilen fiş de numara alıyor.
        var gmp = new GmpTerminalTransportTests.FakeGmp();

        DiscountProbe.Run(gmp);

        Assert.Equal(1, gmp.Calls.Count(c => c == "Start"));
        Assert.Equal(1, gmp.Calls.Count(c => c == "TicketHeader"));
        Assert.Equal(1, gmp.Calls.Count(c => c == "VoidAll"));
        Assert.Equal(1, gmp.Calls.Count(c => c == "Close"));
    }

    [Fact]
    public void ONCE_indirimsiz_REFERANS_kalem_yazilir()
    {
        // ← ÇİVİ: referans olmadan "fiş toplamı net mi brüt mü" sorusu TEK ölçümle cevaplanamaz —
        // 990 mı 890 mı geldiğini ancak indirimsiz bir kalemle karşılaştırarak anlarız.
        var gmp = new GmpTerminalTransportTests.FakeGmp();

        var r = DiscountProbe.Run(gmp);

        Assert.StartsWith("ItemSale(indirimsiz", r.Steps[0].Name);
        Assert.Contains("promotion", r.Steps[1].Name);
    }

    [Fact]
    public void Bes_sorunun_HEPSI_olculur()
    {
        // ← ÇİVİ: eksik bir adım, cevaplanmamış bir soru demek — ve o soruyu sormak için ikinci bir
        // mali fiş açmak gerekir. Beş adım TEK turda ölçülmeli.
        var gmp = new GmpTerminalTransportTests.FakeGmp();

        var r = DiscountProbe.Run(gmp);

        Assert.Equal(ProbeStatus.Completed, r.Status);
        Assert.Equal(5, r.Steps.Count);
        Assert.Equal(3, gmp.Calls.Count(c => c == "ItemSale"));   // düz + promotion + ikram
        Assert.Single(gmp.MinusCalls);
        Assert.Single(gmp.DecCalls);
    }

    [Fact]
    public void ARA_TOPLAM_sorusu_INDEKS_SIFIR_ile_sorulur()
    {
        // ← ÇİVİ: sorunun kendisi "IndexOfItem=0 bütün fişe mi uygulanır". Başka bir indeksle
        // sorulursa cevap alınmaz — ve belgede bu bilgi YOK, varsayılamaz.
        var gmp = new GmpTerminalTransportTests.FakeGmp();

        DiscountProbe.Run(gmp);

        Assert.Equal(0, gmp.MinusCalls[0].Index);
        Assert.Equal(0, gmp.DecCalls[0].Index);
    }

    [Fact]
    public void TAM_IKRAM_indirimi_kalem_tutarina_ESIT()
    {
        // ← ÇİVİ: "990 → 0 geçer mi" sorusu ancak indirim kalem tutarına EŞİTKEN sorulmuş olur.
        // Bir kuruş eksiği BAŞKA bir soruyu cevaplardı ve biz cevabı "tam ikram" sanırdık.
        //
        // Sınanan şey ADIMIN ETİKETİ DEĞİL, CİHAZA GİDEN KALEM: ilk hâlinde bu test etikete
        // bakıyordu ve indirimi 989'a düşüren mutasyon YEŞİL kaldı — etiket sabit metindi.
        var gmp = new GmpTerminalTransportTests.FakeGmp();

        var r = DiscountProbe.Run(gmp);

        var ikram = gmp.ItemSales[2];
        Assert.Equal(DiscountProbe.ItemAmountMinor, ikram.DiscountMinor);
        Assert.Equal(ikram.UnitPriceMinor, ikram.DiscountMinor);   // ← ÇİVİ: TAM eşit, "neredeyse" değil
        Assert.Contains("TAM İKRAM", r.Steps[4].Name);
    }

    [Fact]
    public void Tanitici_gecersizlesirse_DURUR()
    {
        // ← ÇİVİ: geçersiz tanıtıcıyla devam etmek ölçüm değil gürültü üretir; kalan adımların
        // "hata"sı aslında "sorulamadı"dır ve sözleşme kararı ona dayanamaz.
        var gmp = new GmpTerminalTransportTests.FakeGmp();
        gmp.Codes["Minus"] = GmpCodes.InvalidHandle;

        var r = DiscountProbe.Run(gmp);

        Assert.Equal(ProbeStatus.HandleLost, r.Status);
        Assert.Equal(3, r.Steps.Count);            // düz + promotion + Minus; Dec ve ikram YOK
        Assert.Empty(gmp.DecCalls);
        Assert.Contains("VoidAll", gmp.Calls);     // temizlik yine de koştu
    }

    [Fact]
    public void Dec_HESAPLADIGI_tutar_rapora_giriyor()
    {
        // ← ÇİVİ: oran yolunda kuruş yuvarlamasını CİHAZ yapıyor (`pChangedAmount`). O sayıyı
        // rapora taşımazsak "yüzde mi tutar mı" kararını kendi hesabımıza dayandırmış oluruz —
        // ve `Rate` alanı `byte` olduğu için bizim hesabımız zaten kesirli oranı ifade edemiyor.
        var gmp = new GmpTerminalTransportTests.FakeGmp { DecChanged = 99 };

        var r = DiscountProbe.Run(gmp);

        Assert.Equal(99, r.Steps[3].ChangedMinor);
        Assert.Null(r.Steps[0].ChangedMinor);      // diğer adımlarda anlamı yok
    }

    // ── Satış yolu ETKİLENMEDİ ──────────────────────────────────────────────────

    [Fact]
    public void Indirim_YOKSA_GmpItem_varsayilani_DEGISMEDI()
    {
        // ← ÇİVİ (yapısal): `GmpItem`'a indirim alanları eklendi ama satış yolu onları GÖNDERMİYOR.
        // Varsayılan 0/null olmazsa `GmpWrapper.ItemSale` her kaleme `promotion` iliştirir ve cihaza
        // "0 TL indirim var" der — fişe satır bastırabilecek bir davranış değişikliği.
        var kalem = new GmpItem("Espresso", 990, 1, 10);

        Assert.Equal(0, kalem.DiscountMinor);
        Assert.Null(kalem.DiscountText);
    }
}
