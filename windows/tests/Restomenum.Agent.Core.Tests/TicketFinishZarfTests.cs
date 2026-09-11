using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>W110 — ELLE FİŞ TAMAMLAMA ZARFI (<c>TicketFinish</c>).</b>
///
/// <para>Otomatik süpürme sekiz denemeden sonra susup insanı bekliyor (W108) — kâğıt gerçekten
/// bittiyse cihazı süresiz meşgul etmek kasiyeri de kilitlerdi. <b>Kâğıdı değiştiren kişi doğru
/// anı bilir, süpürme bilmez.</b> Bu zarf ona tetiği veriyor; bugüne kadar tek yol ajanı durdurup
/// <c>--finish-ticket</c> çalıştırmaktı.</para>
/// </summary>
public class TicketFinishZarfTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"tf_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;
    private readonly Outbox _outbox;
    private readonly ClockOffset _clock = new();
    private readonly List<(string Mesaj, object? Veri)> _loglar = new();

    public TicketFinishZarfTests()
    {
        _store = CommandStore.Open(_db);
        _outbox = Outbox.Open(_db);
        _clock.Sync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    private sealed class SahteTamamlayici : ITerminalTransport, IStuckTicketFinisher
    {
        private readonly SimulatorTransport _ic = new();
        public int Cagri;
        public BaskiButcesi? SonButce;
        public StuckTicketResult Sonuc = new(true, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0x00020006, ToplamMinor: 990, TahsilMinor: 990, TicketId: "tkt_1");

        public StuckTicketResult FinishStuckTicket(bool uygula, BaskiButcesi? butce = null)
        { Cagri++; SonButce = butce; return Sonuc; }

        public Task<TransportResult> SaleAsync(SaleRequest r, CancellationToken ct = default) => _ic.SaleAsync(r, ct);
        public Task<TicketState> ReadTicketAsync(CancellationToken ct = default) => _ic.ReadTicketAsync(ct);
        public Task<PaymentProbe> ProbeAsync(SaleRequest r, CancellationToken ct = default) => _ic.ProbeAsync(r, ct);
        public Task<bool> EchoAsync(CancellationToken ct = default) => _ic.EchoAsync(ct);
        public Task<TransportResult> VoidAsync(CancellationToken ct = default) => _ic.VoidAsync(ct);
        public Task<TicketVoidResult> VoidTicketAsync(string t, CancellationToken ct = default)
            => _ic.VoidTicketAsync(t, ct);
    }

    private (LocalSaleHandler, SahteTamamlayici) Kur(TimeSpan? kilitTavani = null)
    {
        var tasima = new SahteTamamlayici();
        var orch = new AgentOrchestrator(_store, tasima, _clock, RecoveryPolicy.Immediate);
        var h = new LocalSaleHandler(new SahteTutar(), orch, _store, new SahteBolum(),
            new SahteYontem(), new SahteBildirici(), _outbox, tasima,
            log: (m, o) => _loglar.Add((m, o)), satisKilitTavani: kilitTavani);
        return (h, tasima);
    }

    private static TicketFinishRequest Istek(string fid = "fin-1", string? oturum = null) =>
        new("svc12345", "kasa-1", "t1", fid, DateTimeOffset.UtcNow, oturum);

    private static string Zarf(string? fid) =>
        """{"SaleToPOIRequest":{"MessageHeader":{"ProtocolVersion":"3.0","MessageClass":"Service","MessageCategory":"TicketFinish","MessageType":"Request","ServiceID":"svc12345","SaleID":"kasa-1","POIID":"t1"}"""
        + (fid is null ? "}}" : ",\"Restomenum\":{\"ticketFinishId\":\"" + fid + "\"}}}");

    private static string ZarfOturumlu(string fid, string? oturum) =>
        "{\"SaleToPOIRequest\":{\"MessageHeader\":{\"ProtocolVersion\":\"3.0\",\"MessageClass\":\"Service\","
        + "\"MessageCategory\":\"TicketFinish\",\"MessageType\":\"Request\",\"ServiceID\":\"svc12345\","
        + "\"SaleID\":\"kasa-1\",\"POIID\":\"t1\"},\"Restomenum\":{\"ticketFinishId\":\"" + fid + "\""
        + (oturum is null ? "" : ",\"saleSessionId\":\"" + oturum + "\"")
        + "}}}";

    private static JsonElement Oku(string govde) =>
        JsonDocument.Parse(govde).RootElement.GetProperty("SaleToPOIResponse");

    // ── AYRIŞTIRMA ──────────────────────────────────────────────────────────────

    [Fact]
    public void ticketFinishId_ZORUNLU_yoksa_istek_REDDEDILIR()
    {
        Assert.NotNull(TicketFinishParser.Parse(Zarf("fin-1"), out _));
        // ← ÇİVİ: anahtarsız çalıştırmak, tekrar gelen bir isteğin cihaza İKİNCİ kez gitmesine
        // izin vermektir — ve `FP3_Start` yoklama fişi açıp MALİ NUMARA tüketir.
        Assert.Null(TicketFinishParser.Parse(Zarf(null), out _));
        Assert.Null(TicketFinishParser.Parse(Zarf("   "), out _));
        Assert.Null(TicketFinishParser.Parse(Zarf(new string('x', TicketFinishParser.AnahtarEnCok + 1)), out _));
    }

    [Fact]
    public void RED_SEBEBI_AYRISIR_yok_bos_ve_cok_uzun_ayni_sey_degil()
    {
        // ← ÇİVİ: SAHADA KÖR ARAMA YAPTIRDI. 2026-09-10 18:32:59'da kasa geçerli bir istek
        // gönderdi, reddettim ve log yalnız "ticketFinishId zorunlu" dedi. Alan yok muydu,
        // yanlış yerde miydi, çok mu uzundu — çıkarılamıyordu. Karşı taraf kendi tarafında
        // hata aradı; oysa kusur BENDEYDİ: sınırım 64, anahtarları 66 karakterdi.
        //
        // Teşhis edilemeyen bir red, karşı tarafı kör arama yaptırır. W128'deki görünmez
        // cihaz koduyla aynı sınıf hata.
        Assert.Null(TicketFinishParser.Parse(Zarf(null), out var yok));
        Assert.Contains("YOK", yok);

        Assert.Null(TicketFinishParser.Parse(Zarf("   "), out var bos));
        Assert.Contains("BOŞ", bos);

        Assert.Null(TicketFinishParser.Parse(Zarf(new string('x', 200)), out var uzun));
        Assert.Contains("ÇOK UZUN", uzun);
        Assert.Contains("200", uzun);   // GELEN uzunluk da yazılır — karşı taraf ölçebilsin

        // Başarıda sebep YOKTUR.
        Assert.NotNull(TicketFinishParser.Parse(Zarf("fin-1"), out var sebepYok));
        Assert.Null(sebepYok);
    }

    [Fact]
    public void KASANIN_DETERMINISTIK_ANAHTARI_66_KARAKTER_KABUL_EDILIR()
    {
        // ← ÇİVİ: 64 sınırı GERÇEK bir anahtarı kesiyordu. Kasanın şekli
        // `<uuid>-<terminalId>-ticketfinish` = 36+1+16+13 = 66 karakter ve bu iyi bir
        // tasarım: deterministik, okunabilir, çakışmaz. Sınır taşmayı engellemek için var,
        // makul anahtarları kesmek için değil.
        var anahtar = "edb4d562-c4de-4c0d-9e7c-0f5850b2e4b3-windows-gmp-test-ticketfinish";
        Assert.Equal(66, anahtar.Length);

        var istek = TicketFinishParser.Parse(Zarf(anahtar), out var neden);

        Assert.NotNull(istek);
        Assert.Null(neden);
        Assert.Equal(anahtar, istek!.TicketFinishId);
    }

    [Fact]
    public void HTTP_YOL_AYRIMI_TicketFinish_zarfini_dogru_dala_gonderir()
    {
        // W106'daki kaçağın aynısı burada olmasın: kategori bakışı bu zarfı tanımalı ve
        // ödeme ayrıştırıcısı onu reddetmeli (dal olmazsa istek 400'e düşer).
        Assert.Equal(TicketFinishParser.Category, ReversalRequestParser.PeekCategory(Zarf("fin-1")));
        var red = Assert.IsType<SaleToPoiParseResult.Invalid>(SaleToPoiRequestParser.Parse(Zarf("fin-1")));
        Assert.Equal(SaleToPoiRejectReason.UnsupportedCategory, red.Reason);
    }

    [Fact]
    public void SALE_SESSION_ID_zarftan_okunur_ve_YOKLUGU_gecerlidir()
    {
        // Alan İSTEĞE BAĞLI: eski kasa sürümü göndermez. Yokluğu isteği geçersiz kılmaz.
        var oturumlu = TicketFinishParser.Parse(ZarfOturumlu("fin-1", "otr-A"), out _);
        Assert.Equal("otr-A", oturumlu!.SaleSessionId);

        var oturumsuz = TicketFinishParser.Parse(ZarfOturumlu("fin-1", null), out var neden);
        Assert.NotNull(oturumsuz);
        Assert.Null(oturumsuz!.SaleSessionId);
        Assert.Null(neden);
    }

    // ── DAVRANIŞ ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KURTARMANIN_KAPATTIGI_FISIN_de_MALI_NUMARASI_yazilir()
    {
        // ← ÇİVİ: W144 numarayı yalnız OLAĞAN satış yoluna koymuştu. Oysa denetimde ya da bir
        // itirazda aranacak fişler tam olarak KURTARILANLAR — arıza yaşamış, yarım kalmış,
        // sonradan kapatılmış olanlar. Onların numarası hiçbir yerde yoktu.
        var (h, tasima) = Kur();
        tasima.Sonuc = tasima.Sonuc with { FiscalReceiptNo = 7 };

        var ek = Oku(await h.HandleTicketFinishAsync(Istek())).GetProperty("Restomenum");

        Assert.Equal(7, ek.GetProperty("fiscalReceiptNo").GetInt32());
    }

    [Fact]
    public async Task MALI_NUMARA_OKUNAMAZSA_GOVDEYE_YAZILMAZ()
    {
        // ← ÇİVİ: "yok" ile "0" ayrı şeyler. Sıfır yazmak, var olmayan bir fişe işaret eden
        // gerçek bir numara iddiası olurdu; alanın YOKLUĞU "ölçülemedi" demektir.
        var (h, tasima) = Kur();
        tasima.Sonuc = tasima.Sonuc with { FiscalReceiptNo = null };

        var ek = Oku(await h.HandleTicketFinishAsync(Istek())).GetProperty("Restomenum");

        Assert.False(ek.TryGetProperty("fiscalReceiptNo", out _));
    }

    [Fact]
    public async Task BASKA_HESABIN_fisi_MALI_OLARAK_KESINLESTIRILMEZ()
    {
        // ← ÇİVİ: tamamlama, cihazdaki AÇIK FİŞİ tamamlar — hangi fiş olduğuna BAKMADAN.
        // Kasanın tetiği kendi `open` kaydı ve o kayıt bayatlayabiliyor. S1 kapatılırken
        // cihazda S2'nin fişi açıksa, kapı olmadan S2'nin fişi MALİ OLARAK KESİNLEŞİR ve
        // S2'nin kasiyeri sebebini hiç öğrenmez. Mali kesinleşme GERİ ALINAMAZ.
        var (h, tasima) = Kur();
        _store.BindOpenTicket("t1", "oturum-S2");      // cihazda S2'nin fişi açık

        var ek = Oku(await h.HandleTicketFinishAsync(Istek(oturum: "oturum-S1")))
            .GetProperty("Restomenum");

        Assert.Equal(0, tasima.Cagri);                 // ⚠️ CİHAZA HİÇ GİDİLMEDİ
        Assert.Equal("SESSION_MISMATCH", ek.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task KANIT_YOKSA_ENGELLENMEZ_eski_kasa_surumu_calismaya_devam_eder()
    {
        // ← ÇİVİ: "alan gelmedi" ile "başkasının" AYRI şeyler. Yokluğu suçlama saymak, eski
        // kasa sürümünde yarım kalmış mali belgeyi yarım bırakırdı — düzeltmeye çalıştığımız
        // zararın ta kendisi. `paymentInvoked` üç durumluluğuyla aynı ilke.
        var (h, tasima) = Kur();
        _store.BindOpenTicket("t1", "oturum-S2");

        await h.HandleTicketFinishAsync(Istek(oturum: null));

        Assert.Equal(1, tasima.Cagri);                 // GEÇTİ
    }

    [Fact]
    public async Task DEFTERDE_BAG_YOKSA_ENGELLENMEZ()
    {
        // ← ÇİVİ: karşılaştıracak bir şey yokken reddetmek, uydurma bir kesinlik satmak olurdu.
        // Cihazda fiş var ama bizim defterimizde bağ yoksa (ör. ajan yeniden başlamış),
        // tamamlama HÂLÂ yapılabilmeli — zaten bu zarfın varlık sebebi o durum.
        var (h, tasima) = Kur();

        await h.HandleTicketFinishAsync(Istek(oturum: "oturum-S1"));

        Assert.Equal(1, tasima.Cagri);
    }

    [Fact]
    public async Task ESLESEN_oturum_GECER()
    {
        var (h, tasima) = Kur();
        _store.BindOpenTicket("t1", "oturum-S1");

        await h.HandleTicketFinishAsync(Istek(oturum: "oturum-S1"));

        Assert.Equal(1, tasima.Cagri);
    }

    [Fact]
    public async Task Kapatinca_HAM_bayraklari_ve_uretici_adimini_bildirir()
    {
        var (h, tasima) = Kur();

        var ek = Oku(await h.HandleTicketFinishAsync(Istek())).GetProperty("Restomenum");

        Assert.Equal("CLOSED", ek.GetProperty("ticketState").GetString());
        // ← ÇİVİ: ham bayraklar HER ZAMAN taşınır. Bu alan cihaz izinde GÖRÜNMÜYOR, yalnız
        // yanıtta geliyor; kasa yorumlamıyor ama sözleşmeye/olay kaydına geçiriyor.
        Assert.Equal("0x00020006", ek.GetProperty("transactionFlags").GetString());
        Assert.Equal(5, ek.GetProperty("recoveryStep").GetInt32());
        Assert.Equal(990, ek.GetProperty("devicePaidMinor").GetInt32());
        Assert.Equal(1, tasima.Cagri);
        // ← ÇİVİ: kurtarma zinciri de 310 sn'ye çıkabiliyor; bütçesiz çağrı kasanın 180 sn'lik
        // kapısını aşar ve düzeltmeye çalıştığımız arızayı bu kez kurtarma yolunda üretirdi.
        Assert.NotNull(tasima.SonButce);
    }

    [Fact]
    public async Task AYNI_ticketFinishId_ikinci_kez_gelirse_CIHAZA_GITMEZ()
    {
        var (h, tasima) = Kur();

        var ilk = await h.HandleTicketFinishAsync(Istek());
        var ikinci = await h.HandleTicketFinishAsync(Istek());

        // ← ÇİVİ: kasa ağ hatasında aynı zarfı yeniden POST eder. İkinci kez cihaza gitmek,
        // kapanmış bir fiş için `FP3_Start` çağırıp YOKLAMA FİŞİ açtırmak ve mali numara
        // tüketmek olurdu.
        Assert.Equal(1, tasima.Cagri);
        Assert.Equal(ilk, ikinci);
        Assert.True(_loglar.Any(l => l.Mesaj.Contains("saklanan sonuç replay")));
    }

    [Fact]
    public async Task BASARISIZ_deneme_saklanmaz_kagit_degisince_TEKRAR_denenebilir()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintMF", "0xF003");

        var ilk = Oku(await h.HandleTicketFinishAsync(Istek()));
        Assert.Equal("Failure", ilk.GetProperty("Response").GetProperty("Result").GetString());

        var ikinci = await h.HandleTicketFinishAsync(Istek());

        // ← ÇİVİ: başarısız bir denemeyi "kesin sonuç" diye saklamak, elle kurtarmayı TEK
        // denemeye indirirdi — oysa bu zarfın bütün varlık sebebi insanın doğru anı seçmesi
        // (kâğıdı değiştirip tekrar denemek).
        Assert.Equal(2, tasima.Cagri);
        Assert.Equal("PrintMF", Oku(ikinci).GetProperty("Restomenum").GetProperty("printFailedStep").GetString());
    }

    [Fact]
    public async Task Cihazda_fis_YOKSA_bu_BASARISIZLIK_DEGIL()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, FisAcikDegil: true, false,
            new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "TICKET_NOT_OPEN", 0), 0);

        var y = Oku(await h.HandleTicketFinishAsync(Istek()));

        // ← ÇİVİ: yapılacak iş YOKTU. `Failure` dönmek kasiyeri var olmayan bir arızayla
        // uğraştırırdı (fiş kasiyer ya da süpürme tarafından çoktan kapatılmış olabilir).
        Assert.Equal("Success", y.GetProperty("Response").GetProperty("Result").GetString());
        Assert.Equal("NOT_OPEN", y.GetProperty("Restomenum").GetProperty("ticketState").GetString());
    }

    [Fact]
    public async Task ODEMESI_EKSIK_fis_Failure_doner_ve_sebebini_soyler()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.Dokunma, TicketFinish.OdemeTamamlanmamis, 7), 0);

        var y = Oku(await h.HandleTicketFinishAsync(Istek()));

        // ← ÇİVİ: kasiyer "tamam" görüp masayı KAPATMAMALI — fişin kalanı hâlâ tahsil edilmedi.
        Assert.Equal("Failure", y.GetProperty("Response").GetProperty("Result").GetString());
        Assert.Equal("TICKET_NOT_COMPLETE", y.GetProperty("Restomenum").GetProperty("reason").GetString());
        Assert.Equal(7, y.GetProperty("Restomenum").GetProperty("recoveryStep").GetInt32());
    }

    [Fact]
    public async Task SATIS_SURERKEN_cihaza_GIDILMEZ()
    {
        var (h, tasima) = Kur(kilitTavani: TimeSpan.FromMilliseconds(50));
        using var satis = h.TryAcquire();
        Assert.NotNull(satis);

        var is_ = h.HandleTicketFinishAsync(Istek());
        var biten = await Task.WhenAny(is_, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(biten == is_, "kilit tavanı yok: çağrı 10 sn içinde dönmedi (ASILDI)");

        // ← ÇİVİ: satış HER ZAMAN öncelikli ve tavansız beklemek kasayı cevapsız bırakırdı.
        Assert.Equal(0, tasima.Cagri);
        Assert.Equal("TERMINAL_BUSY",
            Oku(await is_).GetProperty("Restomenum").GetProperty("reason").GetString());
    }

    // ── SAHTELER ────────────────────────────────────────────────────────────────

    private sealed class SahteTutar : IPaymentDetailClient
    {
        public Task<PaymentDetailResult> FetchAsync(string paymentId, CancellationToken ct = default)
            => Task.FromResult<PaymentDetailResult>(
                new PaymentDetailResult.Rejected(PaymentRejectReason.NotFound, "yok", 404));
    }

    private sealed class SahteBolum : ILineDepartmentResolver
    {
        public DepartmentMatch? Resolve(string? productCode, string? categoryId) => new(1, 2000);
    }

    private sealed class SahteYontem : IPaymentMethodResolver
    {
        public int? Resolve(string paymentMethodId) => GmpPaymentTypes.Cash;
    }

    private sealed class SahteBildirici : IResultNotifier
    {
        private static NotifyResult Ok() => new(NotifyOutcome.Recorded, "COMPLETED", null, 200, "ok");
        public Task<NotifyResult> NotifyAsync(string p, string b, CancellationToken ct = default) => Task.FromResult(Ok());
        public Task<NotifyResult> NotifyTicketCancelAsync(string b, CancellationToken ct = default) => Task.FromResult(Ok());
        public Task<NotifyResult> NotifyTicketClosedAsync(string b, CancellationToken ct = default) => Task.FromResult(Ok());
    }
}
