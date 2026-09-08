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
        public Task<PaymentDetailResult> FetchAsync(string p, CancellationToken ct = default) => Task.FromResult(Result);
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
        public Task<NotifyResult> NotifyAsync(string p, string body, CancellationToken ct = default)
        { Bodies.Add(body); return Task.FromResult(Result); }
        public Task<NotifyResult> NotifyTicketCancelAsync(string body, CancellationToken ct = default)
        { Bodies.Add(body); return Task.FromResult(Result); }
        public List<string> TicketClosedBodies { get; } = new();
        public Task<NotifyResult> NotifyTicketClosedAsync(string body, CancellationToken ct = default)
        { TicketClosedBodies.Add(body); return Task.FromResult(Result); }
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

    private (LocalSaleHandler, SimulatorTransport, FakeNotifier) Kur(
        PaymentDetailResult amounts, int? dept = 3, TransportResult? terminal = null, int? rate = null,
        int? paymentType = GmpPaymentTypes.Card, IMappingRefresher? refresher = null)
    {
        var sim = new SimulatorTransport();
        if (terminal is not null) sim.Expect(terminal);
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var notifier = new FakeNotifier();
        var h = new LocalSaleHandler(new FakeAmounts { Result = amounts }, orch, _store,
            new FakeResolver { Dept = dept, Rate = rate }, new FakePaymentMethods { Type = paymentType },
            notifier, _outbox, sim, mappingRefresher: refresher);
        return (h, sim, notifier);
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

    [Fact]
    public async Task Recorded_gelirse_yoklama_KAPANMAZ()
    {
        // ← ÇİVİ: kapatma YALNIZ `Superseded`'a özgü. Normal bir bildirim kabul edildiğinde
        // komut hâlâ çözülmemişse yoklanmaya devam etmeli, yoksa gerçek bir belirsizliği
        // sessizce terk ederdik.
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

    [Fact]
    public async Task Govdesiz_kalmis_fis_kapanisi_ACILISTA_kurtarilir()
    {
        // ← ÇİVİ: taşıma katmanı fişi kapatıp bağı sildikten SONRA, gövde outbox'a yazılmadan
        // önce süreç ölürse o fiş hiçbir kuyrukta görünmez — tahsilat yapılmış, satır yok, ve
        // keşfedecek başka yol da yok. Kalıcı `closed_tickets` kaydı bu boşluğun tek izi.
        var (h, _, notifier) = Kur(new PaymentDetailResult.Ok(Detail()));
        _store.MarkTicketClosed("tkt_kayip", "term-01", "oturum-A", 990, 990);
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
        _store.MarkTicketClosed("tkt_bir", "term-01", "oturum-A", 990, 990);

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
}
