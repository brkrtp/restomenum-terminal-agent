using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W51 — oturum alımı ÖLÇÜLÜYOR, davranış değişmiyor.
///
/// <para><b>Neden (ölçüm 2026-09-08 22:45:08):</b> bir satış uçtan uca 20,2 sn sürdü, cihaz tarafı
/// 7,8 sn'ydi; aradaki 10,9 sn için günlükte tek satır yoktu.</para>
/// </summary>
public class OlcenSessionProviderTests
{
    private sealed class SahteOturum : ISessionProvider
    {
        public int Cagri;
        public Task<SessionToken> AcquireAsync(CancellationToken ct = default)
        {
            Cagri++;
            return Task.FromResult(new SessionToken("jwt-" + Cagri, 900, 0));
        }
    }

    [Fact]
    public async Task Jetonu_DEGISTIRMEDEN_gecirir()
    {
        // ← ÇİVİ: ölçüm sarmalayıcısı yalnız ölçer. Jetonu ya da hata davranışını değiştirirse
        // ölçmek için ölçtüğümüz şeyi bozmuş oluruz.
        var ic = new SahteOturum();
        var o = new OlcenSessionProvider(ic);

        var t = await o.AcquireAsync();

        Assert.Equal("jwt-1", t.Token);
        Assert.Equal(1, ic.Cagri);
    }

    [Fact]
    public async Task Ayni_satista_cagrilar_BIRIKIR()
    {
        // Bir satışta oturum birden çok kez alınabilir (tutar çekimi + bildirim). Tek çağrıyı
        // raporlamak "toplam ne kadar bekledik" sorusunu yanlış cevaplardı.
        OlcenSessionProvider.Sifirla();
        var o = new OlcenSessionProvider(new SahteOturum());

        await o.AcquireAsync();
        await o.AcquireAsync();

        Assert.Equal(2, OlcenSessionProvider.Son!.Cagri);
    }

    [Fact]
    public async Task Sifirla_ONCEKI_satisin_sayilarini_TASIMAZ()
    {
        // ← ÇİVİ: sıfırlanmazsa bir sonraki satışın satırında ÖNCEKİ satışın süresi görünür ve
        // teşhis tamamen yanlış yere bakar — ölçüm aracının kendisi yalan söylemiş olur.
        OlcenSessionProvider.Sifirla();
        var o = new OlcenSessionProvider(new SahteOturum());
        await o.AcquireAsync();
        Assert.NotNull(OlcenSessionProvider.Son);

        OlcenSessionProvider.Sifirla();

        Assert.Null(OlcenSessionProvider.Son);   // kutu yeni, sayaç 0 → "ölçüm yok"
    }

    [Fact]
    public async Task HTTP_sayaci_OKUNAMAZSA_eksi_bir_sifir_DEGIL()
    {
        // ← ÇİVİ: iç uygulama sayaç vermiyorsa (testteki sahte gibi) "0 istek gitti" demek YANLIŞ
        // olurdu — hiç istek gitmemiş gibi okunur. "Ölçülmedi" ile "sıfır" ayrı olgular.
        OlcenSessionProvider.Sifirla();
        var o = new OlcenSessionProvider(new SahteOturum());

        await o.AcquireAsync();

        Assert.Equal(-1, OlcenSessionProvider.Son!.HttpDeneme);
    }
}
