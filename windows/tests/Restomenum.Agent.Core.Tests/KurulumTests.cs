using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>W149 — kurulum sayfası.</b> Sayfa, ayrıştırma ve JSON saf; cihaza dokunan taraf Host'ta.
/// Buradaki çiviler: yanlış girdi doğru sebeple reddediliyor mu, sayfa durumu doğru anlatıyor mu,
/// ve gövde hiçbir yerde kodu geri yansıtmıyor mu.
/// </summary>
public class KurulumTests
{
    // ── AYRIŞTIRMA: sebep AYRIŞIR (W129 dersi) ─────────────────────────────────

    [Fact]
    public void Kod_bos_yok_ve_gecersiz_JSON_ayri_sebeplerle_reddedilir()
    {
        Assert.Null(KurulumIstek.Kod("{}", out var yok));
        Assert.Contains("yok", yok);

        Assert.Null(KurulumIstek.Kod("{\"code\":\"   \"}", out var bos));
        Assert.Contains("boş", bos);

        Assert.Null(KurulumIstek.Kod("bu json degil", out var bozuk));
        Assert.Contains("JSON", bozuk);

        Assert.Null(KurulumIstek.Kod("{\"code\":\"" + new string('x', 300) + "\"}", out var uzun));
        Assert.Contains("uzun", uzun);
    }

    [Fact]
    public void Kod_kirpilir_ve_aynen_doner()
    {
        var kod = KurulumIstek.Kod("{\"code\":\"  8F3K-2QZ7 \"}", out var neden);
        Assert.Equal("8F3K-2QZ7", kod);
        Assert.Null(neden);
    }

    [Theory]
    [InlineData("{\"ip\":\"192.168.1.189\"}", "192.168.1.189", 7500)]           // port varsayılan
    [InlineData("{\"ip\":\"10.0.0.5\",\"port\":7501}", "10.0.0.5", 7501)]
    public void Terminal_adresi_ayristirilir(string govde, string ip, int port)
    {
        var t = KurulumIstek.Terminal(govde, out var neden);
        Assert.NotNull(t);
        Assert.Equal((ip, port), t!.Value);
        Assert.Null(neden);
    }

    [Theory]
    [InlineData("{\"ip\":\"terminal\"}", "IPv4")]           // ad değil, adres
    [InlineData("{\"ip\":\"::1\"}", "IPv4")]                // IPv6 değil
    [InlineData("{\"ip\":\"1.2.3.4\",\"port\":70000}", "aralık")]
    [InlineData("{}", "yok")]
    public void Terminal_adresi_yanlissa_sebep_soyler(string govde, string beklenenParca)
    {
        Assert.Null(KurulumIstek.Terminal(govde, out var neden));
        Assert.Contains(beklenenParca, neden);
    }

    // ── SAYFA ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Sayfa_kayitsiz_ve_eslesmemis_durumu_ADIMLARIYLA_anlatir()
    {
        var html = KurulumSayfasi.Html(new KurulumDurumu(Kayitli: false, Esli: false, TerminalIp: "192.168.1.189"));

        Assert.Contains("kayıtlı değil", html);
        Assert.Contains("eşleşmemiş", html);
        Assert.Contains("id=\"kod\"", html);                 // kod alanı VAR
        Assert.Contains("value=\"192.168.1.189\"", html);    // mevcut IP seçili seçenek olarak gelir
        Assert.Contains("<select id=\"ip\"", html);          // W152: metin kutusu değil, seçim + tarama
        Assert.Contains("Tara", html);                       // W152: ağ tarama düğmesi
        Assert.Contains("GMP Eşleştirme", html);             // kullanıcıya terminaldeki adım
    }

    [Fact]
    public void Sayfa_kayitliysa_kod_alani_GOSTERMEZ()
    {
        // Kayıtlı cihaza yeniden kod girdirmek, kullanıcıyı gereksiz bir eklenti turuna yollar.
        var html = KurulumSayfasi.Html(new KurulumDurumu(Kayitli: true, Esli: true,
            CihazModeli: "INGENICO IDE281", CihazSeri: "2B25035325", ZNo: 93, FisNo: 7));

        Assert.DoesNotContain("id=\"kod\"", html);
        Assert.Contains("Platforma kayıtlı", html);
        Assert.Contains("INGENICO IDE281", html);
        Assert.Contains("2B25035325", html);
        Assert.Contains("Z raporu: 93", html);
        Assert.Contains("Sıradaki fiş: 7", html);   // FisNo SONRAKİ numaradır — sayfa öyle der
    }

    [Fact]
    public void Sayfa_IP_icin_selectbox_ve_elle_gir_secenegi_sunar()
    {
        // W152: kullanıcı IP'yi ya listeden seçer (tarama) ya elle girer — ikisi de olmalı.
        var html = KurulumSayfasi.Html(new KurulumDurumu(true, false, TerminalIp: "192.168.1.189"));
        Assert.Contains("<select id=\"ip\"", html);
        Assert.Contains("__manuel__", html);                 // "elle gir" seçeneği
        Assert.Contains("id=\"ipManuel\"", html);            // elle giriş kutusu
        Assert.Contains("/setup/scan", html);                // tarama ucu JS'te
    }

    [Fact]
    public void Sayfa_IP_yoksa_placeholder_secenek_gosterir_deger_uydurmaz()
    {
        var html = KurulumSayfasi.Html(new KurulumDurumu(true, false, TerminalIp: null));
        Assert.DoesNotContain("value=\"192.168.1.189\"", html);
        Assert.Contains("tarayın ya da elle girin", html);   // boş durum açık anlatılır
    }

    [Fact]
    public void Tarama_JSON_u_adaylari_ve_agi_tasir()
    {
        var j = KurulumIstek.JsonTarama(
            new[] { new TerminalAday("192.168.1.189", 7500), new TerminalAday("192.168.1.50", 7500) },
            "192.168.1.0/24");
        Assert.Contains("\"count\":2", j);
        Assert.Contains("\"subnet\":\"192.168.1.0/24\"", j);
        Assert.Contains("\"ip\":\"192.168.1.189\"", j);
        Assert.Contains("\"port\":7500", j);
    }

    [Fact]
    public void Tarama_bos_sonuc_uydurmaz()
    {
        var j = KurulumIstek.JsonTarama(System.Array.Empty<TerminalAday>(), null);
        Assert.Contains("\"count\":0", j);
        Assert.Contains("\"subnet\":null", j);
        Assert.Contains("\"found\":[]", j);
    }

    [Fact]
    public void Sayfa_dis_kaynak_kullanmaz()
    {
        // Makine internetsiz olabilir; sayfa her koşulda açılmalı.
        var html = KurulumSayfasi.Html(new KurulumDurumu(false, false));
        Assert.DoesNotContain("http://", html.Replace("http://127.0.0.1", ""));
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("src=\"http", html);
    }

    [Fact]
    public void Sayfa_kullanici_metnini_HTML_olarak_kacirir()
    {
        // Not/seri gibi alanlar cihazdan/dosyadan geliyor; ham basmak XSS değil ama bozuk sayfa demek.
        var html = KurulumSayfasi.Html(new KurulumDurumu(true, true, Not: "<b>hata</b>", CihazSeri: "A<B"));
        Assert.Contains("&lt;b&gt;hata&lt;/b&gt;", html);
        Assert.Contains("A&lt;B", html);
        Assert.DoesNotContain("<b>hata</b>", html);
    }

    // ── KALDIRMA ────────────────────────────────────────────────────────────────

    [Fact]
    public void Kaldirma_yalniz_acik_KALDIR_onayiyla_kabul_edilir()
    {
        // Yanlışlıkla basılan bir düğme cihazı platformdan koparmamalı: onay sözcüğü şart.
        Assert.True(KurulumIstek.KaldirmaOnayi("{\"confirm\":\"KALDIR\"}", out var ok));
        Assert.Null(ok);
        Assert.True(KurulumIstek.KaldirmaOnayi("{\"confirm\":\"  kaldır \"}", out _));   // büyük/küçük, Türkçe ı
        Assert.True(KurulumIstek.KaldirmaOnayi("{\"confirm\":\"kaldir\"}", out _));      // Türkçe klavyesiz yazım
        Assert.True(KurulumIstek.KaldirmaOnayi("{\"confirm\":\"KALDİR\"}", out _));

        Assert.False(KurulumIstek.KaldirmaOnayi("{}", out var yok));
        Assert.Contains("yok", yok);
        Assert.False(KurulumIstek.KaldirmaOnayi("{\"confirm\":\"evet\"}", out var yanlis));
        Assert.Contains("KALDIR", yanlis);
        Assert.False(KurulumIstek.KaldirmaOnayi("json degil", out var bozuk));
        Assert.Contains("JSON", bozuk);
    }

    [Fact]
    public void Sayfa_kurulu_cihazda_kaldirma_bolumunu_onay_alaniyla_gosterir()
    {
        var html = KurulumSayfasi.Html(new KurulumDurumu(Kayitli: true, Esli: true, EklentiAdresi: "x.workers.dev"));
        Assert.Contains("Cihazı kaldır", html);
        Assert.Contains("id=\"kaldirOnay\"", html);
        Assert.Contains("/setup/remove", html);
    }

    [Fact]
    public void Sayfa_yalniz_eklenti_bagliyken_de_kaldirma_bolumunu_gosterir()
    {
        // Kaydı olmayan ama eklenti dizesi kalmış cihaz da temizlenebilmeli (kart koşulu VEYA).
        var html = KurulumSayfasi.Html(new KurulumDurumu(Kayitli: false, Esli: false, EklentiAdresi: "x.workers.dev"));
        Assert.Contains("id=\"kaldirOnay\"", html);
    }

    [Fact]
    public void Kaldirma_dugmesi_onayi_sunucunun_bekledigi_ALANLA_gonderir()
    {
        // Alan adı JS'te değişirse düğme hiç çalışmaz ama başka hiçbir test bunu görmezdi (Fable C).
        var html = KurulumSayfasi.Html(new KurulumDurumu(true, true, EklentiAdresi: "x"));
        Assert.Contains("post('/setup/remove',{confirm:document.getElementById('kaldirOnay').value})", html);
    }

    [Fact]
    public void Sayfa_istek_anahtarini_gomer_ve_her_POST_ta_gonderir()
    {
        var html = KurulumSayfasi.Html(new KurulumDurumu(true, true), anahtar: "abc123");
        Assert.Contains("const ANAHTAR='abc123'", html);
        Assert.Contains("'X-Kurulum-Anahtari':ANAHTAR", html);
        Assert.Contains("'Content-Type':'application/json'", html);
    }

    [Fact]
    public void Govde_nesne_degilse_sebep_soylenir_istisna_firlatilmaz()
    {
        Assert.False(KurulumIstek.KaldirmaOnayi("[1,2]", out var neden));
        Assert.Contains("nesne", neden);
        Assert.Null(KurulumIstek.Kod("\"metin\"", out var n2));
        Assert.Contains("nesne", n2);
    }

    [Fact]
    public void Sayfa_hic_kurulmamis_cihazda_kaldirma_bolumunu_GOSTERMEZ()
    {
        // Kaldırılacak kayıt da eklenti de yokken düğme yalnız kafa karıştırır.
        var html = KurulumSayfasi.Html(new KurulumDurumu(Kayitli: false, Esli: false));
        Assert.DoesNotContain("id=\"kaldirOnay\"", html);
    }

    // ── JSON ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sonuc_JSON_u_yeniden_baslatma_bayragini_tasir()
    {
        var j = KurulumIstek.Json(KurulumSonucu.Tamam("Kaydedildi.", yenidenBaslat: true));
        Assert.Contains("\"ok\":true", j);
        Assert.Contains("\"restartRequired\":true", j);
        Assert.Contains("Kaydedildi.", j);

        var h = KurulumIstek.Json(KurulumSonucu.Hata("Kod yanlış."));
        Assert.Contains("\"ok\":false", h);
        Assert.Contains("\"restartRequired\":false", h);
    }

    [Fact]
    public void Durum_JSON_u_bilinmeyeni_null_birakir_uydurmaz()
    {
        // Sayaç okunamadıysa 0 DEĞİL null: 0 gerçek bir sayaç iddiası olurdu.
        var j = KurulumIstek.Json(new KurulumDurumu(true, false));
        Assert.Contains("\"zNo\":null", j);
        Assert.Contains("\"fisNo\":null", j);
        Assert.Contains("\"paperWarning\":null", j);
    }
}
