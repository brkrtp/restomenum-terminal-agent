using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>RM-0389 denetim 2 — kurulum isteklerinin yönlendirilmesi.</b> AgentWorker'daki HTTP
/// yönlendirmesi Core'a taşındı (<see cref="KurulumIstekIsleyici"/>) ki şu iki sınıf hata ölçülebilsin:
/// bakım kapısı bir hata yolunda KAPALI kalıp bütün satışları süresiz 503'e düşürmesin (Codex 1) ve
/// GET/POST dışındaki yöntemler işlem dalına girmesin (Fable A — HEAD ön sorgusuz gönderilebilir).
/// </summary>
public class KurulumIstekIsleyiciTests
{
    private const string Anahtar = "abc";

    private sealed class SahteIslemler : IKurulumIslemleri
    {
        public int TerminalCagrisi, KaldirCagrisi, EslesCagrisi;
        public Exception? TerminalFirlat;
        public string? Engel;
        public KurulumSonucu TerminalSonucu = KurulumSonucu.Tamam("yazıldı", yenidenBaslat: true);
        public BekleyenIsler? GorulenSayim;
        public BakimKapisi? Kapi;
        public bool EklentiBakimdaydi;
        public Func<bool> YenidenBaslatSonucu = () => true;
        public int YenidenBaslatCagrisi;

        public KurulumDurumu Durum() => new(true, true);
        public Task<KurulumSonucu> KaydolAsync(string kod, CancellationToken ct) => Task.FromResult(KurulumSonucu.Tamam("ok"));
        public KurulumSonucu TerminalAyarla(string ip, int port)
        {
            TerminalCagrisi++;
            if (TerminalFirlat is not null) throw TerminalFirlat;
            return TerminalSonucu;
        }
        public KurulumSonucu Esles() { EslesCagrisi++; return KurulumSonucu.Tamam("eşli"); }
        public Task<KurulumSonucu> EklentiAyarlaAsync(DeviceConfigSetup ayar, CancellationToken ct)
        {
            EklentiBakimdaydi = Kapi?.Bakimda ?? false;
            return Task.FromResult(KurulumSonucu.Tamam("ok", yenidenBaslat: true));
        }
        public Task<IReadOnlyList<TerminalAday>> TerminalleriTaraAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TerminalAday>>(Array.Empty<TerminalAday>());
        public KurulumSonucu Kaldir(Func<BekleyenIsler> say)
        {
            KaldirCagrisi++;
            GorulenSayim = say();
            return KurulumSonucu.Tamam("kaldırıldı", yenidenBaslat: true);
        }
        public string? YenidenBaslatmaEngeli() => Engel;
        public bool YenidenBaslat() { YenidenBaslatCagrisi++; return YenidenBaslatSonucu(); }
    }

    private static (KurulumIstekIsleyici isleyici, SahteIslemler islem, BakimKapisi kapi) Kur(Func<BekleyenIsler>? say = null)
    {
        var islem = new SahteIslemler();
        var kapi = new BakimKapisi();
        islem.Kapi = kapi;
        var isleyici = new KurulumIstekIsleyici(islem, kapi, Anahtar, 7788,
            say ?? (() => new BekleyenIsler(0, 0, 0)), TimeSpan.FromSeconds(1));
        return (isleyici, islem, kapi);
    }

    private static KurulumIstegi Istek(string yontem, string alt, string govde = "{}", string? tur = "application/json",
        string? anahtar = Anahtar) =>
        new(yontem, alt, YerelIstemci: true, Host: "127.0.0.1:7788", Origin: "http://127.0.0.1:7788",
            IcerikTuru: tur, Anahtar: anahtar, Govde: govde);

    private static void KapiAcik(BakimKapisi k)
    {
        using var g = k.Gir();
        Assert.NotNull(g);
    }

    // ── YÖNTEM: işlem dalına yalnız POST girer ───────────────────────────────────────────

    [Theory]
    [InlineData("HEAD", "pair")]
    [InlineData("HEAD", "terminal")]
    [InlineData("OPTIONS", "remove")]
    [InlineData("PUT", "plugin")]
    [InlineData("DELETE", "remove")]
    [InlineData("PATCH", "terminal")]
    public async Task GET_POST_disi_yontem_405_alir_kapiya_ve_cihaza_DOKUNMAZ(string yontem, string alt)
    {
        var (isleyici, islem, kapi) = Kur();
        var y = await isleyici.IsleAsync(Istek(yontem, alt, "{\"ip\":\"1.2.3.4\",\"confirm\":\"KALDIR\"}"), default);
        Assert.Equal(405, y.Durum);
        Assert.False(y.YenidenBaslat);
        Assert.Equal(0, islem.TerminalCagrisi + islem.KaldirCagrisi + islem.EslesCagrisi);
        KapiAcik(kapi);
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("")]
    [InlineData("status")]
    public async Task HEAD_govdesiz_405_alir(string alt)
    {
        // Sahada ölçüldü (2026-10-14 15:12): HEAD'e gövde yazmak HttpListener'da istisna attı, 405 hiç
        // ulaşmadı ve her HEAD bir ERR satırı üretti. HEAD yanıtında gövde OLAMAZ (RFC 9110 §9.3.2).
        var (isleyici, _, kapi) = Kur();
        var y = await isleyici.IsleAsync(Istek("HEAD", alt), default);
        Assert.Equal(405, y.Durum);
        Assert.Equal("", y.Govde);
        Assert.True(y.Govdesiz);
        KapiAcik(kapi);
    }

    [Theory]
    [InlineData("HEAD", false)]
    [InlineData("head", false)]
    [InlineData("GET", true)]
    [InlineData("POST", true)]
    [InlineData("OPTIONS", true)]
    public void HEAD_yanitina_hicbir_yolda_govde_yazilmaz(string yontem, bool yazilir)
        => Assert.Equal(yazilir, HttpGovde.Yazilir(yontem));

    [Fact]
    public async Task GET_yalniz_sayfa_durum_ve_tarama_icindir_islem_yollari_405()
    {
        var (isleyici, islem, kapi) = Kur();
        Assert.Equal(200, (await isleyici.IsleAsync(Istek("GET", ""), default)).Durum);
        Assert.Equal(200, (await isleyici.IsleAsync(Istek("GET", "status"), default)).Durum);
        Assert.Equal(200, (await isleyici.IsleAsync(Istek("GET", "scan"), default)).Durum);
        Assert.Equal(405, (await isleyici.IsleAsync(Istek("GET", "pair"), default)).Durum);
        Assert.Equal(0, islem.EslesCagrisi);
        KapiAcik(kapi);
    }

    [Fact]
    public async Task Eslestirme_de_POST_kurallarina_tabidir()
    {
        var (isleyici, islem, _) = Kur();
        Assert.Equal(403, (await isleyici.IsleAsync(Istek("POST", "pair", tur: "text/plain"), default)).Durum);
        Assert.Equal(403, (await isleyici.IsleAsync(Istek("POST", "pair", anahtar: null), default)).Durum);
        Assert.Equal(0, islem.EslesCagrisi);
        Assert.Equal(200, (await isleyici.IsleAsync(Istek("POST", "pair"), default)).Durum);
        Assert.Equal(1, islem.EslesCagrisi);
    }

    // ── BAKIM KAPISI HİÇBİR HATA YOLUNDA KAPALI KALMAZ ───────────────────────────────────

    [Fact]
    public async Task Tasan_port_KAPIDAN_ONCE_reddedilir_kapi_acik_kalir()
    {
        // Codex 1: {"port":2147483648} GetInt32'de istisna atıyordu; kapı kapanmışken 500 → satışlar süresiz 503.
        var (isleyici, islem, kapi) = Kur();
        var y = await isleyici.IsleAsync(Istek("POST", "terminal", "{\"ip\":\"127.0.0.1\",\"port\":2147483648}"), default);
        Assert.Equal(200, y.Durum);
        Assert.Contains("\"ok\":false", y.Govde);
        Assert.Equal(0, islem.TerminalCagrisi);
        KapiAcik(kapi);
    }

    [Fact]
    public async Task Islem_basarisiz_donerse_kapi_ACILIR()
    {
        var (isleyici, islem, kapi) = Kur();
        islem.TerminalSonucu = KurulumSonucu.Hata("yazılamadı");
        var y = await isleyici.IsleAsync(Istek("POST", "terminal", "{\"ip\":\"127.0.0.1\"}"), default);
        Assert.False(y.YenidenBaslat);
        KapiAcik(kapi);
    }

    [Fact]
    public async Task Islem_istisna_atarsa_guvenli_kurtarma_kapi_kapali_ve_yeniden_baslatma()
    {
        // Değişiklik yapılıp yapılmadığı bilinmiyor: süreç bu durumda satış almamalı, temiz açılış şart.
        var (isleyici, islem, kapi) = Kur();
        islem.TerminalFirlat = new IOException("disk");
        var y = await isleyici.IsleAsync(Istek("POST", "terminal", "{\"ip\":\"127.0.0.1\"}"), default);
        Assert.True(y.YenidenBaslat);
        Assert.Contains("\"ok\":false", y.Govde);
        Assert.DoesNotContain("disk", y.Govde);   // ham istisna metni kullanıcıya gitmez
        Assert.Null(kapi.Gir());
    }

    [Fact]
    public async Task Basarili_ve_yeniden_baslatma_gerektiren_islem_kapiyi_KAPALI_tutar()
    {
        var (isleyici, _, kapi) = Kur();
        var y = await isleyici.IsleAsync(Istek("POST", "terminal", "{\"ip\":\"127.0.0.1\"}"), default);
        Assert.True(y.YenidenBaslat);
        Assert.Null(kapi.Gir());
    }

    [Fact]
    public async Task Yeniden_baslatma_engeli_varsa_hicbir_sey_degismez_kapi_acik()
    {
        var (isleyici, islem, kapi) = Kur();
        islem.Engel = "SCM recovery missing";
        var y = await isleyici.IsleAsync(Istek("POST", "remove", "{\"confirm\":\"KALDIR\"}"), default);
        Assert.Contains("SCM recovery missing", y.Govde);
        Assert.Equal(0, islem.KaldirCagrisi);
        KapiAcik(kapi);
    }

    [Fact]
    public async Task Kaldirma_sayimi_kapi_kapandiktan_SONRA_yapilir()
    {
        // Fable B: sayı değil sayan fonksiyon geçer; işlem tarafı kilidi aldıktan sonra sayar.
        BakimKapisi? gozlenen = null;
        var bakimdaydi = false;
        var (isleyici, islem, kapi) = Kur(() => { bakimdaydi = gozlenen!.Bakimda; return new BekleyenIsler(0, 0, 0); });
        gozlenen = kapi;
        await isleyici.IsleAsync(Istek("POST", "remove", "{\"confirm\":\"KALDIR\"}"), default);
        Assert.Equal(1, islem.KaldirCagrisi);
        Assert.True(bakimdaydi);
    }

    // ── EKLENTİ DİZESİ de bakım kapısından geçer (denetim 3, Fable — bozma B12 yaşıyordu) ──────

    [Fact]
    public async Task Eklenti_ayari_BAKIM_kapisi_kapaliyken_yapilir_ve_kapiyi_kapali_tutar()
    {
        var (isleyici, islem, kapi) = Kur();
        var dize = DeviceConfigSetupParser.Kodla(new DeviceConfigSetup(new Uri("https://eklenti.example/"), "sir"));
        var y = await isleyici.IsleAsync(Istek("POST", "plugin", "{\"setup\":\"" + dize + "\"}"), default);
        Assert.True(islem.EklentiBakimdaydi);
        Assert.True(y.YenidenBaslat);
        Assert.Null(kapi.Gir());
    }

    // ── YENİDEN BAŞLATMA BAŞARISIZSA kapı açılır, sayfa "elle yeniden başlatın" der ────────────

    private static async Task<(KurulumIstekIsleyici, SahteIslemler, BakimKapisi)> YenidenBaslatmaBekleyen()
    {
        var (isleyici, islem, kapi) = Kur();
        var y = await isleyici.IsleAsync(Istek("POST", "terminal", "{\"ip\":\"127.0.0.1\"}"), default);
        Assert.True(y.YenidenBaslat);
        Assert.Null(kapi.Gir());
        return (isleyici, islem, kapi);
    }

    [Fact]
    public async Task Yeniden_baslatma_basarisiz_donerse_kapi_ACILIR_ve_durum_elle_baslatin_der()
    {
        // Denetim 3 (Fable): konsol modunda yeni süreç başlatılamazsa kapı sonsuza kadar kapalı kalıyordu.
        var (isleyici, islem, kapi) = await YenidenBaslatmaBekleyen();
        islem.YenidenBaslatSonucu = () => false;
        Assert.False(isleyici.YenidenBaslat());
        KapiAcik(kapi);
        var durum = await isleyici.IsleAsync(Istek("GET", "status"), default);
        Assert.Contains("elle yeniden", durum.Govde);
    }

    [Fact]
    public async Task Yeniden_baslatma_istisna_atarsa_da_kapi_ACILIR()
    {
        var (isleyici, islem, kapi) = await YenidenBaslatmaBekleyen();
        islem.YenidenBaslatSonucu = () => throw new System.ComponentModel.Win32Exception(5);
        Assert.False(isleyici.YenidenBaslat());
        KapiAcik(kapi);
    }

    [Fact]
    public async Task Yeniden_baslatma_basarili_donerse_kapi_KAPALI_kalir()
    {
        var (isleyici, islem, kapi) = await YenidenBaslatmaBekleyen();
        Assert.True(isleyici.YenidenBaslat());
        Assert.Equal(1, islem.YenidenBaslatCagrisi);
        Assert.Null(kapi.Gir());
    }
}
