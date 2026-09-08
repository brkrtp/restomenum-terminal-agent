using System.Net;
using System.Text;
using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W56 — cihaz bölüm tablosu, yoklama turunda cihaz BOŞTAYKEN okunur ve <b>yalnız değiştiyse</b>
/// bildirilir.
///
/// <para><b>Neden (sahada ölçüldü, 2026-09-09 00:51):</b> tabloyu cihazdan okuyan tek yol eşleşmeydi.
/// Kullanıcı terminalde "DIGER" bölümünün KDV'sini %0'dan %10'a çekti; ajan biri yeniden eşleştirene
/// kadar eski oranı DOĞRU sandı. Mali fiş yine doğru basılır (oranı cihaz uygular) ama K-30 KDV
/// çelişki raporu yanlış olur — hayalet çelişki bildirir ya da gerçeğini kaçırır.</para>
/// </summary>
public class DeviceDepartmentRefresherTests
{
    // ── Sahteler ────────────────────────────────────────────────────────────────

    /// <summary>Oturum + departman POST'unu karşılayan uç. Gövdeyi SAKLAR: neyin gittiğini sınarız.</summary>
    private sealed class SahteUc : HttpMessageHandler
    {
        public readonly List<string> Yollar = new();
        public string? SonDepartmanGovdesi;
        public HttpStatusCode DepartmanYaniti = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var yol = r.RequestUri!.AbsolutePath;
            Yollar.Add(yol);
            if (yol.EndsWith("session", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"success":true,"data":{"accessToken":"jwt","expiresIn":900}}""",
                        Encoding.UTF8, "application/json"),
                };

            SonDepartmanGovdesi = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(DepartmanYaniti)
            {
                Content = new StringContent("""{"success":true,"data":{}}""", Encoding.UTF8, "application/json"),
            };
        }

        public int DepartmanPostSayisi => Yollar.Count(y => y.EndsWith("departments", StringComparison.Ordinal));
    }

    private sealed class SahteKapi : IDeviceIdleGate
    {
        public bool Mesgul;
        public int Alim;
        public int Birakim;
        public IDisposable? TryAcquire()
        {
            if (Mesgul) return null;
            Alim++;
            return new Birak(this);
        }
        private sealed class Birak(SahteKapi k) : IDisposable
        {
            public void Dispose() => k.Birakim++;
        }
    }

    private sealed class SahteDepo : IDeviceMappingStore
    {
        public DeviceMapping? Current { get; set; }
        public int? CurrentVersion => Current?.Version;
        public bool IsConfigured => Current is { Version: > 0 };
        public void Update(DeviceMapping mapping, string rawJson) => Current = mapping;
    }

    // ── Kurulum ─────────────────────────────────────────────────────────────────

    private static string CihazDept(params (string Ad, int VergiIndeksi)[] d)
        => JsonSerializer.Serialize(d.Select(x => new { szDeptName = x.Ad, u8TaxIndex = x.VergiIndeksi }));

    private static string CihazOran(params int[] oranlar)
        => JsonSerializer.Serialize(oranlar.Select(o => new { taxRate = o }));

    private static DeviceMapping Esleme(params DeviceDepartment[] d)
        => new(70, d, Array.Empty<MappingEntry>(), new Dictionary<string, int>());

    private static (DeviceDepartmentRefresher, GmpTerminalTransportTests.FakeGmp, SahteUc, SahteKapi, SahteDepo)
        Kur(DeviceMapping? platformda = null)
    {
        var gmp = new GmpTerminalTransportTests.FakeGmp();
        var uc = new SahteUc();
        var istemci = new DeviceConfigClient(new HttpClient(uc), new Uri("https://ornek.test/"), "sir");
        var depo = new SahteDepo { Current = platformda };
        var kapi = new SahteKapi();
        var kimlik = new DeviceIdentityHolder();
        kimlik.Kaydet(new GmpDeviceInfo("INGENICO", "IDE281", "2B25", "1"), """[{"index":2}]""");
        return (new DeviceDepartmentRefresher(gmp, istemci, depo, kapi, kimlik), gmp, uc, kapi, depo);
    }

    // ── Testler ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Oran_DEGISINCE_bildirilir()
    {
        // ← ÇİVİ: sahada yaşanan olayın ta kendisi. Cihazda "DIGER" %0'dan %10'a çekildi; platform
        // hâlâ %0 taşıyor. Bu test kırmızıya dönerse ajan yine eski oranı doğru sanar.
        var (t, gmp, uc, _, _) = Kur(Esleme(new DeviceDepartment(0, "DIGER", 0)));
        gmp.DepartmentsJson = CihazDept(("DIGER", 0));
        gmp.TaxRatesJson = CihazOran(1000);

        var sonuc = await t.RefreshAsync();

        Assert.Equal(DepartmentRefreshOutcome.Reported, sonuc);
        Assert.Equal(1, uc.DepartmanPostSayisi);
        Assert.Contains("\"taxRateBasisPoints\":1000", uc.SonDepartmanGovdesi);
    }

    [Fact]
    public async Task Ayni_tabloda_BILDIRIM_YOK()
    {
        // ← ÇİVİ: her turda bildirmek eşleme sürümünü boşuna artırır ve "sürüm değişti" sinyalini
        // anlamsızlaştırır — 30 dakikada bir sürüm atlayan bir sistemde gerçek değişiklik kaybolur.
        var (t, gmp, uc, _, _) = Kur(Esleme(new DeviceDepartment(0, "YIYECEK", 2000)));
        gmp.DepartmentsJson = CihazDept(("YIYECEK", 0));
        gmp.TaxRatesJson = CihazOran(2000);

        var sonuc = await t.RefreshAsync();

        Assert.Equal(DepartmentRefreshOutcome.Unchanged, sonuc);
        Assert.Equal(0, uc.DepartmanPostSayisi);
    }

    [Fact]
    public async Task Cihaz_MESGULSE_hic_okunmaz()
    {
        // ← ÇİVİ: bakım işi satışın ortasında `GetDepartments` çağırırsa cihaz iki iş arasında
        // paylaşılmış olur (değişmez #4 ihlali). Kilit alınamıyorsa tur ATLANIR — beklemez bile.
        var (t, gmp, uc, kapi, _) = Kur();
        kapi.Mesgul = true;
        gmp.DepartmentsJson = CihazDept(("DIGER", 0));
        gmp.TaxRatesJson = CihazOran(1000);

        var sonuc = await t.RefreshAsync();

        Assert.Equal(DepartmentRefreshOutcome.DeviceBusy, sonuc);
        Assert.DoesNotContain("GetDepartments", gmp.Calls);
        Assert.Equal(0, uc.DepartmanPostSayisi);
    }

    [Fact]
    public async Task Okuma_BASARISIZSA_bildirim_yok_eski_tablo_kalir()
    {
        // ← ÇİVİ: "okunamadı" ≠ "boş" ≠ "dolu". Okunamayan bir tabloyu bildirmek, platformdaki
        // GERÇEK tabloyu okuyamadığımız bir şeyle değiştirmek olurdu.
        var (t, gmp, uc, _, depo) = Kur(Esleme(new DeviceDepartment(0, "YIYECEK", 2000)));
        gmp.Codes["GetDepartments"] = 1234;

        var sonuc = await t.RefreshAsync();

        Assert.Equal(DepartmentRefreshOutcome.ReadFailed, sonuc);
        Assert.Equal(0, uc.DepartmanPostSayisi);
        Assert.Equal(2000, depo.Current!.Departments[0].TaxRateBasisPoints);   // eski tablo yerinde
    }

    [Fact]
    public async Task ORAN_tablosu_okunamazsa_da_bildirilmez()
    {
        // ← ÇİVİ: oran tablosu okunamayınca builder TÜM oranları null yapar. O listeyi bildirmek
        // platformdaki gerçek oranları silip HER ürün eşlemesini engellerdi — sessiz bir kilitlenme.
        var (t, gmp, uc, _, _) = Kur(Esleme(new DeviceDepartment(0, "YIYECEK", 2000)));
        gmp.DepartmentsJson = CihazDept(("YIYECEK", 0));
        gmp.Codes["GetTaxRates"] = 1234;

        var sonuc = await t.RefreshAsync();

        Assert.Equal(DepartmentRefreshOutcome.ReadFailed, sonuc);
        Assert.Equal(0, uc.DepartmanPostSayisi);
    }

    [Fact]
    public async Task BOS_okunursa_bildirilmez()
    {
        // ← ÇİVİ: bildirim tabloyu DEĞİŞTİRİR, birleştirmez. Boş liste platformdaki tabloyu kırpar
        // ve eşleme ekranı çalışmaz hâle gelir. Eşleşme yolunda zaten var olan kural, burada da.
        var (t, gmp, uc, _, _) = Kur(Esleme(new DeviceDepartment(0, "YIYECEK", 2000)));
        gmp.DepartmentsJson = "[]";
        gmp.TaxRatesJson = "[]";

        var sonuc = await t.RefreshAsync();

        Assert.Equal(DepartmentRefreshOutcome.ReadEmpty, sonuc);
        Assert.Equal(0, uc.DepartmanPostSayisi);
    }

    [Fact]
    public async Task NULL_oran_ile_SIFIR_oran_AYRI_sayilir()
    {
        // ← ÇİVİ: `null` = "cihazın vergi indeksi tabloda yok, oran BİLİNMİYOR"; `0` = "%0". Bu ikisini
        // eşit sayan bir karşılaştırma, oran tablosu bozulduğunda değişikliği görmezdi.
        var (t, gmp, uc, _, _) = Kur(Esleme(new DeviceDepartment(0, "DIGER", null)));
        gmp.DepartmentsJson = CihazDept(("DIGER", 0));
        gmp.TaxRatesJson = CihazOran(0);

        var sonuc = await t.RefreshAsync();

        Assert.Equal(DepartmentRefreshOutcome.Reported, sonuc);
        Assert.Contains("\"taxRateBasisPoints\":0", uc.SonDepartmanGovdesi);
    }

    [Fact]
    public async Task KIMLIK_ve_UYGULAMALAR_yeniden_gonderilir()
    {
        // ← ÇİVİ: gövde `null` alanları AYNEN serialize ediyor. `deviceInfo`/`paymentApplications`
        // null gitseydi platform "okuyamadım"ı "yok" diye anlayıp panelde çalışan bankaları silebilirdi.
        // Eşleşmede okunan son değer saklanır ve burada yeniden gönderilir.
        var (t, gmp, uc, _, _) = Kur(Esleme(new DeviceDepartment(0, "DIGER", 0)));
        gmp.DepartmentsJson = CihazDept(("DIGER", 0));
        gmp.TaxRatesJson = CihazOran(1000);

        await t.RefreshAsync();

        Assert.Contains("\"serial\":\"2B25\"", uc.SonDepartmanGovdesi);
        Assert.Contains("\"paymentApplications\":[{\"index\":2}]", uc.SonDepartmanGovdesi);
    }

    [Fact]
    public async Task Bildirim_REDDEDILIRSE_ReportFailed()
    {
        // Bir sonraki tur yeniden dener; hata yutulup "tamam" denmez.
        var (t, gmp, uc, _, _) = Kur(Esleme(new DeviceDepartment(0, "DIGER", 0)));
        gmp.DepartmentsJson = CihazDept(("DIGER", 0));
        gmp.TaxRatesJson = CihazOran(1000);
        uc.DepartmanYaniti = HttpStatusCode.BadRequest;

        Assert.Equal(DepartmentRefreshOutcome.ReportFailed, await t.RefreshAsync());
    }

    [Fact]
    public async Task Kilit_HER_YOLDA_birakilir()
    {
        // ← ÇİVİ: bir kez sızan kilit cihazı KALICI olarak meşgul gösterir ve o andan sonra hiçbir
        // satış çalışmaz. Okuma hatası da dahil her çıkışta bırakılmalı.
        var (t, gmp, _, kapi, _) = Kur();
        gmp.Codes["GetDepartments"] = 1234;
        await t.RefreshAsync();

        gmp.Codes.Remove("GetDepartments");
        gmp.DepartmentsJson = CihazDept(("DIGER", 0));
        gmp.TaxRatesJson = CihazOran(1000);
        await t.RefreshAsync();

        Assert.Equal(2, kapi.Alim);
        Assert.Equal(2, kapi.Birakim);
    }

    [Fact]
    public void Satis_kilidi_CIHAZ_kilidinin_ta_KENDISI()
    {
        // ← ÇİVİ (yapısal): `LocalSaleHandler` `IDeviceIdleGate`'i KENDİSİ uyguluyor. Ayrı bir kilit
        // sınıfı yazılsaydı "cihaz başına tek işlem" iki yerde tanımlanır ve biri diğerini görmezdi.
        Assert.True(typeof(IDeviceIdleGate).IsAssignableFrom(typeof(LocalSaleHandler)));
    }
}
