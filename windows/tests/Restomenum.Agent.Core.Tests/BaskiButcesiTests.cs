using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>W109 — SATIŞ ÇAĞRISININ TOPLAM SÜRE BÜTÇESİ.</b>
///
/// <para>Kasa ölçtü (2026-09-10): POST zaman aşımı <b>180 sn</b>. Ajanın adım adım süreleri
/// toplamı kimse tutmadan yan yana duruyordu ve en kötü hâlde <b>600 sn</b>'ye çıkıyordu
/// (Payment 90 + iki baskı 200 + PrintMF 100×3 + Close 10). Yani kasa ÖNCE pes eder, denemeyi
/// belirsiz bırakır ve uzlaştırma turuna düşer — biz devam edip başarırken. Sonuç:
/// <b>kasa "bilinmiyor", cihazda para alınmış.</b></para>
/// </summary>
public class BaskiButcesiTests
{
    private static (BaskiButcesi, Action<long>) Kur(TimeSpan toplam)
    {
        long tik = 1_000_000;
        var b = new BaskiButcesi(toplam, () => tik);
        return (b, ms => tik += ms);
    }

    [Fact]
    public void Adim_suresi_TAVAN_ile_KALAN_in_kucugu()
    {
        var (b, ilerlet) = Kur(BaskiButcesi.Varsayilan);

        // Başta kalan bol: tavan geçerli.
        Assert.Equal(100_000, b.Sure(100_000));

        // Ödeme 90 sn yedi: kalan 75 sn, tavan artık geçerli DEĞİL.
        ilerlet(90_000);
        Assert.Equal(75_000, b.Sure(100_000));

        // ← ÇİVİ: tavanı körlemesine geçirmek, bütçeyi hiç kurmamakla aynı şey olurdu.
    }

    [Fact]
    public void Butce_BITTIYSE_null_doner_SIFIR_zaman_asimiyla_CAGIRTMAZ()
    {
        var (b, ilerlet) = Kur(TimeSpan.FromSeconds(10));
        ilerlet(10_000);

        // ← ÇİVİ: sıfır/negatif zaman aşımının DLL'de ne yaptığı ÖLÇÜLMEDİ. Para taşıyan bir
        // cihazda tanımsız davranışa girmektense HİÇ çağırmamak doğru.
        Assert.Null(b.Sure(100_000));
        Assert.Equal(0, b.KalanMs);
    }

    [Fact]
    public void Anlamsiz_kucuklukteki_kalan_da_CAGIRTMAZ()
    {
        var (b, ilerlet) = Kur(TimeSpan.FromSeconds(10));
        ilerlet(9_800);   // 200 ms kaldı

        // ← ÇİVİ: 200 ms'lik bir zaman aşımıyla yazıcı çağırmak, çağırmamaktan farksız ama
        // cihazı yarım bir işe sokar ve zaman aşımıyla döner — iki kötüyü birden alırdık.
        Assert.Null(b.Sure(100_000));
        Assert.Equal(200, b.KalanMs);
    }

    [Fact]
    public void Kalan_hicbir_zaman_NEGATIF_olmaz()
    {
        var (b, ilerlet) = Kur(TimeSpan.FromSeconds(1));
        ilerlet(60_000);
        Assert.Equal(0, b.KalanMs);
    }

    [Fact]
    public void EN_KOTU_TOPLAM_kasanin_180_saniyesinin_ALTINDA()
    {
        // Bu test bir aritmetik çividir: bütçe + `Close`'un kendi tavanı, kasanın kapısını
        // aşmamalı. Sabitlerden biri büyütülürse burası kırmızıya döner.
        var butceSn = BaskiButcesi.Varsayilan.TotalSeconds;
        const int closeTavaniSn = 10;   // `GmpWrapper.TimeoutDefault`
        const int kasaKapisiSn = 180;   // kasa: LOCAL_TIMEOUT_MS

        Assert.True(butceSn + closeTavaniSn < kasaKapisiSn,
            $"bütçe {butceSn} sn + Close {closeTavaniSn} sn, kasanın {kasaKapisiSn} sn kapısını AŞIYOR");
    }

    [Fact]
    public void Odeme_butceden_YEMEZ_dendiginde_ne_olurdu_ODEME_DAHIL_olmali()
    {
        // ← ÇİVİ: bütçe satışın EN BAŞINDA başlamalı, baskı zincirinde değil. Ödeme 90 sn
        // yerse baskıya 75 sn kalır ve toplam yine kapının altında durur. Bütçeyi baskıda
        // başlatsaydık ödemenin 90 sn'si hesaba girmez, toplam 255 sn'ye çıkardı.
        var (b, ilerlet) = Kur(BaskiButcesi.Varsayilan);
        ilerlet(90_000);                      // Payment en kötü hâli
        var kalan = b.KalanMs;
        Assert.Equal(75_000, kalan);
        Assert.True(90_000 + kalan + 10_000 < 180_000, "ödeme + baskı + Close kapının altında kalmalı");
    }
    // ── KIRPMA: sarmalayicinin tavani vs cagiranin butcesi ──────────────────────

    [Fact]
    public void Kirp_IKISININ_KUCUGUNU_secer()
    {
        // ← ÇİVİ: bu karar önce `GmpWrapper`'da özel bir yardımcıydı ve `Restomenum.Agent.Gmp`'nin
        // TEST PROJESİ YOK — mutasyon ("geçirilen süreyi yoksay") testleri YEŞİL geçti, yani hata
        // hiçbir yerde görünmüyordu. Karar Core'a taşındı ki görünür olsun.
        Assert.Equal(50_000, BaskiButcesi.Kirp(50_000, 100_000));   // bütçe daha dar
        Assert.Equal(100_000, BaskiButcesi.Kirp(150_000, 100_000)); // tavan daha dar
    }

    [Fact]
    public void Kirp_butce_YOKSA_tavani_kullanir()
    {
        // `null` = "çağıranın bütçesi yok" (ör. elle kurtarma yolu). Sıfır/negatif de aynı:
        // anlamsız bir süreyle cihazı çağırmaktansa kendi ölçülmüş tavanımıza düşeriz.
        Assert.Equal(100_000, BaskiButcesi.Kirp(null, 100_000));
        Assert.Equal(100_000, BaskiButcesi.Kirp(0, 100_000));
        Assert.Equal(100_000, BaskiButcesi.Kirp(-5, 100_000));
    }

}
