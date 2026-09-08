using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W45 — reddedilen isteğin kaynağı loglanıyor.
///
/// <para><b>Neden (ölçüm 2026-09-08 18:57:53):</b> `/nexo`'ya geçerli JSON ama nexo zarfı olmayan
/// bir POST geldi, reddedildi ve "kim gönderdi" sorusu CEVAPSIZ kaldı — reddetme satırında kaynağa
/// dair hiçbir alan yoktu.</para>
/// </summary>
public class IstekKaynagiTests
{
    [Fact]
    public void Uc_alan_da_satirda()
    {
        var s = IstekKaynagi.Tanimla("127.0.0.1:51234", "Electron/29.0", 412);

        Assert.Contains("127.0.0.1:51234", s);
        Assert.Contains("Electron/29.0", s);
        Assert.Contains("412", s);
    }

    [Fact]
    public void Baslik_YOKSA_ile_BOS_ayri_yazilir()
    {
        // ← ÇİVİ: "UA göndermiyor" ile "UA'yı boş gönderiyor" ayrı olgular. Birleştirmek teşhiste
        // aranan ayrımı yok ederdi.
        Assert.Contains("ua=(yok)", IstekKaynagi.Tanimla("127.0.0.1:1", null, 0));
        Assert.Contains("ua=(boş)", IstekKaynagi.Tanimla("127.0.0.1:1", "", 0));
    }

    [Fact]
    public void Content_Length_YOKSA_sifirdan_ayri()
    {
        // ← ÇİVİ: `HttpListener` başlık yoksa -1 verir. -1'i 0 gibi yazmak "gövde boş geldi" derdi;
        // oysa gerçek "uzunluğu bilmiyoruz". Boş gövde ile bilinmeyen uzunluk AYRI olgular.
        Assert.Contains("uzunluk=(yok)", IstekKaynagi.Tanimla("127.0.0.1:1", "x", -1));
        Assert.Contains("uzunluk=0", IstekKaynagi.Tanimla("127.0.0.1:1", "x", 0));
    }

    [Fact]
    public void Uzak_uc_YOKSA_uydurulmaz()
    {
        Assert.Contains("uzakUç=(yok)", IstekKaynagi.Tanimla(null, "x", 1));
    }

    [Fact]
    public void Govde_PARAMETRESI_YOK_dolayisiyla_loglanamaz()
    {
        // ← ÇİVİ (yapısal): gövde kart/kişisel veri taşıyabilir ve REDDEDİLMİŞ bir gövde de
        // taşıyabilir — biçim bozukluğu içeriği zararsız yapmaz. Bu yüzden koruma "gövdeyi
        // yazmamaya dikkat et" değil, "gövdeyi ALAMAMAK": metodun gövde parametresi yok.
        // Parametre eklenirse bu test derlemede değil, imza kontrolünde kırılır.
        var m = typeof(IstekKaynagi).GetMethod(nameof(IstekKaynagi.Tanimla))!;
        Assert.Equal(new[] { "uzakUc", "userAgent", "contentLength" },
            m.GetParameters().Select(p => p.Name).ToArray());
    }
}
