using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>RM-0389 — kurulum sayfasının ajanı yeniden başlatan / kimliği söken işlemleri.</b> Denetim
/// (Codex + Fable, ed0cffa) dört açık buldu: kaldırma satış ortasına denk gelebiliyordu, başka bir
/// site tarayıcı üzerinden kaldırmayı tetikleyebiliyordu, yarım kalan kaldırma geri dönülemez bir
/// ara duruma düşüyordu ve servis kurtarma ayarı yokken "yeniden başlıyor" yalan oluyordu. Buradaki
/// çiviler bunların her birini ayrı ayrı tutar.
/// </summary>
public class KurulumBakimTests
{
    // ── BAKIM KAPISI: yeni satış girişini atomik kapatır, uçuştakini bekler ────────────────

    [Fact]
    public void Bakimdayken_yeni_satis_GIREMEZ()
    {
        var k = new BakimKapisi();
        Assert.True(k.BakimaAlAsync(TimeSpan.FromSeconds(1)).Result);
        Assert.Null(k.Gir());
        Assert.True(k.Bakimda);
    }

    [Fact]
    public async Task Bakim_ucustaki_satisin_BITMESINI_bekler()
    {
        var k = new BakimKapisi();
        var satis = k.Gir();
        Assert.NotNull(satis);

        var bakim = k.BakimaAlAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(bakim.IsCompleted);          // satış sürüyor → bakım başlamadı
        Assert.Null(k.Gir());                     // ama yeni satış artık giremez (kapı kapandı)

        satis!.Dispose();
        Assert.True(await bakim);
    }

    [Fact]
    public async Task Bakim_suresi_dolarsa_kapiyi_GERI_ACAR()
    {
        // Bitmeyen bir satış yüzünden ajan sonsuza kadar satışa kapalı kalmamalı.
        var k = new BakimKapisi();
        using var satis = k.Gir();
        Assert.False(await k.BakimaAlAsync(TimeSpan.FromMilliseconds(150)));
        Assert.False(k.Bakimda);
        using var yeni = k.Gir();
        Assert.NotNull(yeni);
    }

    [Fact]
    public async Task Ayni_anda_iki_bakim_istegi_ikincisini_reddeder()
    {
        var k = new BakimKapisi();
        Assert.True(await k.BakimaAlAsync(TimeSpan.FromSeconds(1)));
        Assert.False(await k.BakimaAlAsync(TimeSpan.FromSeconds(1)));
        k.BakimdanCik();
        Assert.NotNull(k.Gir());
    }

    [Fact]
    public async Task Yogun_eszamanli_giriste_bakim_basladiktan_sonra_HICBIR_satis_girmez()
    {
        var k = new BakimKapisi();
        var bakimdanSonraGiren = 0;
        var bakimBasladi = false;
        using var dur = new CancellationTokenSource();
        var isciler = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!dur.IsCancellationRequested)
            {
                using var g = k.Gir();
                if (g is not null && Volatile.Read(ref bakimBasladi)) Interlocked.Increment(ref bakimdanSonraGiren);
                await Task.Yield();
            }
        })).ToArray();

        await Task.Delay(50);
        Assert.True(await k.BakimaAlAsync(TimeSpan.FromSeconds(5)));
        Volatile.Write(ref bakimBasladi, true);
        await Task.Delay(100);
        dur.Cancel();
        await Task.WhenAll(isciler);
        Assert.Equal(0, bakimdanSonraGiren);
    }

    // ── KALDIRMA: sıra, kısmi durum, bekleyen iş ─────────────────────────────────────────

    private static (string dizin, string connector, string eklenti) GeciciKurulum(bool connectorVar = true, bool eklentiVar = true)
    {
        var dizin = Path.Combine(Path.GetTempPath(), "rm0389-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dizin);
        var c = Path.Combine(dizin, "connector.id");
        var e = Path.Combine(dizin, "device-config.setup");
        if (connectorVar) File.WriteAllText(c, "conn_eski");
        if (eklentiVar) File.WriteAllText(e, "sir");
        return (dizin, c, e);
    }

    [Fact]
    public void Kaldirma_eklentiyi_SILER_kimligi_adini_degistirip_SAKLAR()
    {
        var (dizin, c, e) = GeciciKurulum();
        var s = CihazKaldirma.Uygula(c, e, bekleyenBildirim: 0, yarimKomut: 0, new DateTime(2026, 10, 14, 14, 6, 50));
        Assert.True(s.Basarili);
        Assert.True(s.YenidenBaslatmaGerekli);
        Assert.False(File.Exists(e));                 // sır: yedek YOK
        Assert.False(File.Exists(c));
        Assert.True(File.Exists(c + ".kaldirildi-20261014-140650"));   // kimlik: saklı
    }

    [Theory]
    [InlineData(1, 0, "bildirilmemiş")]
    [InlineData(0, 2, "yarım")]
    public void Bekleyen_is_varken_kimlik_SOKULMEZ(int bildirim, int komut, string beklenen)
    {
        var (_, c, e) = GeciciKurulum();
        var s = CihazKaldirma.Uygula(c, e, bildirim, komut, DateTime.Now);
        Assert.False(s.Basarili);
        Assert.Contains(beklenen, s.Mesaj);
        Assert.True(File.Exists(c));   // hiçbir şey değişmedi
        Assert.True(File.Exists(e));
    }

    [Fact]
    public void Eklenti_silinemezse_kimlige_DOKUNULMAZ()
    {
        // Sıra önemli: önce eklenti, sonra kimlik. Eklenti silinemezse ajan hâlâ tam kurulu kalır.
        var (_, c, e) = GeciciKurulum();
        using (File.Open(e, FileMode.Open, FileAccess.Read, FileShare.None))   // Windows'ta silmeyi engeller
        {
            if (!OperatingSystem.IsWindows()) return;   // Unix'te açık dosya silinebilir; bu yol orada ölçülemez
            var s = CihazKaldirma.Uygula(c, e, 0, 0, DateTime.Now);
            Assert.False(s.Basarili);
            Assert.Contains("hiçbir şey değişmedi", s.Mesaj);
            Assert.DoesNotContain("process", s.Mesaj, StringComparison.OrdinalIgnoreCase);   // ham istisna metni yok
        }
        Assert.True(File.Exists(c));
    }

    [Fact]
    public void Yarim_kalan_kaldirma_tekrar_denenince_KALDIGI_YERDEN_tamamlanir()
    {
        // Eklenti önceki denemede silinmiş, kimlik taşınamamıştı: ikinci deneme yalnız kalanı yapar.
        var (_, c, e) = GeciciKurulum(connectorVar: true, eklentiVar: false);
        var s = CihazKaldirma.Uygula(c, e, 0, 0, DateTime.Now);
        Assert.True(s.Basarili);
        Assert.False(File.Exists(c));
    }

    [Fact]
    public void Kaldirilacak_bir_sey_yoksa_yeniden_baslatmaz()
    {
        var (_, c, e) = GeciciKurulum(connectorVar: false, eklentiVar: false);
        var s = CihazKaldirma.Uygula(c, e, 0, 0, DateTime.Now);
        Assert.True(s.Basarili);
        Assert.False(s.YenidenBaslatmaGerekli);
    }

    // ── GÜVENLİK: yalnız bu bilgisayardan, yalnız bu sayfadan ────────────────────────────

    private const string Anahtar = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private static string? Post(bool yerel = true, string? host = "127.0.0.1:7788", string? origin = "http://127.0.0.1:7788",
        string? tur = "application/json", string? anahtar = Anahtar) =>
        KurulumGuvenlik.Denetle("POST", yerel, host, origin, tur, anahtar, Anahtar, 7788);

    [Fact]
    public void Sayfadan_gelen_gecerli_istek_KABUL_edilir()
    {
        Assert.Null(Post());
        Assert.Null(Post(origin: null));                        // Origin göndermeyen yerel araç
        Assert.Null(Post(host: "localhost:7788", origin: "http://localhost:7788"));
        Assert.Null(Post(tur: "application/json; charset=utf-8"));
    }

    [Fact]
    public void Baska_siteden_gelen_istek_REDDEDILIR()
    {
        Assert.Contains("kaynak", Post(origin: "https://kotu.example"));
        Assert.Contains("JSON", Post(tur: "text/plain"));           // CORS'suz "basit istek" yolu
        Assert.Contains("JSON", Post(tur: null));
        Assert.Contains("anahtar", Post(anahtar: null));
        Assert.Contains("anahtar", Post(anahtar: "yanlis"));
    }

    [Fact]
    public void Uzak_istemci_ve_yabanci_Host_REDDEDILIR()
    {
        Assert.Contains("bu bilgisayardan", Post(yerel: false));
        Assert.Contains("Host", Post(host: "kotu.example:7788"));   // DNS yeniden bağlama
        Assert.Contains("Host", KurulumGuvenlik.Denetle("GET", true, "kotu.example:7788", null, null, null, Anahtar, 7788));
        Assert.Null(KurulumGuvenlik.Denetle("GET", true, "127.0.0.1:7788", null, null, null, Anahtar, 7788));
    }

    [Fact]
    public void Anahtar_her_seferinde_farkli_ve_tahmin_edilemez()
    {
        var a = KurulumGuvenlik.YeniAnahtar();
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, KurulumGuvenlik.YeniAnahtar());
    }

    // ── SERVİS KURTARMA: ayar yoksa "yeniden başlıyor" DENMEZ ────────────────────────────

    [Fact]
    public void Servis_degilse_yeniden_baslatma_serbest()
        => Assert.Null(YenidenBaslatmaKarari.Engel(servisModu: false, eylemTurleri: null));

    [Fact]
    public void Servis_kurtarma_eylemi_yeniden_baslatsa_serbest()
        => Assert.Null(YenidenBaslatmaKarari.Engel(true, new[] { YenidenBaslatmaKarari.ScActionRestart, 1, 1 }));

    [Fact]
    public void Servis_kurtarma_eylemi_yoksa_ya_da_okunamadiysa_ENGEL_ve_cozum_komutu()
    {
        var yok = YenidenBaslatmaKarari.Engel(true, Array.Empty<int>());
        Assert.Contains("sc.exe failure", yok);
        Assert.Contains("sc.exe failure", YenidenBaslatmaKarari.Engel(true, new[] { 0 }));   // SC_ACTION_NONE
        Assert.Contains("okunamadı", YenidenBaslatmaKarari.Engel(true, null));
    }

    // ── YOL: çalışma dizini değişmeden önce göreli yollar sabitlenir ─────────────────────

    [Fact]
    public void Goreli_yol_baslangic_dizinine_gore_tam_yola_cevrilir()
    {
        var taban = Path.Combine(Path.GetTempPath(), "baslangic");
        Assert.Equal(Path.Combine(taban, "kalemler.json"), Yollar.Tam("kalemler.json", taban));
        var mutlak = Path.Combine(Path.GetTempPath(), "x", "agent.db");
        Assert.Equal(mutlak, Yollar.Tam(mutlak, taban));
        Assert.Null(Yollar.Tam(null, taban));
    }
}
