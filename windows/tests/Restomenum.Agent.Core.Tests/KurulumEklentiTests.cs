using System.Text;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>W150 — kurulum sayfası, 4. adım: eklenti kurulum dizesi.</b> Çiviler: yanlış dize DOĞRU sebeple
/// reddedilir ve sebep SIRRI İÇERMEZ; dosya yapılandırmayı ezer ama bozuk dosya yapılandırmaya
/// düşmez; sayfa ve JSON sırrı hiç görmez.
/// </summary>
public class KurulumEklentiTests
{
    private const string Sir = "GIZLI-SIR-0123456789";

    private static string Dize(string url, string? secret = Sir)
    {
        var json = secret is null ? $"{{\"url\":\"{url}\"}}" : $"{{\"url\":\"{url}\",\"secret\":\"{secret}\"}}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    // ── AYRIŞTIRMA: sebep AYRIŞIR ve sırrı taşımaz ─────────────────────────────

    [Fact]
    public void Gecerli_dize_cozulur_ve_taban_adres_normalize_edilir()
    {
        var a = DeviceConfigSetupParser.TryParse(Dize("https://plugin.example.workers.dev"), out var neden);
        Assert.NotNull(a);
        Assert.Null(neden);
        Assert.Equal("https://plugin.example.workers.dev/", a!.BaseUri.AbsoluteUri);
        Assert.Equal(Sir, a.Secret);
    }

    [Theory]
    [InlineData("", "boş")]
    [InlineData("bu base64 değil!!", "base64")]
    [InlineData("ZHV6IG1ldGlu", "JSON")]                                  // "düz metin" base64'ü
    public void Bozuk_dize_sebebiyle_reddedilir(string dize, string parca)
    {
        Assert.Null(DeviceConfigSetupParser.TryParse(dize, out var neden));
        Assert.Contains(parca, neden);
    }

    [Fact]
    public void Url_ve_sir_eksikligi_ayri_sebeplerle_reddedilir()
    {
        Assert.Null(DeviceConfigSetupParser.TryParse(Dize("https://p.test", secret: null), out var sirYok));
        Assert.Contains("secret", sirYok);

        var urlYok = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"secret\":\"x\"}"));
        Assert.Null(DeviceConfigSetupParser.TryParse(urlYok, out var neden));
        Assert.Contains("url", neden);
    }

    [Theory]
    [InlineData("http://plugin.example.com", "https")]                    // düz http, loopback değil
    [InlineData("ftp://plugin.example.com", "https")]
    [InlineData("https://plugin.example.com/api", "kök")]                 // yol var
    [InlineData("https://plugin.example.com/?x=1", "kök")]                // sorgu var
    [InlineData("plugin.example.com", "URL")]                             // şema yok
    public void Adres_kurallari_eklenti_sahibinin_tarifi(string url, string parca)
    {
        Assert.Null(DeviceConfigSetupParser.TryParse(Dize(url), out var neden));
        Assert.Contains(parca, neden);
    }

    [Fact]
    public void Loopback_http_gelistirme_icin_kabul()
    {
        Assert.NotNull(DeviceConfigSetupParser.TryParse(Dize("http://127.0.0.1:8787"), out var n1));
        Assert.NotNull(DeviceConfigSetupParser.TryParse(Dize("http://localhost:8787"), out var n2));
        Assert.Null(n1); Assert.Null(n2);
    }

    [Fact]
    public void Red_sebebi_sirri_ASLA_icermez()
    {
        // Her red yolunu sırla besle; sebep metninde sır geçmemeli. Sayfa bu metni kullanıcıya basar,
        // AgentWorker log'a yazar — ikisi de sır için yanlış yer.
        var yollar = new[]
        {
            Dize("http://plugin.example.com"), Dize("https://plugin.example.com/api"),
            Dize("plugin.example.com"), Dize("https://p.test", secret: null),
        };
        foreach (var d in yollar)
        {
            Assert.Null(DeviceConfigSetupParser.TryParse(d, out var neden));
            Assert.NotNull(neden);
            Assert.DoesNotContain(Sir, neden);
            Assert.DoesNotContain(d, neden);   // dizenin kendisi de (base64 sır) yok
        }
    }

    [Fact]
    public void Kodla_TryParse_ile_gidip_gelir()
    {
        var a = DeviceConfigSetupParser.TryParse(Dize("https://plugin.example.workers.dev"))!;
        var geri = DeviceConfigSetupParser.TryParse(DeviceConfigSetupParser.Kodla(a), out var neden);
        Assert.Null(neden);
        Assert.Equal(a, geri);
    }

    [Fact]
    public void Istek_govdesi_setup_alani_ister()
    {
        Assert.Null(KurulumIstek.Eklenti("{}", out var yok));
        Assert.Contains("setup", yok);

        Assert.Null(KurulumIstek.Eklenti("{\"setup\":\"" + new string('A', 5000) + "\"}", out var uzun));
        Assert.Contains("uzun", uzun);

        var a = KurulumIstek.Eklenti("{\"setup\":\"  " + Dize("https://p.test") + " \"}", out var neden);
        Assert.NotNull(a);
        Assert.Null(neden);
        Assert.Equal("p.test", a!.BaseUri.Host);
    }

    // ── KAYNAK: dosya > yapılandırma; bozuk dosya yapılandırmaya DÜŞMEZ ────────

    [Fact]
    public void Dosya_yapilandirmayi_ezer()
    {
        var a = DeviceConfigSetupKaynagi.Coz(Dize("https://yeni.test"), Dize("https://eski.test"), out var kaynak);
        Assert.Equal("yeni.test", a!.BaseUri.Host);
        Assert.Equal("dosya", kaynak);
    }

    [Fact]
    public void Dosya_yoksa_yapilandirma()
    {
        var a = DeviceConfigSetupKaynagi.Coz(null, Dize("https://eski.test"), out var kaynak);
        Assert.Equal("eski.test", a!.BaseUri.Host);
        Assert.Equal("yapılandırma", kaynak);
    }

    [Fact]
    public void Bozuk_dosya_yapilandirmaya_DUSMEZ_ve_bozuk_der()
    {
        // Sessizce eski adrese dönmek, kullanıcının "kaydettim" sandığı ayarın hiç uygulanmaması demek.
        var a = DeviceConfigSetupKaynagi.Coz("bozuk!!", Dize("https://eski.test"), out var kaynak);
        Assert.Null(a);
        Assert.StartsWith("dosya BOZUK", kaynak);
        Assert.DoesNotContain(Sir, kaynak);
    }

    [Fact]
    public void Ikisi_de_yoksa_yok()
    {
        Assert.Null(DeviceConfigSetupKaynagi.Coz(null, null, out var kaynak));
        Assert.Equal("yok", kaynak);
    }

    // ── SAYFA / JSON: sır görünmez, adım her durumda var ─────────────────────────

    [Fact]
    public void Sayfa_eklenti_yoksa_uyarir_varsa_hostu_gosterir_ve_alan_HER_durumda_vardir()
    {
        var yok = KurulumSayfasi.Html(new KurulumDurumu(true, true));
        Assert.Contains("Eklenti bağlantısı yok", yok);
        Assert.Contains("id=\"dize\"", yok);

        var var_ = KurulumSayfasi.Html(new KurulumDurumu(true, true, EklentiAdresi: "plugin.example.workers.dev"));
        Assert.Contains("Eklenti bağlı: plugin.example.workers.dev", var_);
        Assert.Contains("id=\"dize\"", var_);   // yeni sır üretilince eski geçersiz — yeniden yapıştırma yolu açık kalır
        Assert.Contains("/setup/plugin", var_);
    }

    [Fact]
    public void Durum_tamam_ucu_birden_ister()
    {
        Assert.False(new KurulumDurumu(true, true).Tamam);                         // eklenti yok
        Assert.False(new KurulumDurumu(true, false, EklentiAdresi: "h").Tamam);    // eşli değil
        Assert.True(new KurulumDurumu(true, true, EklentiAdresi: "h").Tamam);
    }

    [Fact]
    public void Durum_JSON_u_yalniz_hostu_tasir()
    {
        var j = KurulumIstek.Json(new KurulumDurumu(true, true, EklentiAdresi: "plugin.example.workers.dev"));
        Assert.Contains("\"pluginHost\":\"plugin.example.workers.dev\"", j);
        Assert.Contains("\"pluginHost\":null", KurulumIstek.Json(new KurulumDurumu(true, true)));
    }
}
