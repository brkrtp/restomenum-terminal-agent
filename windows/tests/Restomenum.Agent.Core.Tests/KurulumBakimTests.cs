using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>RM-0389 — kurulum sayfasının ajanı yeniden başlatan / kimliği söken işlemleri.</b> Denetimler
/// (Codex + Fable, ed0cffa ve 7c430dc) şunları buldu: kaldırma satış ya da arka plan bildirimi
/// ortasına denk gelebiliyordu, başka bir site tarayıcı üzerinden kaldırmayı tetikleyebiliyordu,
/// yarım kalan kaldırma geri dönülemez bir ara duruma düşüyordu, servis kurtarma ayarı eksikken
/// "yeniden başlıyor" yalan oluyordu ve çözülmemiş eski satışlar kontrolden kaçıyordu. Buradaki
/// çiviler bunların her birini ayrı ayrı tutar.
/// </summary>
public class KurulumBakimTests
{
    // ── BAKIM KAPISI: yeni girişi atomik kapatır, içeridekini bekler, sahipliği güvenle bırakır ──

    [Fact]
    public async Task Bakimdayken_yeni_is_GIREMEZ()
    {
        var k = new BakimKapisi();
        using var s = await k.BakimaAlAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(s);
        Assert.Null(k.Gir());
        Assert.True(k.Bakimda);
    }

    [Fact]
    public async Task Bakim_icerideki_isin_BITMESINI_bekler()
    {
        var k = new BakimKapisi();
        var satis = k.Gir();
        Assert.NotNull(satis);

        var bakim = k.BakimaAlAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(bakim.IsCompleted);          // iş sürüyor → bakım başlamadı
        Assert.Null(k.Gir());                     // ama yeni iş artık giremez (kapı kapandı)

        satis!.Dispose();
        using var s = await bakim;
        Assert.NotNull(s);
    }

    [Fact]
    public async Task Iki_isten_biri_cikinca_bakim_HALA_bekler()
    {
        var k = new BakimKapisi();
        var a = k.Gir()!;
        var b = k.Gir()!;
        var bakim = k.BakimaAlAsync(TimeSpan.FromSeconds(5));
        a.Dispose();
        await Task.Delay(100);
        Assert.False(bakim.IsCompleted);
        b.Dispose();
        using var s = await bakim;
        Assert.NotNull(s);
    }

    [Fact]
    public async Task Ayni_girisi_iki_kez_birakmak_sayaci_bir_kez_dusurur()
    {
        var k = new BakimKapisi();
        var a = k.Gir()!;
        var b = k.Gir()!;
        a.Dispose();
        a.Dispose();                               // ikinci bırakma b'yi "çıkmış" saymamalı
        var bakim = k.BakimaAlAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(bakim.IsCompleted);
        b.Dispose();
        using var s = await bakim;
        Assert.NotNull(s);
    }

    [Fact]
    public async Task Bakim_suresi_dolarsa_kapiyi_GERI_ACAR()
    {
        // Bitmeyen bir iş yüzünden ajan sonsuza kadar satışa kapalı kalmamalı.
        var k = new BakimKapisi();
        using var satis = k.Gir();
        Assert.Null(await k.BakimaAlAsync(TimeSpan.FromMilliseconds(150)));
        Assert.False(k.Bakimda);
        using var yeni = k.Gir();
        Assert.NotNull(yeni);
    }

    [Fact]
    public async Task Bakim_beklerken_iptal_edilirse_kapiyi_GERI_ACAR()
    {
        var k = new BakimKapisi();
        using var satis = k.Gir();
        using var iptal = new CancellationTokenSource(100);
        Assert.Null(await k.BakimaAlAsync(TimeSpan.FromSeconds(30), iptal.Token));
        Assert.False(k.Bakimda);
    }

    [Fact]
    public async Task Sahiplik_birakilinca_kapi_ACILIR_KapaliTut_denince_ACILMAZ()
    {
        var k = new BakimKapisi();
        using (var s = await k.BakimaAlAsync(TimeSpan.FromSeconds(1))) { Assert.NotNull(s); }
        using (var g = k.Gir()) Assert.NotNull(g);

        var s2 = await k.BakimaAlAsync(TimeSpan.FromSeconds(1));
        s2!.KapaliTut();
        s2.Dispose();
        Assert.Null(k.Gir());                      // yeniden başlatma bekleniyor
    }

    [Fact]
    public async Task Ayni_anda_iki_bakim_istegi_ikincisini_reddeder()
    {
        var k = new BakimKapisi();
        using var s = await k.BakimaAlAsync(TimeSpan.FromSeconds(1));
        Assert.Null(await k.BakimaAlAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Istisna_atan_is_de_kapidan_CIKAR()
    {
        // Arka plan işi (kurtarma/bildirim) patlarsa sayaç asılı kalmamalı — yoksa bakım hiç başlamaz.
        var k = new BakimKapisi();
        try { using var g = k.Gir(); throw new InvalidOperationException(); }
        catch (InvalidOperationException) { }
        using var s = await k.BakimaAlAsync(TimeSpan.FromMilliseconds(200));
        Assert.NotNull(s);
    }

    [Fact]
    public async Task Yogun_eszamanli_giriste_bakim_basladiktan_sonra_HICBIR_is_girmez()
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
        using var s = await k.BakimaAlAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(s);
        Volatile.Write(ref bakimBasladi, true);
        await Task.Delay(100);
        dur.Cancel();
        await Task.WhenAll(isciler);
        Assert.Equal(0, bakimdanSonraGiren);
    }

    // ── KALDIRMA: sıra, kısmi durum, bekleyen iş ─────────────────────────────────────────

    private static readonly DateTime Saat = new(2026, 10, 14, 14, 6, 50);

    private static (CihazKaldirici kaldirici, string connector, string eklenti) GeciciKurulum(bool connectorVar = true, bool eklentiVar = true)
    {
        var dizin = Path.Combine(Path.GetTempPath(), "rm0389-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dizin);
        var c = Path.Combine(dizin, "connector.id");
        var e = Path.Combine(dizin, "device-config.setup");
        if (connectorVar) File.WriteAllText(c, "conn_eski");
        if (eklentiVar) File.WriteAllText(e, "sir");
        return (new CihazKaldirici(c, e, () => Saat), c, e);
    }

    private static BekleyenIsler Bos() => new(0, 0, 0);

    [Fact]
    public void Kaldirma_eklentiyi_SILER_kimligi_adini_degistirip_SAKLAR()
    {
        var (k, c, e) = GeciciKurulum();
        var s = k.Uygula(Bos);
        Assert.True(s.Basarili);
        Assert.True(s.YenidenBaslatmaGerekli);
        Assert.False(File.Exists(e));                 // sır: yedek YOK
        Assert.False(File.Exists(c));
        Assert.True(File.Exists(c + ".kaldirildi-20261014-140650"));   // kimlik: saklı
    }

    [Theory]
    [InlineData(1, 0, 0, "bildirilmemiş")]
    [InlineData(0, 1, 0, "kalıcı olarak reddettiği")]
    [InlineData(0, 0, 2, "kesinleşmemiş")]
    public void Bekleyen_reddedilen_ya_da_cozulmemis_is_varken_kimlik_SOKULMEZ(int bildirim, int reddedilen, int yarim, string beklenen)
    {
        // Codex 4: yaş sınırı YOK — 24 saatten eski çözülmemiş satış da, kalıcı reddedilmiş bildirim de engeller.
        var (k, c, e) = GeciciKurulum();
        var s = k.Uygula(() => new BekleyenIsler(bildirim, reddedilen, yarim));
        Assert.False(s.Basarili);
        Assert.Contains(beklenen, s.Mesaj);
        Assert.DoesNotContain("birkaç dakika", s.Mesaj);   // Fable C: gerçek süreyi söyle, uydurma
        Assert.True(File.Exists(c));
        Assert.True(File.Exists(e));
    }

    [Fact]
    public void Eklenti_silinemezse_kimlige_DOKUNULMAZ()
    {
        // Sıra önemli: önce eklenti, sonra kimlik. Eklenti silinemezse ajan hâlâ tam kurulu kalır.
        // Yalnız Windows'ta ölçülebilir: Unix'te açık dosya silinebilir.
        if (!OperatingSystem.IsWindows()) return;
        var (k, c, e) = GeciciKurulum();
        using (File.Open(e, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var s = k.Uygula(Bos);
            Assert.False(s.Basarili);
            Assert.Contains("hiçbir şey değişmedi", s.Mesaj);
            Assert.DoesNotContain("process", s.Mesaj, StringComparison.OrdinalIgnoreCase);   // ham istisna metni yok
        }
        Assert.True(File.Exists(c));
    }

    [Fact]
    public void Yarim_kalan_kaldirma_tekrar_denenince_KALDIGI_YERDEN_tamamlanir()
    {
        var (k, c, _) = GeciciKurulum(connectorVar: true, eklentiVar: false);
        var s = k.Uygula(Bos);
        Assert.True(s.Basarili);
        Assert.False(File.Exists(c));
    }

    [Fact]
    public void Kaldirilacak_bir_sey_yoksa_yeniden_baslatmaz()
    {
        var (k, _, _) = GeciciKurulum(connectorVar: false, eklentiVar: false);
        var s = k.Uygula(Bos);
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

    [Theory]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void GET_POST_disi_yontem_guvenlikten_GECMEZ(string yontem)
    {
        // Fable A: denetim yalnız POST'a bakıyordu; HEAD ön sorgusuz gelir.
        Assert.Contains("yöntem", KurulumGuvenlik.Denetle(yontem, true, "127.0.0.1:7788", null, null, null, Anahtar, 7788));
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

    // ── SERVİS KURTARMA: TÜM eylemler yeniden başlatma olmalı ────────────────────────────

    private const int R = YenidenBaslatmaKarari.ScActionRestart;

    [Fact]
    public void Servis_degilse_yeniden_baslatma_serbest()
        => Assert.Null(YenidenBaslatmaKarari.Engel(servisModu: false, eylemTurleri: null));

    [Fact]
    public void Servis_kurtarma_eylemlerinin_hepsi_yeniden_baslatsa_serbest()
        => Assert.Null(YenidenBaslatmaKarari.Engel(true, new[] { R, R, R }));

    [Fact]
    public void Ilk_eylem_yeniden_baslatip_sonraki_YOKSA_engel()
    {
        // Codex 3: N'inci hata N'inci eylemi kullanır, son eylem tekrarlanır — [RESTART, NONE] ikinci
        // yeniden başlatmada servisi kapalı bırakır.
        Assert.Contains("sc.exe failure", YenidenBaslatmaKarari.Engel(true, new[] { R, 0 }));
        Assert.Contains("sc.exe failure", YenidenBaslatmaKarari.Engel(true, new[] { R, R, 2 }));
    }

    [Fact]
    public void Servis_kurtarma_eylemi_yoksa_ya_da_okunamadiysa_ENGEL_ve_cozum_komutu()
    {
        Assert.Contains("sc.exe failure", YenidenBaslatmaKarari.Engel(true, Array.Empty<int>()));
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
