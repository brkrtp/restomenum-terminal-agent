using Newtonsoft.Json;
using Restomenum.Agent.Gmp;
using Restomenum.Agent.Gmp.Interop;
using Xunit;

namespace Restomenum.Agent.Gmp.Tests;

/// <summary>
/// <b>W142 — CİHAZIN GÖNDERDİĞİ JSON'A KARŞI eşleme sınavı.</b>
///
/// <para><b>Neden ayrı bir proje:</b> interop <c>net8.0-windows</c> hedefliyor, Core testleri
/// <c>net8.0</c>. Bu yüzden bu köprü BUGÜNE KADAR HİÇ TEST EDİLMEDİ — ve tam orada bir kusur
/// vardı: cihaz mali sayaçları her Echo'da gönderiyordu, <c>ST_ECHO</c>'da karşılıkları
/// olmadığı için Newtonsoft onları sessizce düşürüyordu.</para>
///
/// <para>⚠️ Buradaki JSON <b>uydurma değil</b>: cihazın DLL izinden birebir alındı
/// (2026-09-11 00:11:26). Sahte bir gövdeye karşı test yazmak, tam da kaçırdığımız kusuru
/// yeniden üretirdi.</para>
/// </summary>
public class EchoEslemeTests
{
    /// <summary>Cihazın 2026-09-11 00:11:26'da gönderdiği yanıt — DLL izinden birebir.</summary>
    private const string CihazinGonderdigi =
        "{\"retcode\":0,\"status\":29,\"kvc\":\"FKekTTOnY3U=\",\"ecrMode\":1,\"mtuSize\":8192,"
        + "\"szEcrVersion\":\"2001010000\",\"szEcrNewVersion\":\"215132107T\","
        + "\"EkuNo\":1,\"ZNo\":91,\"FisNo\":2,\"Handle\":0,"
        + "\"UniqueID\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}";

    [Fact]
    public void MALI_SAYACLAR_cihazin_GERCEK_yanitindan_okunur()
    {
        // ← ÇİVİ: alan adları cihazın gönderdiğiyle BİREBİR tutmazsa Newtonsoft sessizce
        // düşürür. Bu okunarak doğrulanamaz — yalnız gerçek gövdeye karşı çalıştırarak.
        var echo = JsonConvert.DeserializeObject<ST_ECHO>(CihazinGonderdigi)!;

        Assert.Equal(91u, echo.ZNo);
        Assert.Equal(2u, echo.FisNo);
        Assert.Equal(1u, echo.EkuNo);
        Assert.Equal(0ul, echo.Handle);
        Assert.Equal(29u, echo.status);   // 0x1D — sağlıklı cihaz
    }

    [Fact]
    public void CEVIR_sayaclari_KARISTIRMADAN_tasir()
    {
        // ← ÇİVİ: ZNo/FisNo/EkuNo aynı tipte ve yan yana. Sıraları karışırsa derleyici
        // uyarmaz, testler yeşil kalır ve mali denetimde yanlış sayı raporlanır.
        var echo = JsonConvert.DeserializeObject<ST_ECHO>(CihazinGonderdigi)!;

        var d = GmpWrapper.Cevir(echo);

        Assert.Equal(91u, d.ZNo);
        Assert.Equal(2u, d.FisNo);
        Assert.Equal(1u, d.EkuNo);
        Assert.Equal(0ul, d.Handle);
        Assert.Equal(29u, d.Status);
    }

    [Fact]
    public void ALAN_ADI_DEGISIRSE_SESSIZCE_DUSER_bunu_belgeliyoruz()
    {
        // ⚠️ Bu test bir KUSURU değil, BİR TEHLİKEYİ belgeliyor: yanlış adlı alan hata
        // vermez, SIFIR olur. `szEcrVersion` şu an tam olarak bu durumda — cihaz onu
        // gönderiyor, sınıfta `ecrVersion` yazıyor ve değer düşüyor.
        //
        // Sayaçların haftalarca görünmemesinin sebebi buydu; aynı sessizliğin bir daha
        // fark edilmeden geçmemesi için kayda geçiyor.
        var yanlisAdla = JsonConvert.DeserializeObject<ST_ECHO>(
            "{\"retcode\":0,\"status\":29,\"FiskNo\":2,\"ZedNo\":91}")!;

        Assert.Equal(0u, yanlisAdla.FisNo);   // hata YOK, sessizce SIFIR
        Assert.Equal(0u, yanlisAdla.ZNo);
    }
}
