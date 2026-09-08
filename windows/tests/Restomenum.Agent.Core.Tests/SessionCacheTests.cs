using System.Net;
using System.Text;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W52 — oturum jetonu ÖNBELLEKLENİYOR.
///
/// <para><b>Neden (ölçüm 2026-09-08 22:45):</b> önbellek yoktu; platforma giden HER çağrı baştan
/// bir oturum POST'u atıyordu. Tek satışta <b>3 tur</b> sayıldı (bulut günlüğü 09.370 / 27.861 /
/// 29.355). Sıcakken ~140 ms, SOĞUKKEN <b>4,59 sn</b> — o gün 20 saniyelik satışın yarısı buydu.</para>
/// </summary>
public class SessionCacheTests
{
    private sealed class SayanIsleyici : HttpMessageHandler
    {
        public int Istek;
        public int ExpiresInSec = 900;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Istek++;
            var govde = "{\"success\":true,\"data\":{\"token\":\"jwt-" + Istek
                + "\",\"expiresInSec\":" + ExpiresInSec + ",\"serverTime\":0}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(govde, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SahteAnahtar : IDeviceKey
    {
        public byte[] Sign(byte[] data) => new byte[] { 1, 2, 3 };
        public string Fingerprint => "fp";
        public string ConnectorId => "conn_test";
        public string PublicKeyPem => "";
    }

    private long _saatMs = 1_700_000_000_000;

    private (HttpSessionProvider, SayanIsleyici) Kur(int expiresInSec = 900)
    {
        var h = new SayanIsleyici { ExpiresInSec = expiresInSec };
        var p = new HttpSessionProvider(new HttpClient(h), new SahteAnahtar(), "srv",
            new Uri("https://ornek.test/session"), new ClockOffset(),
            now: () => DateTimeOffset.FromUnixTimeMilliseconds(_saatMs));
        return (p, h);
    }

    [Fact]
    public async Task Gecerli_jeton_YENIDEN_KULLANILIR()
    {
        // ← ÇİVİ: bu testin kırmızıya döndüğü hâl, sahada ölçtüğümüz "satış başına 3 bulut turu".
        var (p, h) = Kur();

        var a = await p.AcquireAsync();
        var b = await p.AcquireAsync();
        var c = await p.AcquireAsync();

        Assert.Equal(1, h.Istek);          // ← ÇİVİ: üç mantıksal alım, TEK tur
        Assert.Equal(a.Token, b.Token);
        Assert.Equal(a.Token, c.Token);
    }

    [Fact]
    public async Task Sure_dolunca_YENIDEN_alinir()
    {
        var (p, h) = Kur(expiresInSec: 900);
        await p.AcquireAsync();

        // 900 sn − 60 sn marj = 840 sn geçerli. 839'da hâlâ önbellek, 841'de yeniden.
        _saatMs += 839_000;
        await p.AcquireAsync();
        Assert.Equal(1, h.Istek);

        _saatMs += 2_000;
        await p.AcquireAsync();
        Assert.Equal(2, h.Istek);
    }

    [Fact]
    public async Task MARJ_son_saniyeye_kadar_kullandirmaz()
    {
        // ← ÇİVİ: jetonu son saniyesine kadar kullanmak, tam da uçta işlenirken dolmasına yol
        // açardı — ve o hata satışın ORTASINDA görünürdü. Marj bunun için var.
        var (p, h) = Kur(expiresInSec: 900);
        await p.AcquireAsync();

        _saatMs += 850_000;                 // ömrün içinde (900) ama marjın dışında (840)
        await p.AcquireAsync();

        Assert.Equal(2, h.Istek);
    }

    [Fact]
    public async Task OMUR_marjdan_kisaysa_ONBELLEK_KURULMAZ()
    {
        // ← ÇİVİ: `expiresInSec` marjdan küçükse (ya da 0/bozuk gelirse) uydurma bir ömür
        // vermektense önbelleksiz çalışmak doğru — geçersiz jetonla satışa girmektense bir tur
        // daha atmak.
        var (p, h) = Kur(expiresInSec: 30);

        await p.AcquireAsync();
        await p.AcquireAsync();

        Assert.Equal(2, h.Istek);
    }

    [Fact]
    public async Task Invalidate_SONRAKI_alimda_yeni_jeton()
    {
        // 401 alan çağıran bunu söyler; jeton uçta bizden önce geçersiz olmuş olabilir.
        var (p, h) = Kur();
        var a = await p.AcquireAsync();

        p.Invalidate();
        var b = await p.AcquireAsync();

        Assert.Equal(2, h.Istek);
        Assert.NotEqual(a.Token, b.Token);
    }

    // ── 401 → TEK yenileme + TEK tekrar (fırtına YOK) ───────────────────────────

    private sealed class YanitIsleyici : HttpMessageHandler
    {
        public readonly Queue<HttpStatusCode> Sira = new();
        public int Istek;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Istek++;
            var kod = Sira.Count > 0 ? Sira.Dequeue() : HttpStatusCode.OK;
            // Başarı gövdesinin ŞEKLİ burada sınanmıyor (o `PaymentDetailParserTests`'in işi);
            // burada sınanan şey KAÇ istek gittiği ve kaç kez yenilendiği.
            var govde = kod == HttpStatusCode.OK
                ? """{"success":true,"data":{}}"""
                : """{"success":false,"message":"unauthorized"}""";
            return Task.FromResult(new HttpResponseMessage(kod)
            {
                Content = new StringContent(govde, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SayanOturum : ISessionProvider
    {
        public int Alim, Atma;
        public Task<SessionToken> AcquireAsync(CancellationToken ct = default)
        { Alim++; return Task.FromResult(new SessionToken("jwt", 900, 0)); }
        public void Invalidate() => Atma++;
    }

    [Fact]
    public async Task GET_401_alinca_TEK_yenileme_TEK_tekrar()
    {
        // ← ÇİVİ: jeton artık önbellekli; uç onu bizden önce geçersiz sayabilir. Yenilemeden
        // pes etmek satışı boşuna düşürürdü.
        var h = new YanitIsleyici();
        h.Sira.Enqueue(HttpStatusCode.Unauthorized);
        h.Sira.Enqueue(HttpStatusCode.OK);
        var oturum = new SayanOturum();
        var c = new HttpPaymentDetailClient(new HttpClient(h), oturum, new Uri("https://ornek.test/"));

        await c.FetchAsync("pay_1");

        Assert.Equal(2, h.Istek);        // ← ÇİVİ: bir 401 + bir tekrar
        Assert.Equal(1, oturum.Atma);    // ← ÇİVİ: TEK yenileme
        Assert.Equal(2, oturum.Alim);    // her denemede jeton istendi (ikincisi yenilenmiş)
    }

    [Fact]
    public async Task GET_surekli_401_ise_FIRTINA_YOK()
    {
        // ← ÇİVİ: tekrar SINIRSIZ olsaydı, uç jetonu kabul etmediğinde her satış sonsuz döngüye
        // girer ve uca istek yağdırırdı. İkinci 401 olduğu gibi rette biter.
        var h = new YanitIsleyici();
        h.Sira.Enqueue(HttpStatusCode.Unauthorized);
        h.Sira.Enqueue(HttpStatusCode.Unauthorized);
        h.Sira.Enqueue(HttpStatusCode.Unauthorized);
        var oturum = new SayanOturum();
        var c = new HttpPaymentDetailClient(new HttpClient(h), oturum, new Uri("https://ornek.test/"));

        var r = await c.FetchAsync("pay_1");

        Assert.IsType<PaymentDetailResult.Rejected>(r);
        Assert.Equal(2, h.Istek);        // ← ÇİVİ: TAM 2, üç değil
        Assert.Equal(1, oturum.Atma);    // ← ÇİVİ: TAM 1 yenileme
    }

    [Fact]
    public void Jeton_LOGA_giremez_cunku_gunlukcu_YOK()
    {
        // ← ÇİVİ (yapısal): jetonun loglanmaması bir dikkat meselesi değil — HTTP istemcilerinin
        // günlükçü bağımlılığı YOK, dolayısıyla `Authorization` başlığı ya da jeton hiçbir log
        // satırına giremez. Birine günlükçü eklenirse bu test kırılır ve karar bilinçli olur.
        foreach (var tip in new[] { typeof(HttpPaymentDetailClient), typeof(HttpResultNotifier), typeof(HttpSessionProvider) })
        {
            var parametreler = tip.GetConstructors()[0].GetParameters().Select(x => x.ParameterType.Name);
            Assert.DoesNotContain(parametreler, ad => ad.Contains("Log", StringComparison.OrdinalIgnoreCase));
        }
    }
}
