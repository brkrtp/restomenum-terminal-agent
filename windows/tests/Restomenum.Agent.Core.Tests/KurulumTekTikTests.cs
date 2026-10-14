using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>Tek tık kurulum.</b> Eklentideki düğme <c>http://127.0.0.1:7788/setup#kod=…</c> adresini açar;
/// sayfa kodu alana yerleştirir, kullanıcı "Kaydol"a TEK tıklar. Bağlantı açılınca kendiliğinden
/// kayıt YOK: <c>127.0.0.1</c>'i her site açtırabilir, kendi koduyla ajanı başka hesaba bağlardı.
/// Kod hazır geldiği için sayfa başka bir sitenin çerçevesine de GÖMÜLEMEMELİ (tıklama kaçırma).
/// </summary>
public class KurulumTekTikTests
{
    private const string Anahtar = "abc";

    private sealed class SahteIslemler : IKurulumIslemleri
    {
        public KurulumDurumu Durum() => new(false, false);
        public Task<KurulumSonucu> KaydolAsync(string kod, CancellationToken ct) => Task.FromResult(KurulumSonucu.Tamam("ok"));
        public KurulumSonucu TerminalAyarla(string ip, int port) => KurulumSonucu.Tamam("ok");
        public KurulumSonucu Esles() => KurulumSonucu.Tamam("ok");
        public Task<KurulumSonucu> EklentiAyarlaAsync(DeviceConfigSetup ayar, CancellationToken ct) => Task.FromResult(KurulumSonucu.Tamam("ok"));
        public Task<IReadOnlyList<TerminalAday>> TerminalleriTaraAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TerminalAday>>(Array.Empty<TerminalAday>());
        public KurulumSonucu Kaldir(Func<BekleyenIsler> say) => KurulumSonucu.Tamam("ok");
        public string? YenidenBaslatmaEngeli() => null;
        public bool YenidenBaslat() => true;
    }

    private static KurulumIstekIsleyici Isleyici() =>
        new(new SahteIslemler(), new BakimKapisi(), Anahtar, 7788, () => new BekleyenIsler(0, 0, 0), TimeSpan.FromSeconds(1));

    private static KurulumIstegi Get(string alt) =>
        new("GET", alt, YerelIstemci: true, Host: "127.0.0.1:7788", Origin: null, IcerikTuru: null, Anahtar: null, Govde: "");

    private static string? Baslik(KurulumYaniti y, string ad) =>
        y.Basliklar.FirstOrDefault(b => string.Equals(b.Key, ad, StringComparison.OrdinalIgnoreCase)).Value;

    // ── ÇERÇEVE: sayfa başka siteye gömülemez ─────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("status")]
    [InlineData("yok-boyle-bir-yol")]
    public async Task Kurulum_yanitlari_cerceveye_gomulmeyi_ve_yonlendiren_bilgisini_yasaklar(string alt)
    {
        var y = await Isleyici().IsleAsync(Get(alt), CancellationToken.None);

        Assert.Equal("DENY", Baslik(y, "X-Frame-Options"));
        Assert.Contains("frame-ancestors 'none'", Baslik(y, "Content-Security-Policy"));
        Assert.Equal("no-referrer", Baslik(y, "Referrer-Policy"));
    }

    [Fact]
    public async Task Reddedilen_istek_de_cerceve_yasagini_tasir()
    {
        var y = await Isleyici().IsleAsync(Get("") with { Host = "kotu.example" }, CancellationToken.None);

        Assert.Equal(403, y.Durum);
        Assert.Equal("DENY", Baslik(y, "X-Frame-Options"));
    }

    // ── SAYFA: #kod yerleşir, kendiliğinden gönderilmez ─────────────────────────────────

    private static string Fonksiyon(string html, string ad)
    {
        html = html.Replace("\r\n", "\n");   // kaynak dosyanın satır sonu sayfaya geçer
        var bas = html.IndexOf($"function {ad}(", StringComparison.Ordinal);
        Assert.True(bas >= 0, $"{ad} sayfada yok");
        var son = html.IndexOf("\n}\n", bas, StringComparison.Ordinal);
        Assert.True(son > bas, $"{ad} gövdesi kapanmıyor");
        return html[bas..son];
    }

    [Fact]
    public void Sayfa_adresteki_kodu_alana_yerlestirir_ve_adresten_siler()
    {
        var html = KurulumSayfasi.Html(new KurulumDurumu(Kayitli: false, Esli: false));
        var f = Fonksiyon(html, "hashtenKodYerlestir");

        Assert.Contains("kodHashten(location.hash)", f);
        Assert.Contains("history.replaceState", f);          // kod geçmişte ve yeniden yüklemede kalmaz
        Assert.Contains("getElementById('kod')", f);
        Assert.Contains("hashtenKodYerlestir();", html);     // sayfa açılınca çalışır
    }

    [Fact]
    public void Adresten_gelen_kod_KENDILIGINDEN_gonderilmez()
    {
        var html = KurulumSayfasi.Html(new KurulumDurumu(Kayitli: false, Esli: false));
        var f = Fonksiyon(html, "hashtenKodYerlestir");

        Assert.DoesNotContain("kaydol(", f);
        Assert.DoesNotContain("post(", f);
        Assert.DoesNotContain("fetch(", f);
        Assert.DoesNotContain(".click(", f);
    }

    [Fact]
    public void Kod_cozucu_yalniz_kod_karakterlerini_kabul_eder()
    {
        // Kod alana value ile yazılır (HTML olarak değil); yine de biçim dışı her şey düşer.
        var f = Fonksiyon(KurulumSayfasi.Html(new KurulumDurumu(false, false)), "kodHashten");
        Assert.Contains("/^[A-Za-z0-9-]{4,64}$/", f);
        Assert.Contains("decodeURIComponent", f);
    }

    [Fact]
    public void Kayitli_cihazda_gelen_kod_icin_mesaj_yeri_vardir()
    {
        // Eklenti kayıtlı bir cihazda düğmeye basılırsa kullanıcı sessizlik değil açıklama görmeli.
        var html = KurulumSayfasi.Html(new KurulumDurumu(Kayitli: true, Esli: true));
        Assert.DoesNotContain("id=\"kod\"", html);
        Assert.Contains("id=\"kodMsg\"", html);
    }
}
