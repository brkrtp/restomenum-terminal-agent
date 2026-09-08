using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W57 — yetenek yoklaması. Beyan (<see cref="AgentCapabilities"/>) tahmine değil ÖLÇÜME dayansın diye.
///
/// <para><b>Bu testlerin asıl işi güvenlik:</b> araç mali fiş AÇIYOR. Açtığı fişi yazdırmadan iptal
/// etmesi bir dikkat meselesi değil, çivilenmiş bir kural olmalı — bir <c>PrintBeforeMF</c> çağrısı
/// fişi mali hafızaya sokar ve o fiş bir daha İPTAL EDİLEMEZ (2357).</para>
/// </summary>
public class CapabilityProbeTests
{
    private static readonly string[] YazdirmaCagrilari =
        { "PrintTotalsAndPayments", "PrintBeforeMF", "PrintMF", "PrintUserMessage" };

    [Fact]
    public void ASLA_yazdirma_cagrilmaz()
    {
        // ← ÇİVİ: yoklamanın çalıştırılabilir olmasının TEK nedeni bu. `PrintBeforeMF` çağrılırsa fiş
        // mali hafızaya girer, kâğıda basılır ve iptal edilemez (2357) — teşhis aracı bir müşteriye
        // ait olmayan gerçek bir mali fiş üretmiş olur.
        var gmp = new GmpTerminalTransportTests.FakeGmp();

        CapabilityProbe.Run(gmp);

        foreach (var c in YazdirmaCagrilari)
            Assert.DoesNotContain(c, gmp.Calls);
    }

    [Fact]
    public void Fis_HER_YOLDA_VoidAll_ve_Close_ile_kapanir()
    {
        // ← ÇİVİ: yarım açılmış fiş bırakılırsa bir SONRAKİ satış onu sessizce iptal eder ve o fişin
        // üzerindeki para kaybolur. Ödemeler reddedilse bile temizlik koşmalı.
        var gmp = new GmpTerminalTransportTests.FakeGmp();
        gmp.Codes["Payment"] = 2086;   // hepsi RET

        var r = CapabilityProbe.Run(gmp);

        Assert.Contains("VoidAll", gmp.Calls);
        Assert.Contains("Close", gmp.Calls);
        Assert.True(r.CleanedUp);
        Assert.All(r.Results, s => Assert.False(s.Accepted));
    }

    [Fact]
    public void Fis_ACILAMAZSA_da_temizlik_kosar()
    {
        // ← ÇİVİ: `TicketHeader` hata verse bile `Start` fişi AÇMIŞ olabilir. Erken dönen bir yol
        // temizliği atlarsa cihazda sahipsiz bir fiş kalır.
        var gmp = new GmpTerminalTransportTests.FakeGmp();
        gmp.Codes["TicketHeader"] = 8;

        var r = CapabilityProbe.Run(gmp);

        Assert.Equal(ProbeStatus.SetupFailed, r.Status);
        Assert.Equal("TicketHeader", r.FailedStep);
        Assert.Contains("VoidAll", gmp.Calls);
        Assert.Contains("Close", gmp.Calls);
        Assert.Empty(r.Results);
    }

    [Fact]
    public void ACIK_FIS_varsa_HIC_dokunulmaz()
    {
        // ← ÇİVİ: 2080 = cihazda BAŞKA bir fiş var; üzerinde tahsilat olabilir. Onu iptal etmek bu
        // aracın işi değil — `VoidAll` bile çağrılmamalı.
        var gmp = new GmpTerminalTransportTests.FakeGmp();
        gmp.Codes["Start"] = GmpCodes.AlreadyDone;

        var r = CapabilityProbe.Run(gmp);

        Assert.Equal(ProbeStatus.TicketAlreadyOpen, r.Status);
        Assert.DoesNotContain("VoidAll", gmp.Calls);
        Assert.DoesNotContain("Close", gmp.Calls);
        Assert.DoesNotContain("TicketHeader", gmp.Calls);
        Assert.Empty(r.Results);
    }

    [Fact]
    public void Her_aday_TEK_fis_uzerinde_olculur()
    {
        // ← ÇİVİ: her tip için ayrı fiş açmak dört mali fiş numarası tüketirdi. FR07'de ölçüldü:
        // iptal edilen boş fiş de numara alıyor (FNo 2). Tek fiş = en küçük mali ayak izi.
        var gmp = new GmpTerminalTransportTests.FakeGmp();

        var r = CapabilityProbe.Run(gmp);

        Assert.Equal(1, gmp.Calls.Count(c => c == "Start"));
        Assert.Equal(1, gmp.Calls.Count(c => c == "TicketHeader"));
        Assert.Equal(1, gmp.Calls.Count(c => c == "ItemSale"));
        Assert.Equal(CapabilityProbe.Candidates.Count, gmp.PaymentRequests.Count);
        Assert.Equal(CapabilityProbe.Candidates.Select(c => c.Type), gmp.PaymentRequests.Select(p => p.PaymentType));
    }

    [Fact]
    public void Fis_HIC_TAMAMLANMAZ_kalem_odemelerden_buyuk()
    {
        // ← ÇİVİ: kalem tutarı ödemelerin toplamına eşit olsaydı fiş TAM ödenmiş olur ve kapanmaya
        // hazır hâle gelirdi — iptal yerine yanlışlıkla basılabilecek bir fiş.
        var gmp = new GmpTerminalTransportTests.FakeGmp();

        CapabilityProbe.Run(gmp);

        var toplamOdeme = gmp.PaymentRequests.Sum(p => p.AmountMinor);
        Assert.True(CapabilityProbe.ItemAmountMinor > toplamOdeme,
            $"kalem {CapabilityProbe.ItemAmountMinor} ≤ ödeme toplamı {toplamOdeme}");
    }

    [Fact]
    public void Tanitici_gecersizlesirse_DURUR_ve_kalanlar_olculmez()
    {
        // ← ÇİVİ: geçersiz tanıtıcıyla devam etmek ölçüm değil gürültü üretir; "RET" gibi görünen
        // sonuçlar aslında "sorulamadı"dır ve beyan onlara dayanamaz.
        var gmp = new GmpTerminalTransportTests.FakeGmp();
        gmp.PaymentSequence.Enqueue(GmpCodes.Ok);
        gmp.PaymentSequence.Enqueue(GmpCodes.InvalidHandle);

        var r = CapabilityProbe.Run(gmp);

        Assert.Equal(ProbeStatus.HandleLost, r.Status);
        Assert.Equal(2, r.Results.Count);          // ← ÇİVİ: dört değil, İKİ
        Assert.Contains("VoidAll", gmp.Calls);     // temizlik yine de koştu
    }

    [Fact]
    public void Adaylar_UYGULAMA_KOSULU_olmayan_tipler()
    {
        // ← ÇİVİ: DLL'in kendi tablosunda YEMEKCEKI (8) ve MOBILE (16) yanında "(Uygulama varsa)"
        // yazıyor ve bu terminalde kurulu böyle bir uygulama YOK (--payment-apps: 4 kayıt, dördü de
        // banka kartı). Onları yoklamak "cihaz desteklemiyor"u değil "uygulama yok"u ölçerdi.
        // DÖVİZ (2) de dışarıda: ajan her tutarı TL gönderiyor.
        var tipler = CapabilityProbe.Candidates.Select(c => c.Type).ToList();

        Assert.Equal(new[] { 32, 512, 2048, 4096 }, tipler);
        Assert.DoesNotContain(GmpPaymentTypes.MealVoucher, tipler);
        Assert.DoesNotContain(GmpPaymentTypes.Mobile, tipler);
        Assert.DoesNotContain(2, tipler);
    }

    [Fact]
    public void Adaylarin_hepsi_W54_ile_KABUL_edilen_tipler()
    {
        // ← ÇİVİ: ölçülüp beyan edilen bir tipi satış yolu reddediyorsa beyan yalan olur. Yoklama ile
        // `IsKnown` aynı listeye bakmalı.
        Assert.All(CapabilityProbe.Candidates, c => Assert.True(GmpPaymentTypes.IsKnown(c.Type)));
    }
}
