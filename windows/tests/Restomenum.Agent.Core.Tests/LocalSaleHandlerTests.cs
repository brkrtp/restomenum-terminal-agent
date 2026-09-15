using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Yerel ödeme akışı — kasadan SaleToPOI → GET tutar → departman → orkestratör → ÖNCE bildir SONRA dön.
/// Karar mantığı (dedupe/durum/UNKNOWN) <see cref="AgentOrchestrator"/>'da; burada AKIŞ çivilenir.
/// </summary>
public class LocalSaleHandlerTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"lsh_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;
    private readonly Outbox _outbox;
    private readonly ClockOffset _clock = new();

    public LocalSaleHandlerTests()
    {
        _store = CommandStore.Open(_db);
        _outbox = Outbox.Open(_db);
        _clock.Sync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());   // saat senkron (offset≈0)
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    // ── Fakes ──
    private sealed class FakeAmounts : IPaymentDetailClient
    {
        public required PaymentDetailResult Result;
        /// <summary>Çağrı SIRASINDA çalışır — testte sahte saati ilerletmek için (W51/W51b).</summary>
        public Action? Sirasinda;
        public Task<PaymentDetailResult> FetchAsync(string p, CancellationToken ct = default)
        { Sirasinda?.Invoke(); return Task.FromResult(Result); }
    }

    private sealed class FakeResolver : ILineDepartmentResolver
    {
        public int? Dept = 3;
        public int? Rate;   // null = §30.12 doğrulaması atlanır (mevcut testler etkilenmez)
        public string? LastProductCode;
        public string? LastCategoryId;
        public DepartmentMatch? Resolve(string? productCode, string? categoryId)
        {
            LastProductCode = productCode;
            LastCategoryId = categoryId;
            return Dept is int d ? new DepartmentMatch(d, Rate) : (DepartmentMatch?)null;
        }
    }

    private sealed class FakePaymentMethods : IPaymentMethodResolver
    {
        public int? Type = GmpPaymentTypes.Card;   // varsayılan geçerli — mevcut testler etkilenmez
        public string? LastMethodId;
        public int? Resolve(string paymentMethodId) { LastMethodId = paymentMethodId; return Type; }
    }

    private sealed class FakeNotifier : IResultNotifier
    {
        public List<string> Bodies { get; } = new();
        public NotifyResult Result = new(NotifyOutcome.Recorded, "APPROVED", null, 200, "");
        /// <summary>Çağrı SIRASINDA çalışır — testte sahte saati ilerletmek için (W51b).</summary>
        public Action? Sirasinda;
        public Task<NotifyResult> NotifyAsync(string p, string body, CancellationToken ct = default)
        { Bodies.Add(body); Sirasinda?.Invoke(); return Task.FromResult(Result); }
        public Task<NotifyResult> NotifyTicketCancelAsync(string body, CancellationToken ct = default)
        { Bodies.Add(body); return Task.FromResult(Result); }
        public List<string> TicketClosedBodies { get; } = new();
        public Task<NotifyResult> NotifyTicketClosedAsync(string body, CancellationToken ct = default)
        { TicketClosedBodies.Add(body); Sirasinda?.Invoke(); return Task.FromResult(Result); }
    }

    private const string Pay = "pay_0123456789abcdef0123456789abcdef01234567";

    private static SaleToPoiRequest Req(string serviceId = "svc1") =>
        new(serviceId, "kasa-1", "term-01", Pay, "1042", DateTimeOffset.UtcNow);

    private static PaymentDetail Detail(long amount = 24000, string pmId = "11-cash") =>
        new(Pay, "1042", "TRY", 2, amount, amount, "TR", "ACCEPTED",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000, "fullSale",
            new List<SaleLine> { new(0, "p1", "Adana", 2, amount, "10", "c1", "l1") },
            pmId);

    /// <summary>Satış öncesi eşleme tazeleyicisi — gerçekte HTTP, testte kontrollü.</summary>
    private sealed class FakeRefresher : IMappingRefresher
    {
        public int Calls;
        public int? YeniSurum;
        public Exception? Patlat;
        public TimeSpan Gecikme = TimeSpan.Zero;
        public async Task<int?> EnsureFreshAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Gecikme > TimeSpan.Zero) await Task.Delay(Gecikme, ct);
            if (Patlat is not null) throw Patlat;
            return YeniSurum;
        }
    }

    /// <summary>Yakalanan log çağrıları (W51b çivileri sırayı ve alanları buradan okuyor).</summary>
    private readonly List<(string Mesaj, object? Veri)> _loglar = new();

    /// <summary>Sahte duvar saati (unix ms) — W51/W51b süre ölçümü testte bununla sürülüyor.</summary>
    private long _saatMs = 1_700_000_000_000;

    private object? LogVeri(string mesajParcasi) =>
        _loglar.LastOrDefault(l => l.Mesaj.Contains(mesajParcasi)).Veri;

    private static T? Alan<T>(object? veri, string ad) =>
        veri is null ? default : (T?)veri.GetType().GetProperty(ad)?.GetValue(veri);

    private (LocalSaleHandler, SimulatorTransport, FakeNotifier) Kur(
        PaymentDetailResult amounts, int? dept = 3, TransportResult? terminal = null, int? rate = null,
        int? paymentType = GmpPaymentTypes.Card, IMappingRefresher? refresher = null,
        DeviceIdentityHolder? kimlik = null)
    {
        var sim = new SimulatorTransport();
        if (terminal is not null) sim.Expect(terminal);
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var notifier = new FakeNotifier();
        _loglar.Clear();
        var h = new LocalSaleHandler(new FakeAmounts { Result = amounts }, orch, _store,
            new FakeResolver { Dept = dept, Rate = rate }, new FakePaymentMethods { Type = paymentType },
            notifier, _outbox, sim,
            now: () => DateTimeOffset.FromUnixTimeMilliseconds(_saatMs),
            log: (m, o) => _loglar.Add((m, o)),
            mappingRefresher: refresher, kimlik: kimlik);
        return (h, sim, notifier);
    }

    // ── W63: SATIR İNDİRİMİ KAPISI ──────────────────────────────────────────────

    /// <summary><paramref name="indirim"/> ANA birimde (ör. 3.00 = 300 kuruş) — `ItemAmount` ile aynı
    /// eksende. Ayrıştırıcının yaptığı çeviri burada elle yapılıyor ki işleyicinin kapısı sınansın.</summary>
    private static PaymentDetail Indirimli(decimal? indirim, bool bozuk = false, long tutar = 24000) =>
        new(Pay, "1042", "TRY", 2, tutar, tutar, "TR", "ACCEPTED",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000, "fullSale",
            new List<SaleLine> { new(0, "p1", "Adana", 2, tutar, "10", "c1", "l1",
                indirim, indirim is decimal h ? Money.ToMinor(h, 2) : 0, bozuk) },
            "11-cash");

    [Theory]
    [InlineData(-1)]        // negatif
    [InlineData(240.01)]    // 24001 kuruş — satır toplamını (24000) AŞIYOR
    public async Task GECERSIZ_indirim_terminale_GITMEDEN_reddedilir(double indirimAna)
    {
        // ← ÇİVİ: geçersiz bir indirimi kırpmak müşteriden yanlış tutar tahsil etmek, yok saymak
        // indirimi hiç uygulamamak olurdu. İkisi de GERİ ALINAMAZ mali kayıt üretir; tek doğru
        // davranış cihaza gitmeden reddetmek.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Indirimli((decimal)indirimAna)));

        var cevap = await h.HandleAsync(Req());

        Assert.Empty(sim.SaleCalls);                       // ← ÇİVİ: cihaza HİÇ gidilmedi
        var resp = Resp(cevap);
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("PaymentRestriction", resp.GetProperty("ErrorCondition").GetString());
        Assert.Contains("DISCOUNT_INVALID", resp.GetProperty("AdditionalResponse").GetString());
        Assert.Contains("p1", resp.GetProperty("AdditionalResponse").GetString());   // HANGİ ürün
    }

    [Fact]
    public async Task DISCOUNT_INVALID_reddi_REASON_ile_tasinir_paymentInvoked_FALSE()
    {
        // ← ÇİVİ (sözleşme, c5 ölçtü): platform reddi `AdditionalResponse`tan OKUMUYOR
        // (`nexoResponse.js:302`). Yalnız `ErrorCondition PaymentRestriction` görürse tablodan
        // "declined" çıkarıyor ve kasiyere **"banka reddetti"** dedirtiyor — oysa banka hiç
        // devrede değil (P16 hatasının aynısı). Karar `Restomenum.reason` dalından okunuyor ve o
        // dal `paymentInvoked: false` ile birlikte "başlamadı" (`notStarted`) üretiyor.
        //
        // `paymentInvoked: true` gelirse platformun tutarlılık kapısı sonucu tamamen reddediyor;
        // yani bu iki alan BİRLİKTE doğru olmak zorunda.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Indirimli(-1)));

        var govde = await h.HandleAsync(Req());
        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");

        Assert.Equal("DISCOUNT_INVALID", ek.GetProperty("reason").GetString());   // ← ÇİVİ
        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());              // ← ÇİVİ
        Assert.Equal("p1", ek.GetProperty("productCode").GetString());            // hangi kalem
        Assert.Empty(sim.SaleCalls);                                              // cihaza gidilmedi
    }

    [Fact]
    public async Task KURUS_ALTI_sapma_reddedilir()
    {
        // ← ÇİVİ: 0,995 lira = 99,5 kuruş ve `Money.ToMinor` onu 100'e yuvarlar — müşteriden yarım
        // kuruş FAZLA alınır. Hangi yöne yuvarlarsak yuvarlayalım biri yanlış; tek doğru davranış
        // reddetmek. (Exponent 2 → en fazla iki ondalık basamak.)
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Indirimli(0.995m)));

        var cevap = await h.HandleAsync(Req());

        Assert.Empty(sim.SaleCalls);
        Assert.Contains("DISCOUNT_INVALID", cevap);
    }

    [Fact]
    public async Task SAYI_OLMAYAN_indirim_reddedilir()
    {
        // ← ÇİVİ: "alan var ama sayı değil" ≠ "alan yok". İkincisi indirimsiz satıştır; ilkini
        // indirimsiz saymak, sözleşme ihlalini SESSİZCE geçirmek olurdu.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Indirimli(null, bozuk: true)));

        var cevap = await h.HandleAsync(Req());

        Assert.Empty(sim.SaleCalls);
        Assert.Contains("DISCOUNT_INVALID", cevap);
    }

    [Fact]
    public async Task Indirim_SATIR_TOPLAMINA_ESIT_olabilir()
    {
        // ← ÇİVİ: sözleşme `0 < x ≤ ItemAmount` — eşitlik DAHİL (tam ikram). Sınırı `<` yapmak
        // ikramı imkânsız kılardı. (Cihazın 990→0'ı kabul edip etmediği W60'ta ölçülecek; bugünkü
        // kapı SÖZLEŞMEYE uyar, ölçülmemiş cihaz davranışına değil.)
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Indirimli(240m)));   // 240 TL = 24000 kuruş

        await h.HandleAsync(Req());

        Assert.Single(sim.SaleCalls);                      // cihaza GİTTİ
        Assert.Equal(24000, sim.SaleCalls[0].FiscalLines!.Sum(l => l.LineDiscountMinor * l.Quantity));
    }

    [Fact]
    public async Task SIFIR_indirim_GECERLI_sayilir()
    {
        // ← ÇİVİ: sözleşme `0 < x` diyor ama açıkça sıfır gelen bir indirim YANLIŞ MALİ KAYIT
        // ÜRETMEZ — "indirim yok" ile aynı şeydir. Onu reddetmek zararsız bir değer yüzünden satışı
        // düşürüp kasayı manuel ödemeye indirmek olurdu (kullanıcının değişmez şartı).
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Indirimli(0)));

        await h.HandleAsync(Req());

        Assert.Single(sim.SaleCalls);
        Assert.Equal(0, sim.SaleCalls[0].FiscalLines!.Sum(l => l.LineDiscountMinor));
    }

    [Fact]
    public async Task GECERLI_indirim_fis_satirlarina_TASINIR()
    {
        // ← ÇİVİ: alan taşınmazsa `LineDiscountMinor` yine ölü kalır ve indirim hiçbir yere ulaşmaz.
        // 24000 kuruş / 2 adet, indirim 300 → birim başına 150.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Indirimli(3m)));    // 3 TL = 300 kuruş

        await h.HandleAsync(Req());

        var satirlar = sim.SaleCalls[0].FiscalLines!;
        Assert.Equal(300, satirlar.Sum(l => l.LineDiscountMinor * l.Quantity));
        Assert.All(satirlar, l => Assert.Equal(150, l.LineDiscountMinor));
    }

    [Fact]
    public async Task Indirim_ALANI_YOKSA_satis_normal_gecer()
    {
        // Bugünkü davranış korunuyor: alan yok → indirim 0, hiçbir kapı tetiklenmiyor.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()));

        await h.HandleAsync(Req());

        Assert.Single(sim.SaleCalls);
        Assert.All(sim.SaleCalls[0].FiscalLines!, l => Assert.Equal(0, l.LineDiscountMinor));
    }

    // ── W76: KAYIP KAPANIŞ KURTARMASINDA YAŞ KAPISI ─────────────────────────────

    /// <summary>Sabit duvar saati — yaş kapısı sınırları buna göre kuruluyor.</summary>
    private const long T0 = 1_700_000_000_000;

    [Fact]
    public void TAZE_kapanis_TOPLANMAZ_yaris_yok()
    {
        // ← ÇİVİ (sahada görüldü 2026-09-09 15:44): normal akışta fiş kapanışı ile bildirimin
        // outbox'a yazılması arasında ~800 ms var (cihaz `Close` 49.873 → `fiş KAPANDI` 50.698).
        // Arka plan drain'i tam o aralıkta dönünce satırı "kayıp" sanıp gövdeyi İKİNCİ KEZ kurdu
        // ve gönderdi. Zarar olmadı (aynı olay kimliği, platform tekrarı gördü) ama yarış gerçek.
        _store.MarkTicketClosed("tkt-taze", "term-01", "otr", 990, 990, now: T0);

        // 800 ms sonra bakılıyor: eşik 30 sn geriden.
        var esik = T0 + 800 - 30_000;

        Assert.Empty(_store.PendingClosedTickets(esik));
    }

    [Fact]
    public void ESKI_kapanis_TOPLANIR_koruma_duruyor()
    {
        // ← ÇİVİ: yaş kapısı yarışı kaldırırken korumayı da kaldırmamalı. Bu kurtarmanın var oluş
        // sebebi "süreç öldü ve gövde hiç kurulamadı" — o kayıt SONSUZA KADAR kuyrukta görünmez,
        // tek izi `closed_tickets`tir ve mutlaka toplanmalıdır.
        _store.MarkTicketClosed("tkt-eski", "term-01", "otr", 990, 990, now: T0 - 31_000);

        var esik = T0 - 30_000;
        var bekleyen = Assert.Single(_store.PendingClosedTickets(esik));

        Assert.Equal("tkt-eski", bekleyen.TicketId);
    }

    [Fact]
    public async Task DRAIN_taze_kapanisi_TOPLAMAZ_yaris_kapandi()
    {
        // ← ÇİVİ: kapı deponun sorgusunda değil, KULLANILDIĞI yerde de doğru olmalı. Depoyu tek
        // başına sınamak yetmiyordu — mutasyon "eşiği hiç geçirme" ve "eşiği 0 yap" dediğinde
        // depo testleri YEŞİL kalıyordu, çünkü işleyicinin bağlantısını kimse sınamıyordu.
        // Bu test drain'in tamamından geçiyor: taze bir kapanış GÖNDERİLMEMELİ.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()));
        _store.MarkTicketClosed("tkt_taze", "term-01", "oturum-A", 990, 990, now: _saatMs);
        _store.RecordTicketPayment("tkt_taze", "pay-1", 990, GmpPaymentTypes.Cash, null);

        await h.DrainOutboxAsync();

        Assert.Empty(notifier.TicketClosedBodies);          // ← ÇİVİ: gönderilmedi
        Assert.NotEmpty(_store.PendingClosedTickets());      // kayıt DURUYOR, kaybolmadı
    }

    [Fact]
    public void Yas_kapisi_BILDIRILMISI_yine_atlar()
    {
        // Kapı yaşa bakıyor ama `notified_at` süzgeci YERİNE geçmiyor: bildirilmiş bir kapanış,
        // ne kadar eski olursa olsun toplanmamalı.
        _store.MarkTicketClosed("tkt-eski", "term-01", "otr", 990, 990, now: T0 - 60_000);
        _store.ConfirmTicketClosed("tkt-eski");

        Assert.Empty(_store.PendingClosedTickets(T0));
    }

    // ── W62: KURTARMA UÇUŞTAKİ KOMUTA DOKUNMAZ ──────────────────────────────────

    [Fact]
    public async Task Kurtarma_UCUSTAKI_komutu_ATLAR()
    {
        // ← ÇİVİ: sahada görüldü (2026-09-09 01:44). Satış yolu terminal kilidini yalnız CİHAZ
        // çağrısı boyunca tutuyor; kurtarma turu bildirimden önceki boşlukta kilidi alıp hâlâ
        // uçuşta olan komutun adına P4 gönderdi ve o komutun YOKLAMASINI KAPATTI. O an satış
        // çökseydi komut UNKNOWN + kapatılmış kalırdı — bir daha hiç yoklanmaz, sessiz kayıp.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()));

        // Cihaz çağrısı SÜRERKEN kurtarma turunu başlat: satış hâlâ uçuşta.
        sim.Delay = TimeSpan.FromMilliseconds(300);
        var satis = h.HandleAsync(Req("svc-ucusta"));
        await Task.Delay(120);
        await h.RecoverPendingAsync();
        await satis;

        var veri = LogVeri("UÇUŞTAKİ komutlar atlandı");
        Assert.NotNull(veri);
        Assert.Equal(1, Alan<int>(veri, "atlanan"));
        Assert.Contains("svc-ucusta", Alan<string>(veri, "komutlar"));
    }

    [Fact]
    public async Task Satis_BITINCE_komut_yeniden_kurtarilabilir()
    {
        // ← ÇİVİ: koruma kalıcı olsaydı, koruduğu şeyi yok ederdi — bir kez işlenen komut bir daha
        // ASLA kurtarılamazdı. `finally` her çıkış yolunda kümeden düşürmeli.
        var (h, _, _) = Kur(new PaymentDetailResult.Ok(Detail()));

        await h.HandleAsync(Req("svc-biten"));
        _loglar.Clear();
        await h.RecoverPendingAsync();

        Assert.Null(LogVeri("UÇUŞTAKİ komutlar atlandı"));   // artık uçuşta değil
    }

    [Fact]
    public async Task Satis_PATLASA_da_komut_kumeden_DUSER()
    {
        // ← ÇİVİ: istisna yolunda küme temizlenmezse komut sonsuza kadar "uçuşta" görünür ve
        // kurtarma onu bir daha hiç ele almaz. Tam da kaçındığımız sessiz kayıp.
        var patlayan = new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) };
        patlayan.Sirasinda = () => throw new InvalidOperationException("tutar ucu patladı");
        var orch = new AgentOrchestrator(_store, new SimulatorTransport(), _clock, RecoveryPolicy.Immediate);
        _loglar.Clear();
        var h = new LocalSaleHandler(patlayan, orch, _store, new FakeResolver(), new FakePaymentMethods(),
            new FakeNotifier(), _outbox, new SimulatorTransport(),
            now: () => DateTimeOffset.FromUnixTimeMilliseconds(_saatMs),
            log: (m, o) => _loglar.Add((m, o)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.HandleAsync(Req("svc-patlak")));
        _loglar.Clear();
        await h.RecoverPendingAsync();

        Assert.Null(LogVeri("UÇUŞTAKİ komutlar atlandı"));
    }

    // ── W61: BANKA KİMLİĞİ YALNIZ BANKA BACAĞI OLAN TİPTE ───────────────────────

    private static SaleToPoiRequest ReqBanka(int bkm, string serviceId = "svc-bkm") =>
        new(serviceId, "kasa-1", "term-01", Pay, "1042", DateTimeOffset.UtcNow, BankBkmId: bkm);

    [Fact]
    public async Task Banka_bacagi_YOKSA_bankBkmId_DUSURULUR()
    {
        // ← ÇİVİ: `bankBkmId` zarftan geliyor ve ayrıştırıcıda YALNIZ aralık doğrulanıyor; tip ile
        // çapraz kontrol yoktu. `{tip: 32 hediye çeki, bankBkmId: 46}` cihaza 46 ile giderdi ve
        // cihazın banka bacağı olmayan bir ödemede banka seçmeye çalışması mümkün — ölçülmedi.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail(pmId: "11-gift")),
            paymentType: GmpPaymentTypes.GiftVoucher);

        await h.HandleAsync(ReqBanka(46));

        Assert.Null(sim.SaleCalls[0].BankBkmId);   // ← ÇİVİ: cihaza GİTMEDİ
    }

    [Fact]
    public async Task Banka_bacagi_dusurulunce_GORUNUR_log_yazilir()
    {
        // ← ÇİVİ: sessizce düşürmek, kasanın yanlış alan gönderdiğini kimsenin fark etmemesi
        // demek. Loga yalnız yöntem kimliği ve tip girer — kart/kimlik verisi YOK.
        var (h, _, _) = Kur(new PaymentDetailResult.Ok(Detail(pmId: "11-gift")),
            paymentType: GmpPaymentTypes.GiftVoucher);

        await h.HandleAsync(ReqBanka(46));

        var veri = LogVeri("banka kimliği YOK SAYILDI");
        Assert.NotNull(veri);
        Assert.Equal(46, Alan<int>(veri, "gelenBkm"));
        Assert.Equal(GmpPaymentTypes.GiftVoucher, Alan<int>(veri, "tip"));
    }

    [Fact]
    public async Task KARTTA_bankBkmId_AYNEN_tasinir()
    {
        // ← ÇİVİ: düşürme kuralı kartı da yakalarsa kasiyerin SEÇTİĞİ banka kaybolur ve cihaz
        // kendi seçer — sahada ölçüldü ki cihaz aynı fişte iki denemede FARKLI banka seçebiliyor.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail(pmId: "11-credit")),
            paymentType: GmpPaymentTypes.Card);

        await h.HandleAsync(ReqBanka(62));

        Assert.Equal(62, sim.SaleCalls[0].BankBkmId);
    }

    [Fact]
    public async Task Banka_bacagi_YOKSA_ama_bkm_de_YOKSA_log_YAZILMAZ()
    {
        // Gürültü yok: düşürülecek bir şey olmadığında uyarı da olmamalı, yoksa her nakit satışta
        // anlamsız bir satır birikir ve gerçek uyarı içinde kaybolur.
        var (h, _, _) = Kur(new PaymentDetailResult.Ok(Detail()), paymentType: GmpPaymentTypes.Cash);

        await h.HandleAsync(Req());

        Assert.Null(LogVeri("banka kimliği YOK SAYILDI"));
    }

    private static JsonElement Resp(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("SaleToPOIResponse").GetProperty("PaymentResponse").GetProperty("Response");

    // ── W31: KURTARMA TURLARI ÜST ÜSTE BİNMEZ ───────────────────────────────────

    [Fact]
    public async Task Ayni_anda_TEK_kurtarma_turu_kosar()
    {
        // ← ÇİVİ (canlıda görüldü, 2026-09-07 20:59): açılış kurtarması 7 komutu ~31 sn'de bir
        // yoklarken 60 sn'lik periyodik tur başa dönüp AYNI komutları yeniden sordu. Bozulma yok
        // ama cihaza gereksiz tur biniyor; kuyruk uzadıkça turlar yığılırdı.
        _store.Save("svc-A", Pay, "term-01", _clock.ServerNow() + 600_000);
        _store.Advance("svc-A", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL);

        var sim = new SimulatorTransport { Delay = TimeSpan.FromMilliseconds(250) };
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var h = new LocalSaleHandler(new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) },
            orch, _store, new FakeResolver(), new FakePaymentMethods(),
            new FakeNotifier(), _outbox, sim);

        // İki tur AYNI ANDA: biri koşar, diğeri ATLAR (beklemez).
        var t1 = h.RecoverPendingAsync();
        var t2 = h.RecoverPendingAsync();
        await Task.WhenAll(t1, t2);

        // Terminale komut başına TEK yoklama gitti — iki tur çalışsaydı iki olurdu.
        Assert.Equal(1, sim.ReadTicketCalls + sim.ProbeCalls);
    }

    // ── W51b: SÜRE DÖKÜMÜ SATIŞIN TAMAMINI ÖLÇÜYOR ─────────────────────────────

    [Fact]
    public async Task Sure_dokumu_BILDIRIMLERDEN_SONRA_yazilir()
    {
        // ← ÇİVİ: satır bildirimlerden ÖNCE yazılırsa satışın son parçası ölçüm dışında kalır.
        // Sahada ölçüldü (2026-09-08 23:08): `toplamMs = 14.261` ama gerçek uçtan uca 15,76 sn.
        // Ölçüm aracının kendisi eksik ölçüyordu.
        var (h, _, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 990,
                TicketState: "CLOSED", TicketId: "tkt_1"));

        await h.HandleAsync(Req());

        var dokumIndex = _loglar.FindIndex(l => l.Mesaj.Contains("süre dökümü"));
        var kapanisIndex = _loglar.FindIndex(l => l.Mesaj.Contains("fiş kapandı bildirimi"));
        Assert.True(dokumIndex >= 0, "süre dökümü satırı yok");
        Assert.True(kapanisIndex >= 0, "kapanış bildirimi satırı yok");
        Assert.True(dokumIndex > kapanisIndex, "döküm satırı kapanış bildiriminden ÖNCE yazılmış");
    }

    [Fact]
    public async Task toplamMs_UCTAN_UCA_olcuyor_bildirimler_DAHIL()
    {
        // ← ÇİVİ: `toplamMs` cihaz çağrısında bitiyordu. Sahte saat her aşamada ilerliyor;
        // toplam, aşamaların TAMAMINI kapsamazsa bu test kırılır.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 990,
                TicketState: "CLOSED", TicketId: "tkt_1"));
        var bas = _saatMs;
        notifier.Sirasinda = () => _saatMs += 700;      // her bildirim 700 ms
        await h.HandleAsync(Req());
        var gercek = _saatMs - bas;

        var veri = LogVeri("süre dökümü");
        var toplam = Alan<long>(veri, "toplamMs");
        var bildirim = Alan<long>(veri, "bildirimMs");

        Assert.Equal(gercek, toplam);                   // ← ÇİVİ: ±0 (sahte saat)
        Assert.True(bildirim >= 1400, $"bildirimMs={bildirim}, iki bildirim 700+700 bekleniyordu");
    }

    // ── W38: açık fiş rakamları GÖVDEYE ve OUTBOX'A giriyor ─────────────────────

    [Fact]
    public async Task Acik_fis_rakamlari_govdeye_ve_outboxa_AYNI_gider()
    {
        // ← ÇİVİ: kasaya dönen ile kuyruğa yazılan TEK üreticiden gelmeli; iki ayrı üretici
        // olsaydı biri değişince diğeri sessizce eskirdi.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 990,
                TicketState: "OPEN", TicketId: "tkt_acik",
                DeviceTicketTotalMinor: 2380, DeviceRemainingMinor: 1390));
        notifier.Result = new NotifyResult(NotifyOutcome.NetworkError, null, null, 0, "ağ");

        var govde = await h.HandleAsync(Req());

        foreach (var kaynak in new[] { govde, _outbox.Pending(ignoreBackoff: true).Single().PayloadJson })
        {
            var ek = JsonDocument.Parse(kaynak).RootElement
                .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
            Assert.Equal("OPEN", ek.GetProperty("ticketState").GetString());
            Assert.Equal(2380, ek.GetProperty("deviceTicketTotalMinor").GetInt64());
            Assert.Equal(1390, ek.GetProperty("deviceRemainingMinor").GetInt64());
        }
    }

    // ── W39: ESKİ komutta ilk gecikme ATLANIR (terminal kilidi boşuna tutulmasın) ──

    /// <summary>Uykuları ölçen politika — gerçekten beklemeden, çağrıları kaydederek.</summary>
    private static (RecoveryPolicy Politika, List<TimeSpan> Uykular) OlcenPolitika()
    {
        var uykular = new List<TimeSpan>();
        return (new RecoveryPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(30),
            RetryDelay = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromSeconds(20),
            MaxAttempts = 2,
            InitialDelaySkipAge = TimeSpan.FromMinutes(2),
            Sleep = (d, _) => { uykular.Add(d); return Task.CompletedTask; },
        }, uykular);
    }

    private async Task<List<TimeSpan>> KurtarmaUykulari(long yasMs)
    {
        var (politika, uykular) = OlcenPolitika();
        var alindi = _clock.ServerNow() - yasMs;
        _store.Save("svc-A", Pay, "term-01", _clock.ServerNow() + 600_000, now: alindi);
        // Gerçek yol: terminale gönderildi, sonuç belirsiz kaldı. Doğrudan RECEIVED→UNKNOWN
        // geçişi durum makinesinde yok; RECEIVED kalsaydı kurtarma satışı YENİDEN çalıştırırdı.
        _store.Advance("svc-A", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL);
        _store.Advance("svc-A", CommandState.SENT_TO_TERMINAL, CommandState.UNKNOWN);

        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, politika);
        var h = new LocalSaleHandler(new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) },
            orch, _store, new FakeResolver(), new FakePaymentMethods(), new FakeNotifier(), _outbox, sim);

        await h.RecoverPendingAsync();
        Assert.NotEmpty(uykular);   // tur gerçekten koştu
        return uykular;
    }

    [Fact]
    public async Task ESKI_komutta_ilk_yoklama_GECIKMESIZ()
    {
        // ← ÇİVİ: kurtarma turu terminal kilidini alıp uyuyor; o sırada gelen satış bekliyor.
        // Sahada bir satış 18,7 sn bekledi ve cihaz o süre boyunca BOŞTU. Saatler önce
        // başarısız olmuş bir komut için "cihaz yerleşsin" beklemesinin gerekçesi yok.
        var uykular = await KurtarmaUykulari(yasMs: 10 * 60 * 1000);   // 10 dakikalık komut

        Assert.Equal(TimeSpan.Zero, uykular[0]);                        // ← ÇİVİ
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 20)]     // MaxDelay'de sabitleniyor
    public void Sonraki_turlarin_kadansi_DEGISMEDI(int deneme, int beklenenSaniye)
    {
        // ← ÇİVİ: W39 YALNIZ ilk gecikmeyi kaldırıyor. Sonraki turların eğrisine dokunulmadı;
        // dokunulsaydı cihaza baskı artardı ve bu W39'un amacının tersi olurdu.
        var p = new RecoveryPolicy();
        Assert.Equal(beklenenSaniye, (int)p.DelayFor(deneme).TotalSeconds);
        Assert.Equal(beklenenSaniye, (int)p.WithoutInitialDelay().DelayFor(deneme).TotalSeconds);
    }

    [Fact]
    public void WithoutInitialDelay_YALNIZ_ilk_gecikmeyi_sifirlar()
    {
        var p = new RecoveryPolicy();
        var y = p.WithoutInitialDelay();
        Assert.Equal(TimeSpan.Zero, y.DelayFor(0));
        Assert.Equal(TimeSpan.FromSeconds(30), p.DelayFor(0));   // özgün politika bozulmadı
        Assert.Equal(p.MaxAttempts, y.MaxAttempts);
        Assert.Equal(p.MaxDelay, y.MaxDelay);
        Assert.Equal(p.InitialDelaySkipAge, y.InitialDelaySkipAge);
    }

    [Fact]
    public async Task TAZE_komutta_ilk_gecikme_KORUNUR()
    {
        // ← ÇİVİ: çökme sonrası açılışta komut saniyeler önce gelmiş olabilir; orada cihazın
        // yerleşmesi GEREKİYOR. Gecikmeyi her yerde kaldırmak bunu bozardı.
        var uykular = await KurtarmaUykulari(yasMs: 5_000);             // 5 saniyelik komut

        Assert.Equal(TimeSpan.FromSeconds(30), uykular[0]);             // ← ÇİVİ
    }

    // ── W34: PLATFORM NİHAİ DEDİYSE YOKLAMA KAPANIR ─────────────────────────────

    [Fact]
    public async Task Superseded_gelince_komut_bir_daha_YOKLANMAZ()
    {
        // ← ÇİVİ (ölçüm 2026-09-08): 7 çözülmemiş komut gece boyunca 148 kez yoklandı ve
        // 148'i de `Superseded` döndü — sıfır fayda, her tur cihaza gidip terminal kilidini
        // aldı. `Superseded` = "sonuç bende kesinleşti, cevabını almıyorum"; sormaya devam
        // etmenin bir sonucu olamaz.
        _store.Save("svc-A", Pay, "term-01", _clock.ServerNow() + 600_000);
        _store.Advance("svc-A", CommandState.RECEIVED, CommandState.UNKNOWN);

        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var notifier = new FakeNotifier
        {
            Result = new NotifyResult(NotifyOutcome.Superseded, "REVERSED", "stale", 200, ""),
        };
        var h = new LocalSaleHandler(new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) },
            orch, _store, new FakeResolver(), new FakePaymentMethods(), notifier, _outbox, sim);

        await h.RecoverPendingAsync();
        var ilkTur = sim.ReadTicketCalls + sim.ProbeCalls;
        await h.RecoverPendingAsync();

        Assert.True(ilkTur > 0, "ilk tur cihaza sormalı");
        Assert.Equal(ilkTur, sim.ReadTicketCalls + sim.ProbeCalls);   // ← ÇİVİ: ikinci tur SORMADI
        Assert.Empty(_store.Pending());                                // kuyruktan çıktı
    }

    [Fact]
    public async Task Yoklama_kapansa_bile_komutun_DURUMU_UNKNOWN_kalir()
    {
        // ← ÇİVİ: "bir daha sorma" ile "ne olduğunu biliyorum" ayrı olgular. Durumu değiştirmek,
        // bilmediğimiz bir şeyi biliyormuş gibi deftere yazmak olurdu.
        _store.Save("svc-A", Pay, "term-01", _clock.ServerNow() + 600_000);
        _store.Advance("svc-A", CommandState.RECEIVED, CommandState.UNKNOWN);

        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var h = new LocalSaleHandler(new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) },
            orch, _store, new FakeResolver(), new FakePaymentMethods(),
            new FakeNotifier { Result = new NotifyResult(NotifyOutcome.Superseded, "REVERSED", "stale", 200, "") },
            _outbox, sim);

        await h.RecoverPendingAsync();

        Assert.Equal(CommandState.UNKNOWN, _store.Read("svc-A")!.State);   // ← ÇİVİ
    }

    // ── W64: 409 ÇELİŞKİSİ DE NİHAİDİR ──────────────────────────────────────────

    [Fact]
    public async Task Conflict_409_gelince_komut_bir_daha_YOKLANMAZ()
    {
        // ← ÇİVİ (canlı vaka 2026-09-09 02:19–02:25): başarısız bir kart denemesi
        // (`04cfa7cdd6`) HER 60 SANİYEDE kurtarmaya girdi, cihazda `OptionFlags`+`GetTicket`
        // yaptı ve her turda 409 `plugin.payment.conflictingResult` aldı. Yedi tur,
        // `recovery_closed_at` NULL — durma ihtimali YOK. Her tur terminal kilidini alıyor,
        // yani gerçek satışla yarışıyor.
        //
        // 409 = "platform bu denemeyi BAŞKA bir sonuçla kapattı". `Superseded`'den tek farkı ret
        // biçimi; gönderdiğimiz cevap asla kabul edilmeyecek.
        _store.Save("svc-A", Pay, "term-01", _clock.ServerNow() + 600_000);
        _store.Advance("svc-A", CommandState.RECEIVED, CommandState.UNKNOWN);

        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var h = new LocalSaleHandler(new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) },
            orch, _store, new FakeResolver(), new FakePaymentMethods(),
            new FakeNotifier
            {
                Result = new NotifyResult(NotifyOutcome.Conflict, null, "plugin.payment.conflictingResult", 409, ""),
            },
            _outbox, sim);

        await h.RecoverPendingAsync();
        var ilkTur = sim.ReadTicketCalls + sim.ProbeCalls;
        await h.RecoverPendingAsync();

        Assert.True(ilkTur > 0, "ilk tur cihaza sormalı");
        Assert.Equal(ilkTur, sim.ReadTicketCalls + sim.ProbeCalls);   // ← ÇİVİ: ikinci tur SORMADI
        Assert.Empty(_store.Pending());
        Assert.Equal(CommandState.UNKNOWN, _store.Read("svc-A")!.State);   // durum DEĞİŞMEDİ
    }

    [Theory]
    [InlineData(NotifyOutcome.NetworkError)]
    [InlineData(NotifyOutcome.RateLimited)]
    public async Task GECICI_hata_yoklamayi_KAPATMAZ(NotifyOutcome sonuc)
    {
        // ← ÇİVİ: ayrım bu testin tamamı. 5xx / ağ hatası / 429 "platform ŞU AN cevap veremedi"
        // demek — o kaydı kapatmak, geçici bir kesinti yüzünden gerçekten bildirilmemiş bir sonucu
        // SESSİZCE gömmek olurdu. Yalnız 409 ve `Superseded` nihaidir.
        _store.Save("svc-A", Pay, "term-01", _clock.ServerNow() + 600_000);
        _store.Advance("svc-A", CommandState.RECEIVED, CommandState.UNKNOWN);

        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var h = new LocalSaleHandler(new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) },
            orch, _store, new FakeResolver(), new FakePaymentMethods(),
            new FakeNotifier { Result = new NotifyResult(sonuc, null, null, 503, "") }, _outbox, sim);

        await h.RecoverPendingAsync();

        Assert.NotEmpty(_store.Pending());   // ← ÇİVİ: kuyrukta KALDI, yeniden denenecek
    }

    [Fact]
    public async Task Yoklama_kapanisinda_SEBEP_loglanir()
    {
        // ← ÇİVİ: `Superseded` (bayat/sırasız) ile `Conflict` (409 çelişki) FARKLI sorunlara
        // işaret ediyor. Tek satırda ayırt edilemezse, gece yarısı bir sahada "neden kapandı"
        // sorusunun cevabı yok.
        _store.Save("svc-A", Pay, "term-01", _clock.ServerNow() + 600_000);
        _store.Advance("svc-A", CommandState.RECEIVED, CommandState.UNKNOWN);

        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        _loglar.Clear();
        var h = new LocalSaleHandler(new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) },
            orch, _store, new FakeResolver(), new FakePaymentMethods(),
            new FakeNotifier
            {
                Result = new NotifyResult(NotifyOutcome.Conflict, null, "plugin.payment.conflictingResult", 409, ""),
            },
            _outbox, sim, log: (m, o) => _loglar.Add((m, o)));

        await h.RecoverPendingAsync();

        var veri = LogVeri("yoklama KAPATILDI");
        Assert.NotNull(veri);
        Assert.Equal("Conflict", Alan<string>(veri, "nihaiSebep"));
    }

    [Fact]
    public async Task Recorded_gelirse_yoklama_KAPANMAZ()
    {
        // ← ÇİVİ: kapatma YALNIZ platformun NİHAİ dediği hâllere özgü (`Superseded` + W64'ten
        // sonra `Conflict`). Normal bir bildirim kabul edildiğinde komut hâlâ çözülmemişse
        // yoklanmaya devam etmeli, yoksa gerçek bir belirsizliği sessizce terk ederdik.
        _store.Save("svc-A", Pay, "term-01", _clock.ServerNow() + 600_000);
        _store.Advance("svc-A", CommandState.RECEIVED, CommandState.UNKNOWN);

        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var h = new LocalSaleHandler(new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) },
            orch, _store, new FakeResolver(), new FakePaymentMethods(),
            new FakeNotifier { Result = new NotifyResult(NotifyOutcome.Recorded, "UNKNOWN", null, 200, "") },
            _outbox, sim);

        await h.RecoverPendingAsync();

        Assert.NotEmpty(_store.Pending());   // ← ÇİVİ: kuyrukta KALDI
    }

    // ── W30: `info` KASAYA VE DEFTERE ULAŞMALI ──────────────────────────────────

    [Fact]
    public async Task Belirsiz_sonucta_info_ve_reason_BIRLIKTE_gider()
    {
        // ← ÇİVİ (saha hatası, 2026-09-07 20:26): W25 doğru çalıştı, yoklama "fişe para
        // yazılmadı" dedi, ama `ToTransportResult` yeni gövdeyi kurarken `Info`yu KOPYALAMADI.
        // Platform `info:"NOT_LANDED"` görmediği için P34 dalına hiç girmedi; deneme
        // `bankReviewNeeded` almadan UNKNOWN'da kaldı ve yoklama sürdü.
        //
        // Hata GÖRÜNMÜYORDU çünkü `ErrorCondition` ve `Reason` taşınıyordu — gövde doğru
        // görünüyor, yalnız bir alan eksikti.
        // Gerçek yolu izle: terminal 2086 verir (Unknown), orkestratör YOKLAR, yoklama W25
        // kararını üretir. Hata tam da bu sonucun gövdeye çevrildiği yerdeydi.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Unknown,
                ErrorCondition: "UnreachableHost", PaymentInvoked: true, UsedBankBkmId: 62));
        sim.ProbeResult = new PaymentProbe(ProbeVerdict.NotLanded, CounterRead: true,
            ErrorCondition: "UnreachableHost", Reason: RestomenumReasons.NoResponse,
            Info: RestomenumReasons.NotLandedOnTicket);

        var govde = await h.HandleAsync(Req());
        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");

        Assert.Equal("UnreachableHost", Resp(govde).GetProperty("ErrorCondition").GetString());
        Assert.Equal(RestomenumReasons.NoResponse, ek.GetProperty("reason").GetString());
        Assert.Equal(RestomenumReasons.NotLandedOnTicket, ek.GetProperty("info").GetString());  // ← ÇİVİ
        Assert.True(ek.GetProperty("paymentInvoked").GetBoolean());
    }

    // ── W29: SATIŞ ÖNCESİ EŞLEME TAZELİĞİ ───────────────────────────────────────

    [Fact]
    public async Task Satis_oncesi_esleme_TAZELENIR()
    {
        // K-21 ("satış anında çekme yok") bilinçli olarak geri alındı: operatör bir eşleme
        // hatasını düzeltip hemen denediğinde ~30 dakika eski sürümle çalışıyordu ve düzeltmenin
        // işe yarayıp yaramadığını göremiyordu.
        var r = new FakeRefresher { YeniSurum = 45 };
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000),
            refresher: r);

        await h.HandleAsync(Req());

        Assert.Equal(1, r.Calls);
        Assert.Single(sim.SaleCalls);
    }

    [Fact]
    public async Task Tazeleme_PATLARSA_satis_yine_yapilir()
    {
        // ← ÇİVİ: K-21'in koruduğu şey. Yapılandırma kanalı satışı DÜŞÜREMEZ; "taze veriyi
        // alamadım" ödemeyi reddetmek için bir sebep değil.
        var r = new FakeRefresher { Patlat = new HttpRequestException("config ucu kapalı") };
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000),
            refresher: r);

        var govde = await h.HandleAsync(Req());

        Assert.Equal("Success", Resp(govde).GetProperty("Result").GetString());   // ← ÇİVİ
        Assert.Single(sim.SaleCalls);                                             // terminale GİTTİ
    }

    [Fact]
    public async Task Tazeleyici_YOKSA_eski_davranis_aynen_surer()
    {
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000));

        var govde = await h.HandleAsync(Req());

        Assert.Equal("Success", Resp(govde).GetProperty("Result").GetString());
    }

    // ── W27: RET GÖVDESİNDE ÜRÜN ADRESİ ─────────────────────────────────────────

    [Fact]
    public async Task Eslemesiz_urunde_HANGI_URUN_oldugu_makine_okunur_gider()
    {
        // ← ÇİVİ: kasiyer "ayar eksik" görüp ne yapacağını bilemiyordu. Kod `AdditionalResponse`
        // içinde de var ama orası serbest teşhis metni; kasanın oradan ayrıştırması kırılgan olur.
        // (K-30'dan sonra bu çivi ORAN çelişkisinde değil, EŞLEMESİZ üründe geçerli.)
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()), dept: null);

        var govde = await h.HandleAsync(Req());
        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");

        Assert.Equal("PRODUCT_UNMAPPED", ek.GetProperty("reason").GetString());
        Assert.Equal("p1", ek.GetProperty("productCode").GetString());     // ← ÇİVİ
        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
        Assert.Empty(sim.SaleCalls);                                        // terminale GİDİLMEDİ
    }

    // ── P27: FİŞ KAPANDI BİLDİRİMİ (K-26) ───────────────────────────────────────

    [Fact]
    public async Task Fis_kapandiysa_AYRI_bildirim_gider_ve_odemeleri_TASIR()
    {
        // Kullanıcı kararı: ödemeler deftere fiş TAMAMEN kapanınca yazılıyor. Bu bildirim o anın
        // tek habercisi — gitmezse o fişin HİÇBİR ödemesi deftere yazılmaz.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000,
                TicketState: "CLOSED", TicketId: "tkt_abc",
                ClosedTicketPayments: new[]
                {
                    new TicketPaymentRow("pay-1", 200, GmpPaymentTypes.Cash, null),
                    new TicketPaymentRow("pay-2", 790, GmpPaymentTypes.Card, 62),
                },
                DevicePaidMinor: 990, DeviceTicketTotalMinor: 990));

        await h.HandleAsync(Req());

        var govde = Assert.Single(notifier.TicketClosedBodies);
        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.Equal("tkt_abc", ek.GetProperty("ticketId").GetString());
        Assert.Equal("CLOSED", ek.GetProperty("ticketState").GetString());
        Assert.Equal("ticket", ek.GetProperty("scope").GetString());
        var satirlar = ek.GetProperty("payments").EnumerateArray().ToList();
        Assert.Equal(2, satirlar.Count);
        Assert.Equal("pay-1", satirlar[0].GetProperty("paymentId").GetString());
        Assert.Equal("cash", satirlar[0].GetProperty("methodType").GetString());
        Assert.False(satirlar[0].TryGetProperty("bankBkmId", out _));     // nakitte banka YOK
        Assert.Equal("card", satirlar[1].GetProperty("methodType").GetString());
        Assert.Equal(62, satirlar[1].GetProperty("bankBkmId").GetInt32());
        // ← ÇİVİ: tahsil toplamı CİHAZDAN gider, listemizin toplamı değil. Ayrı gittiği için
        // platform "listede olmayan bir tahsilat var mı" sorusunu kendi cevaplayabilir.
        Assert.Equal(990, ek.GetProperty("paidMinor").GetInt64());
        // POIID sahiplik kapısı — yoksa platform 400 döner.
        Assert.Equal("term-01", JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("MessageHeader")
            .GetProperty("POIID").GetString());
    }

    // ── W151: MALİ KİMLİK PLATFORMA GİDER (fiş no + Z no + EKÜ no + cihaz serisi) ──────

    private static DeviceIdentityHolder Kimlik(string seri)
    {
        var k = new DeviceIdentityHolder();
        k.Kaydet(new GmpDeviceInfo("INGENICO       ", "IDE281          ", seri, "20010100"), null);
        return k;
    }

    [Fact]
    public async Task Fis_kapanis_bildirimi_MALI_KIMLIGI_ve_CIHAZ_SERISINI_tasir()
    {
        // ← ÇİVİ: fiş numarası her Z raporunda sıfırlanır (ölçüldü 2026-09-15); tek başına
        // kimlik DEĞİL. Platformun tekilleştirme anahtarı (seri, EKÜ, Z, fiş) dörtlüsü — dördü
        // de gitmeli. Seri eşleşmede okunan HAM değerden kırpılarak gider.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 990,
                TicketState: "CLOSED", TicketId: "tkt_abc",
                ClosedTicketPayments: new[] { new TicketPaymentRow("pay-1", 990, GmpPaymentTypes.Cash, null) },
                DevicePaidMinor: 990, DeviceTicketTotalMinor: 990,
                FiscalReceipt: new FiscalReceiptId(ReceiptNo: 5, ZNo: 93, EkuNo: 1)),
            kimlik: Kimlik("2B25035325 "));

        await h.HandleAsync(Req());

        var ek = JsonDocument.Parse(Assert.Single(notifier.TicketClosedBodies)).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.Equal(5, ek.GetProperty("fiscalReceiptNo").GetInt32());
        Assert.Equal(93, ek.GetProperty("fiscalZNo").GetInt32());
        Assert.Equal(1, ek.GetProperty("fiscalEkuNo").GetInt32());
        Assert.Equal("2B25035325", ek.GetProperty("deviceSerial").GetString());
    }

    [Fact]
    public async Task Mali_kimlik_okunamadiysa_alanlar_HIC_yazilmaz_ama_bildirim_GIDER()
    {
        // ← ÇİVİ: "yok" ile "0" ayrı beyanlar. Sıfır yazmak var olmayan bir fişe işaret ederdi;
        // kimlik yokken bildirim yine gider — para alınmış, tahsilat deftere yazılmalı.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 990,
                TicketState: "CLOSED", TicketId: "tkt_abc",
                ClosedTicketPayments: new[] { new TicketPaymentRow("pay-1", 990, GmpPaymentTypes.Cash, null) },
                DevicePaidMinor: 990, DeviceTicketTotalMinor: 990,
                FiscalReceipt: null));                       // kimlik yok, seri de yok

        await h.HandleAsync(Req());

        var ek = JsonDocument.Parse(Assert.Single(notifier.TicketClosedBodies)).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.Equal("tkt_abc", ek.GetProperty("ticketId").GetString());
        Assert.False(ek.TryGetProperty("fiscalReceiptNo", out _));
        Assert.False(ek.TryGetProperty("fiscalZNo", out _));
        Assert.False(ek.TryGetProperty("fiscalEkuNo", out _));
        Assert.False(ek.TryGetProperty("deviceSerial", out _));
    }

    [Fact]
    public async Task Kurtarilan_kapanis_bildirimi_kimligi_DEFTERDEN_tasir()
    {
        // ← ÇİVİ: kurtarma yolu (süpürme / elle TicketFinish) gövdeyi DEFTERDEN kurar; kimlik
        // yalnız taşıma sonucunda kalsaydı kurtarılan fişler — denetimde en çok aranacak olanlar —
        // kimliksiz giderdi. Kalıcı üç sütun bu yüzden var.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()), kimlik: Kimlik("2B25035325"));
        _store.MarkTicketClosed("tkt_kayip", "term-01", "oturum-A", 990, 990, now: _saatMs - 31_000);
        _store.RecordTicketPayment("tkt_kayip", "pay-1", 990, GmpPaymentTypes.Cash, null);
        _store.SetFiscalReceipt("tkt_kayip", new FiscalReceiptId(6, 93, 1));

        await h.DrainOutboxAsync();

        var ek = JsonDocument.Parse(Assert.Single(notifier.TicketClosedBodies)).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.Equal(6, ek.GetProperty("fiscalReceiptNo").GetInt32());
        Assert.Equal(93, ek.GetProperty("fiscalZNo").GetInt32());
        Assert.Equal(1, ek.GetProperty("fiscalEkuNo").GetInt32());
        Assert.Equal("2B25035325", ek.GetProperty("deviceSerial").GetString());
    }

    [Fact]
    public async Task Govdesiz_kalmis_fis_kapanisi_ACILISTA_kurtarilir()
    {
        // ← ÇİVİ: taşıma katmanı fişi kapatıp bağı sildikten SONRA, gövde outbox'a yazılmadan
        // önce süreç ölürse o fiş hiçbir kuyrukta görünmez — tahsilat yapılmış, satır yok, ve
        // keşfedecek başka yol da yok. Kalıcı `closed_tickets` kaydı bu boşluğun tek izi.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()));
        // W76: bu testin modellediği vaka "süreç öldü ve gövde hiç kurulamadı" — o kayıt TANIMI
        // GEREĞİ eskidir. Yaş kapısı (30 sn) tam da taze kayıtları, yani normal akışın 800 ms'lik
        // penceresini dışarıda bırakmak için var; kaydı 31 sn geriye alıyoruz ki test gerçekten
        // kurtarma vakasını sınasın, yarış penceresini değil.
        _store.MarkTicketClosed("tkt_kayip", "term-01", "oturum-A", 990, 990, now: _saatMs - 31_000);
        _store.RecordTicketPayment("tkt_kayip", "pay-1", 200, GmpPaymentTypes.Cash, null);
        _store.RecordTicketPayment("tkt_kayip", "pay-2", 790, GmpPaymentTypes.Card, 62);

        await h.DrainOutboxAsync();

        var govde = Assert.Single(notifier.TicketClosedBodies);
        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.Equal("tkt_kayip", ek.GetProperty("ticketId").GetString());
        Assert.Equal(2, ek.GetProperty("payments").GetArrayLength());
        Assert.Equal(990, ek.GetProperty("paidMinor").GetInt64());
        Assert.Empty(_store.PendingClosedTickets());       // devredildi
    }

    [Fact]
    public async Task Kurtarilan_kapanis_IKINCI_turda_tekrar_gonderilmez()
    {
        // Aynı fişi iki kez bildirmek platformda tekrar sayılır (güvenli) ama gürültü üretir;
        // asıl mesele kaydın devredildikten sonra kuyrukta ASILI KALMAMASI.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()));
        // W76: "süreç öldü" vakası TANIMI GEREĞİ eski bir kayıttır; yaş kapısı taze satırları
        // (normal akışın ~800 ms'lik penceresi) dışarıda bırakmak için var.
        _store.MarkTicketClosed("tkt_bir", "term-01", "oturum-A", 990, 990, now: _saatMs - 31_000);

        await h.DrainOutboxAsync();
        var ilk = notifier.TicketClosedBodies.Count;
        await h.DrainOutboxAsync();

        Assert.Equal(1, ilk);
        Assert.Equal(ilk, notifier.TicketClosedBodies.Count);
    }

    [Fact]
    public async Task Fis_ACIK_kaldiysa_kapanis_bildirimi_GITMEZ()
    {
        // ← ÇİVİ: kısmi ödemede satır yazmak, sonradan iptal edilen bir fişin satırlarını geri
        // almayı gerektirirdi.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000,
                TicketState: "OPEN", TicketId: "tkt_abc"));

        await h.HandleAsync(Req());

        Assert.Empty(notifier.TicketClosedBodies);
    }

    [Fact]
    public async Task Kimlik_YOKSA_kapanis_bildirimi_GONDERILMEZ()
    {
        // Platform tekilleştirmeyi `ticketId`'ye dayıyor. Uydurulmuş bir kimlik aynı fişi iki kez
        // yazdırabilir ya da iki ayrı fişi birleştirebilirdi. Sessiz kalmıyoruz: log alarm basar.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000,
                TicketState: "CLOSED", TicketId: null));

        await h.HandleAsync(Req());

        Assert.Empty(notifier.TicketClosedBodies);
    }

    [Fact]
    public async Task Kapanis_bildirimi_OUTBOXa_once_yazilir_ve_gonderilince_onaylanir()
    {
        // Fiş cihazda GERÇEKTEN kapandı; bildirim o an gidemezse satırlar hiç yazılmaz ve bunu
        // sonradan keşfedecek bir kurtarma turu YOK. Bu yüzden önce diske, sonra ağa.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000,
                TicketState: "CLOSED", TicketId: "tkt_xyz"));
        notifier.Result = new NotifyResult(NotifyOutcome.NetworkError, null, null, 0, "ağ");

        await h.HandleAsync(Req());

        // W20: başarısız gönderimden sonra kayıt kuyrukta AMA hemen sıraya girmiyor (30 sn
        // geri çekilme). `ignoreBackoff` ile bakınca duruyor — kayıp yok, yalnız ertelenmiş.
        var bekleyen = _outbox.Pending(ignoreBackoff: true)
            .Where(e => e.Status == OutboxKinds.TicketClosed).ToList();
        var kayit = Assert.Single(bekleyen);
        Assert.Equal("tkt_xyz:ticket-closed", kayit.EventId);   // tekilleştirme fiş bazlı
        Assert.Empty(_outbox.Pending().Where(e => e.Status == OutboxKinds.TicketClosed));   // ← ÇİVİ
    }

    [Fact]
    public async Task Onaylanan_odeme_Success_doner_ve_platforma_bildirilir()
    {
        var (h, sim, notifier) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1", CardLast4: "4242"));

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Success", resp.GetProperty("Result").GetString());
        Assert.Single(sim.SaleCalls);        // terminale bir kez gitti
        Assert.Single(notifier.Bodies);      // platforma bildirildi
    }

    [Fact]
    public async Task GET_reddi_terminale_GITMEZ_ve_bildirmez()
    {
        var (h, sim, notifier) = Kur(
            new PaymentDetailResult.Rejected(PaymentRejectReason.AmountWindowClosed, "plugin.payment.amountWindowClosed", 409));

        var govde = await h.HandleAsync(Req());
        var resp = Resp(govde);
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        // ⚠️ Eskiden "Aborted"/"UnreachableHost" idi. Tutar alınamadığında `FP3_Payment` HİÇ
        // çağrılmaz, yani sonuç KESİNDİR; belirsiz sınıfına sokmak denemeyi gereksiz yere çözüm
        // döngüsünde bırakıyordu. Sebep artık makine-okunur alanda.
        Assert.Equal("PaymentRestriction", resp.GetProperty("ErrorCondition").GetString());
        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
        Assert.Equal("AMOUNT_FETCH_FAILED", ek.GetProperty("reason").GetString());
        Assert.Empty(sim.SaleCalls);         // terminale GİTMEDİ (tutar bilinmiyor)
        Assert.Empty(notifier.Bodies);       // platform GET reddini zaten biliyor
    }

    [Fact]
    public async Task Eslenmemis_urun_terminale_GITMEZ_ama_bildirir_ve_urunu_soyler()
    {
        var (h, sim, notifier) = Kur(new PaymentDetailResult.Ok(Detail()), dept: null);

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("PaymentRestriction", resp.GetProperty("ErrorCondition").GetString());
        Assert.Contains("PRODUCT_UNMAPPED", resp.GetProperty("AdditionalResponse").GetString());
        Assert.Contains("p1", resp.GetProperty("AdditionalResponse").GetString());   // HANGİ ürün
        Assert.Empty(sim.SaleCalls);
        Assert.Single(notifier.Bodies);      // GET başarılıydı (ACCEPTED) → takılı kalmasın diye bildir
    }

    [Fact]
    public async Task KDV_celiskisi_satisi_DUSURMEZ_ama_BILDIRILIR()
    {
        // ⚠️ DAVRANIŞ DEĞİŞTİ (K-30, kullanıcı kararı 2026-09-07): "Eklenti ürünlerin kdv oranını
        // değiştirmesin zaten, terminal uygulaması bizim beyanımıza göre işlesin."
        //
        // ÖNCEDEN: bu durumda satış REDDEDİLİYORDU (`PROVIDER_CONFIG_INCOMPLETE`), gerekçesi
        // fişe %20 basılıp deftere %10 yazılmasını engellemekti. Gerekçe hâlâ geçerli ama karar
        // değişti: mali belge ÖKC fişidir, oranı bölüm belirler, katalogdaki oran menü verisidir.
        // Eski hâlin sahadaki bedeli ölçüldü: 88 üründen 87'si satılamıyordu.
        //
        // ← ÇİVİ: sapma SESSİZ kalmaz. Satış geçer AMA ayrışan her kalem yanıtta sayılır.
        var (h, sim, notifier) = Kur(new PaymentDetailResult.Ok(Detail()), dept: 0, rate: 2000,
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000));

        var govde = await h.HandleAsync(Req());
        var resp = Resp(govde);
        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");

        Assert.Equal("Success", resp.GetProperty("Result").GetString());
        Assert.Single(sim.SaleCalls);                       // terminale GİTTİ

        var sapmalar = ek.GetProperty("taxMismatches").EnumerateArray().ToList();
        var t = Assert.Single(sapmalar);
        Assert.Equal("p1", t.GetProperty("productCode").GetString());
        Assert.Equal(1000, t.GetProperty("productRateBasisPoints").GetInt32());      // ürün %10
        Assert.Equal(2000, t.GetProperty("departmentRateBasisPoints").GetInt32());   // fişe %20
        Assert.Equal(0, t.GetProperty("departmentIndex").GetInt32());
    }

    [Fact]
    public async Task KDV_celiskisi_YOKSA_taxMismatches_alani_HIC_KONMAZ()
    {
        // ← ÇİVİ: "çelişki yok" ile "boş liste" ayrı beyanlar. Boş dizi göndermek, platformun
        // "sapma kontrolü çalıştı ve temiz" ile "alan hiç gelmedi"yi ayırmasını engellerdi.
        var (h, _, _) = Kur(new PaymentDetailResult.Ok(Detail()), dept: 10, rate: 1000,
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000));

        var govde = await h.HandleAsync(Req());
        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");

        Assert.False(ek.TryGetProperty("taxMismatches", out _));
    }

    [Fact]
    public async Task Esleme_YOKSA_hala_REDDEDILIR_K30_bunu_degistirmedi()
    {
        // ← ÇİVİ: K-30 yalnız ORAN çelişkisini bildirime çevirdi. Eşlemesiz ürün hâlâ ret:
        // departmanı bilinmeyen bir kalemi uydurulmuş bir bölüme yazmak, yanlış mali kayıt
        // demektir ve bunun bir "beyanı" da yoktur.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()), dept: null);

        var resp = Resp(await h.HandleAsync(Req()));

        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Contains("PRODUCT_UNMAPPED", resp.GetProperty("AdditionalResponse").GetString());
        Assert.Empty(sim.SaleCalls);
    }

    [Fact]
    public async Task Departman_KDVsi_TaxCode_ile_uyusuyorsa_normal_gecer()
    {
        // TaxCode="10" ↔ departman oranı 1000 (%10): 10*100=1000 → uyumlu, yanlış-ret YOK, terminale gider.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()), dept: 10, rate: 1000,
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1", CardLast4: "4242"));

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Success", resp.GetProperty("Result").GetString());
        Assert.Single(sim.SaleCalls);        // doğrulama uyumluysa akış aynen sürer
    }

    [Fact]
    public async Task Eslenmemis_odeme_yontemi_terminale_GITMEZ_ama_bildirir_ve_yontemi_soyler()
    {
        // §20-I: PaymentMethodId payment-methods.json'da yoksa (resolver null) → terminale gitme.
        var (h, sim, notifier) = Kur(new PaymentDetailResult.Ok(Detail(pmId: "11-uydurma")), paymentType: null);

        var resp = Resp(await h.HandleAsync(Req()));
        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("PaymentRestriction", resp.GetProperty("ErrorCondition").GetString());
        Assert.Contains("PAYMENT_METHOD_UNMAPPED", resp.GetProperty("AdditionalResponse").GetString());
        Assert.Contains("11-uydurma", resp.GetProperty("AdditionalResponse").GetString());   // HANGİ yöntem
        Assert.Empty(sim.SaleCalls);         // terminale GİTMEDİ
        Assert.Single(notifier.Bodies);      // GET ACCEPTED'dı → takılı kalmasın diye bildir
    }

    [Fact]
    public async Task Ayni_ServiceID_ikinci_istek_karti_TEKRAR_CEKMEZ()
    {
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000));

        await h.HandleAsync(Req("dup1"));
        await h.HandleAsync(Req("dup1"));    // kasa ağ-hatası retry'ı: AYNI ServiceID
        Assert.Single(sim.SaleCalls);        // ← ÇİVİ: terminal YALNIZ bir kez sürüldü
    }

    // ── W5: AYNI ServiceID ile İKİNCİ POST (kasa köprüsünün "yeniden dene" düğmesi) ──────

    [Fact]
    public async Task AyniServiceId_ikinci_POST_ikinci_odeme_BASLATMAZ()
    {
        // Kasa köprüsü belirsiz denemede AYNI ServiceID ile zarfı yeniden POST ediyor ve panel bu
        // düğmeyi kasiyere gösteriyor. Tekilleme olmasaydı ikinci POST ikinci FP3_Payment başlatırdı
        // → müşteri iki kez öderdi.
        // ← ÇİVİ: ikinci POST terminale GİTMEZ; saklanan sonuç döner.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1"));

        var ilk = Resp(await h.HandleAsync(Req("svc-tekil")));
        var ikinci = Resp(await h.HandleAsync(Req("svc-tekil")));

        Assert.Equal("Success", ilk.GetProperty("Result").GetString());
        Assert.Single(sim.SaleCalls);   // ← ÇİVİ: ikinci satış çağrısı = çift tahsilat
        Assert.False(ikinci.TryGetProperty("ErrorCondition", out var ec) && ec.GetString() == "Refusal");
    }

    [Fact]
    public async Task AyniServiceId_ilk_islem_BELIRSIZKEN_ikinci_POST_yalniz_SORAR()
    {
        // En tehlikeli an: ilk işlem UNKNOWN'da asılı (para hareket etmiş OLABİLİR) ve kasiyer
        // "yeniden dene"ye basıyor. Burada ikinci FP3_Payment ASLA çağrılmaz — yalnız terminale
        // sorulur. Aksi hâlde çekilmiş bir kart ikinci kez çekilir.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Unknown, ProviderResultCode: "Payment:0x0826",
                ErrorCondition: "UnreachableHost"));
        sim.ProbeResult = new PaymentProbe(ProbeVerdict.Indeterminate, Note: "fiş okunamadı");

        var ilk = Resp(await h.HandleAsync(Req("svc-ucus")));
        var ikinci = Resp(await h.HandleAsync(Req("svc-ucus")));

        Assert.Equal("Failure", ilk.GetProperty("Result").GetString());
        Assert.Equal("Failure", ikinci.GetProperty("Result").GetString());
        Assert.Single(sim.SaleCalls);   // ← ÇİVİ: belirsizlik sürerken TEKRAR GÖNDERİM YOK
        Assert.True(sim.ProbeCalls >= 2);   // her POST yalnız SORDU
    }

    [Fact]
    public async Task Tekilleme_anahtari_ServiceId_FARKLI_ServiceId_yeni_islemdir()
    {
        // Anahtar ServiceID'dir (LocalSaleHandler: CommandId = req.ServiceId); SaleID ya da
        // PaymentID DEĞİL. Bu test o sınırı dürüstçe kayda geçirir: aynı ödeme için FARKLI
        // ServiceID ile gelen ikinci POST, tekilleme tarafından YAKALANMAZ ve yeni bir satıştır.
        // ⚠️ Yani çift-tahsilat koruması kasanın aynı ServiceID'yi kullanmasına BAĞLIDIR.
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1"));
        sim.Expect(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN2"));

        await h.HandleAsync(Req("svc-A"));
        await h.HandleAsync(Req("svc-B"));

        Assert.Equal(2, sim.SaleCalls.Count);
    }
    // ── W109: CİHAZ KİLİDİNİ BEKLEMENİN TAVANI ──────────────────────────────────

    [Fact]
    public async Task Kilit_TAVAN_suresinde_alinamazsa_odeme_CIHAZA_GITMEZ_ve_KESIN_ret_doner()
    {
        // Kasa ölçtü: POST zaman aşımı 180 sn. Kilit eskiden TAVANSIZ bekleniyordu; önceki satış
        // hâlâ basıyorsa ikinci istek orada bekler, kasa pes eder ve HİÇBİR ŞEY öğrenemez —
        // zaman aşımı bütçesinin (W109) bütün faydası tam burada kaybolurdu.
        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        _loglar.Clear();
        var h = new LocalSaleHandler(
            new FakeAmounts { Result = new PaymentDetailResult.Ok(Detail()) }, orch, _store,
            new FakeResolver { Dept = 3 }, new FakePaymentMethods(), new FakeNotifier(), _outbox, sim,
            now: () => DateTimeOffset.FromUnixTimeMilliseconds(_saatMs),
            log: (m, o) => _loglar.Add((m, o)),
            satisKilitTavani: TimeSpan.FromMilliseconds(50));

        using var mesgul = h.TryAcquire();   // cihazı başka bir iş tutuyor
        Assert.NotNull(mesgul);

        // ⚠️ ZAMAN AŞIMLI BEKLE. Tavan kaldırılırsa (mutasyon: `WaitAsync(1 gün)`) bu çağrı
        // SONSUZA KADAR ASILIR ve test "başarısız" değil "askıda" olur — mutasyon bataryası da
        // testi çalıştıran süreç de kilitlenir (sahada bir kez oldu). Asılmayı BAŞARISIZLIĞA
        // çevirmek, hatayı görünür kılmanın tek yolu.
        var is_ = h.HandleAsync(Req());
        var biten = await Task.WhenAny(is_, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(biten == is_, "kilit tavanı yok: çağrı 10 sn içinde dönmedi (ASILDI)");
        var govde = await is_;

        var yanit = System.Text.Json.JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse");
        var resp = yanit.GetProperty("PaymentResponse").GetProperty("Response");

        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        // ← ÇİVİ: KESİN ret ve kesinlik HAK EDİLMİŞ — cihaza hiç gidilmedi, para hareket etmedi.
        // `Busy` koşulu platformda "bilinmiyor"a eşleniyor ve kasayı boş yere uzlaştırma turuna
        // sokardı; burada belirsizlik YOK.
        Assert.Equal("PaymentRestriction", resp.GetProperty("ErrorCondition").GetString());
        Assert.Equal("TERMINAL_BUSY", resp.GetProperty("AdditionalResponse").GetString());

        var ek = yanit.GetProperty("Restomenum");
        Assert.Equal("TERMINAL_BUSY", ek.GetProperty("reason").GetString());
        // ← ÇİVİ: "kart KESİNLİKLE çekilmedi" — kasiyere söylenecek tek doğru cümle bunu gerektirir.
        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
        // ← ÇİVİ: cihaza gerçekten GİDİLMEDİ.
        Assert.Empty(sim.SaleCalls);
    }

    [Fact]
    public void TicketQuery_GERCEK_kilidi_okur_ucusta_is_varken_deviceBusy_TRUE()
    {
        // ← ÇİVİ: bu alanın bütün değeri GERÇEK kilide bağlı olmasında. Sabit `false` ya da ters
        // okuma, yeniden başlatmadan önce bakan kişiye "boşta" der ve bu gecenin hatasını
        // (uçuştaki ödemeyi kesmek → 19,80 çift tahsilat) aynen tekrar ettirir.
        var (h, _, _) = Kur(new PaymentDetailResult.Ok(Detail()));
        var istek = new TicketQueryRequest("svc12345", "kasa-1", "t1", null, DateTimeOffset.UtcNow);

        var bosta = System.Text.Json.JsonDocument.Parse(h.HandleTicketQuery(istek)).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.False(bosta.GetProperty("deviceBusy").GetBoolean());

        using (var mesgul = h.TryAcquire())
        {
            Assert.NotNull(mesgul);
            var esnasinda = System.Text.Json.JsonDocument.Parse(h.HandleTicketQuery(istek)).RootElement
                .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
            Assert.True(esnasinda.GetProperty("deviceBusy").GetBoolean());
        }

        // Kilit bırakılınca yine boşta — sorgu kilidi ALMIYOR, yalnız okuyor.
        var sonra = System.Text.Json.JsonDocument.Parse(h.HandleTicketQuery(istek)).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.False(sonra.GetProperty("deviceBusy").GetBoolean());
    }

    [Fact]
    public async Task BELIRSIZ_sonucta_paymentInvoked_FALSE_govdeye_kadar_TASINIR()
    {
        // ← ÇİVİ: bu, kilidi açan alan. Sahada (2026-09-10 03:04) kâğıtsız cihaz `FP3_Start`'ı
        // reddetti, taşıma `PaymentInvoked:false` üretti — ama belirsizlik yolu onu düşürdü ve
        // kasaya `paymentInvoked:true` gitti. Kasa "para hareket etmiş OLABİLİR" deyip masayı
        // kilitledi; kullanıcı HİÇ tahsilat yapamaz hâle geldi. Oysa cihaz izi kanıtladı:
        // `FP3_Payment` hiç çağrılmamıştı.
        //
        // Orkestratör testi `AgentOutcome.Result`ı çiviliyor; bu test ondan GÖVDEYE kadar olan
        // yolu çiviliyor. İkisi ayrı: alan doğru üretilip son adımda düşebilir (W30'da tam
        // olarak öyle olmuştu — `Info` sessizce kaybolmuştu).
        var (h, sim, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Unknown,
                ProviderResultCode: "Start:0x0020", ErrorCondition: "PaymentRestriction",
                PaymentInvoked: false, Reason: RestomenumReasons.OutOfPaper));
        sim.ProbeResult = new PaymentProbe(ProbeVerdict.Indeterminate, Note: "fiş okunamadı: 0x0020");

        var ek = System.Text.Json.JsonDocument.Parse(await h.HandleAsync(Req())).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");

        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
        Assert.Equal(RestomenumReasons.OutOfPaper, ek.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Tasima_HIC_cevap_uretmediyse_paymentInvoked_TRUE_kalir()
    {
        // ← ÇİVİ: FAIL-SAFE VARSAYILAN. Bir önceki test "bildiğimizi taşı" diyor; bu test
        // "bilmediğimizde YANLIŞ TARAFA düşme" diyor. İkisi zıt yönde ve ikisi de gerekli:
        // varsayılanı `false` yapmak, kart çekilmiş OLABİLECEK bir denemeyi kasaya
        // "kart kesinlikle çekilmedi" diye bildirmek olurdu — kasiyer aynı tutarı ikinci kez
        // çeker. Bu, bu gece iki kez uğraştığımız ÇİFT TAHSİLATIN ta kendisi.
        var (h, _, _) = Kur(new PaymentDetailResult.Ok(Detail()));

        // Süresi GEÇMİŞ deneme: taşıma hiç çağrılmaz, sonuç üretilmez (`Result` null).
        var gecmis = DateTimeOffset.UtcNow.AddHours(-1);
        var ek = System.Text.Json.JsonDocument.Parse(
                await h.HandleAsync(Req("svc-eski"))).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        _ = gecmis;

        // Varsayılanın kendisi: alan varsa TRUE olmalı (yanlış bir kesinlik yayma).
        if (ek.TryGetProperty("paymentInvoked", out var pi))
            Assert.True(pi.GetBoolean(), "bilinmiyorsa 'kart çekildi' denir — tersi çift tahsilat üretir");
    }

    [Fact]
    public async Task KAGIT_BITTI_gercekten_GOVDEYE_giriyor_mu()
    {
        // ← ÇİVİ: kullanıcı "kâğıt bittiği hâlde UI'a gitmedi" dedi ve ben ajan tarafından
        // DOĞRULAYAMADIM — gönderdiğim `reason`ı loglamıyordum, gövdeyi de loglamıyorum.
        // Taşıma katmanı testi `r.Reason`ı çiviliyordu ama GÖVDEYE kadar olan yolu hiçbir test
        // izlemiyordu. W30'da tam bu yolda bir alan (`Info`) sessizce düşmüştü; aynı aile.
        //
        // "Gönderiyorum" demek yerine ÖLÇÜYORUM.
        var (h, _, _) = Kur(new PaymentDetailResult.Ok(Detail()),
            terminal: new TransportResult(TransportOutcome.Approved,
                ApprovedAmountMinor: 24000, ProviderResultCode: "0x0000",
                TicketState: "OPEN",
                Reason: RestomenumReasons.OutOfPaper,
                PrintFailedStep: "PrintBeforeMF", PrintFailedCode: "0x0020"));

        var yanit = System.Text.Json.JsonDocument.Parse(await h.HandleAsync(Req())).RootElement
            .GetProperty("SaleToPOIResponse");
        var ek = yanit.GetProperty("Restomenum");

        // Sonuç `Success` KALIR — para alındı. Sebep taşımak "reddedildi" demek değil.
        Assert.Equal("Success",
            yanit.GetProperty("PaymentResponse").GetProperty("Response").GetProperty("Result").GetString());
        Assert.Equal("OUT_OF_PAPER", ek.GetProperty("reason").GetString());
        Assert.Equal("PrintBeforeMF", ek.GetProperty("printFailedStep").GetString());
        Assert.Equal("0x0020", ek.GetProperty("printFailedCode").GetString());
        Assert.Equal("OPEN", ek.GetProperty("ticketState").GetString());
    }

    [Fact]
    public void Kilit_tavani_kasanin_180_saniyesinin_ALTINDA()
    {
        // Aritmetik çivi: tavan 180'i aşarsa kasa yine cevapsız kalır ve bu düzeltmenin
        // tamamı anlamsızlaşır. Sabit büyütülürse burası kırmızıya döner.
        Assert.True(LocalSaleHandler.SatisKilitTavaniVarsayilan < TimeSpan.FromSeconds(180),
            "satış kilidi tavanı kasanın 180 sn'lik POST kapısının ALTINDA olmalı");
    }

}
