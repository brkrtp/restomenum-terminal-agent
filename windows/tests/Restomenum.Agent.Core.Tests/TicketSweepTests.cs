using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>W105 — CİHAZDA YARIM KALMIŞ FİŞİ OTOMATİK TAMAMLAMA TURU.</b>
///
/// <para>Var olma sebebi tek bir olay: 2026-09-09 23:07'de baskı patladı, fiş 990 kuruş gerçek
/// tahsilatla açık kaldı ve <b>83 dakika</b> öyle durdu — ajan bütün o süre boyunca sorunsuz
/// çalışıyordu, çünkü o fişi tekrar deneyecek hiçbir mekanizma yoktu.</para>
///
/// <para>Bu tur gözetimsiz çalışacak ve para taşıyan bir cihaza dokunacak; üç güvenlik şartının
/// her biri burada ayrı ayrı çivilenmiştir.</para>
/// </summary>
public class TicketSweepTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"sweep_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;
    private readonly Outbox _outbox;
    private readonly ClockOffset _clock = new();
    private readonly List<(string Mesaj, object? Veri)> _loglar = new();
    private long _saatMs = 1_700_000_000_000;

    public TicketSweepTests()
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

    /// <summary>
    /// Tamamlama yeteneği olan sahte taşıma — kaç kez ve NASIL çağrıldığını sayar.
    /// <c>SimulatorTransport</c> <c>sealed</c> olduğu için türetilmiyor, SARMALANIYOR.
    /// </summary>
    private sealed class SahteTamamlayici : ITerminalTransport, IStuckTicketFinisher
    {
        private readonly SimulatorTransport _ic = new();
        public List<bool> Cagrilar { get; } = new();   // her çağrının `uygula` değeri
        public StuckTicketResult Sonuc = new(true, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            TicketFlags.TotalsAndPaymentsPrinted, ToplamMinor: 990, TahsilMinor: 990);

        /// <summary>Süpürmenin geçirdiği bütçe — cihazı ne kadar tutabileceğinin çivisi.</summary>
        public BaskiButcesi? SonButce;
        /// <summary>Doluysa çağrı bunu FIRLATIR — istisna yolunun aralığını ölçmenin tek yolu.</summary>
        public Exception? Patlat;
        /// <summary>Çağrı SIRASINDA çalışır — testin sahte saati ilerletip denemeyi PAHALI
        /// yapmasının tek yolu (gerçekten 15 saniye beklemek yerine).</summary>
        public Action? Sirasinda;
        public StuckTicketResult FinishStuckTicket(bool uygula, BaskiButcesi? butce = null)
        {
            Cagrilar.Add(uygula); SonButce = butce;
            Sirasinda?.Invoke();
            if (Patlat is not null) throw Patlat;
            return Sonuc;
        }

        public Task<TransportResult> SaleAsync(SaleRequest r, CancellationToken ct = default) => _ic.SaleAsync(r, ct);
        public Task<TicketState> ReadTicketAsync(CancellationToken ct = default) => _ic.ReadTicketAsync(ct);
        public Task<PaymentProbe> ProbeAsync(SaleRequest r, CancellationToken ct = default) => _ic.ProbeAsync(r, ct);
        public Task<bool> EchoAsync(CancellationToken ct = default) => _ic.EchoAsync(ct);
        public Task<TransportResult> VoidAsync(CancellationToken ct = default) => _ic.VoidAsync(ct);
        public Task<TicketVoidResult> VoidTicketAsync(string t, CancellationToken ct = default)
            => _ic.VoidTicketAsync(t, ct);
    }

    private (LocalSaleHandler, SahteTamamlayici) Kur()
    {
        var tasima = new SahteTamamlayici();
        var orch = new AgentOrchestrator(_store, tasima, _clock, RecoveryPolicy.Immediate);
        var h = new LocalSaleHandler(new SahteTutar(), orch, _store, new SahteBolum(),
            new SahteYontem(), new SahteBildirici(), _outbox, tasima,
            now: () => DateTimeOffset.FromUnixTimeMilliseconds(_saatMs),
            log: (m, o) => _loglar.Add((m, o)));
        return (h, tasima);
    }

    private object? LogVeri(string parca) => _loglar.LastOrDefault(l => l.Mesaj.Contains(parca)).Veri;
    private bool LogVar(string parca) => _loglar.Any(l => l.Mesaj.Contains(parca));
    private static T? Alan<T>(object? veri, string ad) =>
        veri is null ? default : (T?)veri.GetType().GetProperty(ad)?.GetValue(veri);

    // ── ZAMANLAMA: BEKLEME ŞARTI ────────────────────────────────────────────────

    [Fact]
    public async Task Isaretlenen_fise_HEMEN_gidilmez_once_beklenir()
    {
        var (h, tasima) = Kur();
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", "PrintBeforeMF", "0xF003",
            ilkBeklemeSn: 15, now: _saatMs);

        await h.DrainOutboxAsync();

        // ← ÇİVİ (şart ①): cihaz baskı adımından hemen sonra hâlâ meşgul olabilir; saniyesinde
        // üstüne gitmek aynı duvara toslamaktır. Fiş zaten açık ve bekliyor — acele kazanç yok.
        Assert.Empty(tasima.Cagrilar);
    }

    [Fact]
    public async Task Bekleme_dolunca_fis_TAMAMLANIR_ve_kayit_duser()
    {
        var (h, tasima) = Kur();
        _store.BindOpenTicket("t1", "oturum-A");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", "PrintBeforeMF", "0xF003",
            ilkBeklemeSn: 15, now: _saatMs);
        _saatMs += 16_000;

        await h.DrainOutboxAsync();

        Assert.Equal(new[] { true }, tasima.Cagrilar);   // ← KURU PROVA DEĞİL: gerçekten uygular
        // ← ÇİVİ: kasa beklemiyor ama CİHAZ KİLİTLİ. Bütçesiz bir deneme cihazı 310 sn'ye kadar
        // tutar ve o sırada gelen satış kilit tavanını doldurup `TERMINAL_BUSY` alır.
        Assert.NotNull(tasima.SonButce);
        Assert.Empty(_store.PendingFinishes(_saatMs));
        Assert.True(LogVar("yarım kalmış fiş TAMAMLANDI"));
    }

    [Fact]
    public async Task Zamani_gelmemis_kayit_TURU_TETIKLEMEZ()
    {
        var (h, tasima) = Kur();
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null,
            ilkBeklemeSn: 300, now: _saatMs);
        _saatMs += 60_000;   // 5 dakikanın dolmasına daha var

        await h.DrainOutboxAsync();

        // ← ÇİVİ: geri çekilme sorgunun İÇİNDE. Zamanı gelmemiş kaydı döndürseydik üstel
        // erteleme hiçbir işe yaramaz, her turda cihaza gidilirdi.
        Assert.Empty(tasima.Cagrilar);
    }

    [Fact]
    public async Task SATIS_SURERKEN_cihaza_GIDILMEZ_tur_atlanir()
    {
        var (h, tasima) = Kur();
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        // Cihaz kilidini bir SATIŞ tutuyormuş gibi al (bakım işleri aynı kilidi kullanır).
        using var satis = h.TryAcquire();
        Assert.NotNull(satis);

        await h.DrainOutboxAsync();

        // ← ÇİVİ (şart ①): SATIŞ HER ZAMAN ÖNCELİKLİ. Beklemek de yanlış olurdu — kuyruğa
        // girip kasiyerin ödemesini geciktirirdi. Atlamak güvenli: kayıt duruyor, tur 30 sn
        // sonra yine gelecek.
        Assert.Empty(tasima.Cagrilar);
        Assert.True(LogVar("cihaz satışta — tur atlandı"));
        Assert.Single(_store.PendingFinishes(_saatMs));   // kayıt DURUYOR, deneme SAYILMADI
    }

    // ── GERİ ÇEKİLME VE ALARM ───────────────────────────────────────────────────

    [Fact]
    public async Task Basarisiz_deneme_USTEL_olarak_ertelenir_ve_kayit_DURUR()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            TicketFlags.TotalsAndPaymentsPrinted, "PrintMF", "0xF003");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        await h.DrainOutboxAsync();

        // ← ÇİVİ: VAZGEÇME YOK, SİLME YOK. Bu fişte gerçek para var; ertelemek başka, unutmak başka.
        var kalan = Assert.Single(_store.PendingFinishes(_saatMs + 10_000_000));
        Assert.Equal(1, kalan.Attempts);
        Assert.Equal("PrintMF", kalan.LastStep);
        // Bir sonraki deneme ERTELENDİ: aynı anda tekrar sorgulanınca listede YOK.
        Assert.Empty(_store.PendingFinishes(_saatMs));
    }

    [Fact]
    public async Task Esik_asilinca_ALARM_yukselir()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintMF", "0xF003");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        // Beş tur: her turda zamanı ilerlet ki geri çekilme dolsun.
        for (var i = 0; i < 5; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }

        // ← ÇİVİ: sessizce sonsuza kadar denemek, arızayı gizlemek olurdu. Ses yükselir —
        // ama kayıt yine DURUR (vazgeçme eşiği değil, alarm eşiği).
        Assert.True(LogVar("denemedir kapanmıyor"));
        Assert.Single(_store.PendingFinishes(_saatMs));
    }

    [Fact]
    public async Task Otomatik_pencere_dolunca_CIHAZA_GIDILMEYI_BIRAKIR_ama_kayit_DURUR()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintMF", "0xF003");
        // W121: PAHALI olmasını sağlayan şey KOD değil SÜRE — çağrı 16 sn sürüyormuş gibi.
        tasima.Sirasinda = () => _saatMs += 16_000;
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        // Sekiz denemeyi doldur (her turda geri çekilmeyi aş).
        for (var i = 0; i < 10; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }

        // ← ÇİVİ: kâğıt gerçekten bittiyse her deneme `PrintMF`'te dakikalarca bloke olur ve
        // cihaz o süre boyunca HERKESE kapalıdır. Sonsuza kadar denemek, cihazı rehin almak
        // ve kasiyerin elle kurtarmasını da engellemek olurdu.
        Assert.True(LogVar("otomatik denemeler DURDURULDU"));
        Assert.Equal(8, tasima.Cagrilar.Count);   // sekizinciden sonra CİHAZA GİDİLMEDİ

        // ← ÇİVİ: VAZGEÇME DEĞİL. Fişte gerçek para var; kayıt SİLİNMEZ ve elle tamamlama
        // her an çalışır. Yalnız 24 saatlik insan ölçeğine çekilir.
        var kalan = Assert.Single(_store.PendingFinishes(_saatMs + (long)TimeSpan.FromDays(2).TotalMilliseconds));
        Assert.Equal(8, kalan.Attempts);
        Assert.Empty(_store.PendingFinishes(_saatMs));   // yakın vadede artık gelmiyor
    }

    [Fact]
    public async Task KAGIT_YOK_denemeleri_UCUZ_oldugu_icin_SINIRLANMAZ()
    {
        // Kullanıcının talimatı (2026-09-10): "kâğıt bittiğini bildiriyorsa ... kalan 0 ise bir
        // interval başlatıp fiş kapanışını otomatik yapsın BAŞARILI OLANA KADAR."
        //
        // ← ÇİVİ: W108'in sekiz denemede durma kuralı, denemenin PAHALI olduğu varsayımına
        // dayanıyordu (her deneme cihazı dakikalarca kilitler). Bu, cihaz CEVAP VERMEDİĞİNDE
        // doğru. Ama cihaz `0x0020` ile HIZLI ve KESİN cevap verdiğinde deneme UCUZDUR ve
        // sınırlamak kullanıcının istediği davranışı bedelsizce engellemek olurdu.
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintBeforeMF", "0x0020");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        for (var i = 0; i < 12; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }

        // Sekizinci denemeden SONRA da denemeye devam etti.
        Assert.True(tasima.Cagrilar.Count > 8, $"deneme sayısı {tasima.Cagrilar.Count}, 8'de durmamalıydı");
        Assert.False(LogVar("otomatik denemeler DURDURULDU"));
        Assert.Single(_store.PendingFinishes(_saatMs));
    }

    [Fact]
    public async Task SINIRSIZ_DENEME_SESSIZ_DONGU_OLMAMALI()
    {
        // ← ÇİVİ: "8'de dur" ile "sonsuza kadar dene" arasındaki seçim, asıl kusuru gizliyordu.
        // İKİSİ DE SESSİZDİ. Eski hâl durup susuyordu; W121 sonrası durmuyor ama yine susuyordu.
        //
        // Sahada ölçüldü (2026-09-10 04:29→04:54): kâğıt bitti, süpürme 89 denemedir aynı yerde
        // (`Start`/`0xF01C`), üzerinde 990 kuruş olan fiş 25 dakikadır kapanamıyor ve NE KASA
        // NE PANEL bunu biliyor. Panel kasiyere hâlâ "rulo takın, fiş kendiliğinden kapanacak"
        // diyor — 89 denemelik bir durumda bu YANLIŞ UMUT.
        //
        // Doğru davranış: denemeye DEVAM ET (fişte para var, vazgeçmek yok) ama GÖRÜNÜR ol.
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintBeforeMF", "0x0020");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        // Yedi deneme: HENÜZ bağırma. Erken alarm, alarmı değersizleştirir.
        for (var i = 0; i < 7; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }
        Assert.False(LogVar("KURTARILAMIYOR"), "sekizden önce alarm vermemeliydi");

        // Sekizinci: SES ÇIKAR.
        await h.DrainOutboxAsync(); _saatMs += 10 * 60_000;
        Assert.True(LogVar("KURTARILAMIYOR"), "sekizinci denemede sesini çıkarmalıydı");

        // Ama denemeyi BIRAKMAZ — üzerinde para olan fiş unutulmaz.
        Assert.False(LogVar("otomatik denemeler DURDURULDU"));
        Assert.Single(_store.PendingFinishes(_saatMs));

        // Ve HER TURDA bağırmaz: 9→25 arasında yalnız 10 ve 20'de. Sürekli WARN, gürültüdür.
        for (var i = 0; i < 17; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }
        var kez = _loglar.Count(l => l.Mesaj.Contains("KURTARILAMIYOR"));
        Assert.Equal(3, kez);   // 8, 10, 20

        // Süpürme gerçekten denemeye devam etmiş olmalı (sesin bedeli durmak DEĞİL).
        Assert.True(tasima.Cagrilar.Count >= 25, $"deneme {tasima.Cagrilar.Count}, durmamalıydı");
    }

    [Fact]
    public async Task UCUZ_basarisizlikta_aralik_SABIT_10_sn_buyumez()
    {
        // ← ÇİVİ: üstel geri çekilmenin varlık sebebi PAHALI denemeyi seyreltmek. `0x0020`
        // milisaniyelerde dönüyor; orada aralığın büyümesi hiçbir şey kazandırmaz, yalnız
        // KÂĞIDI TAKAN İNSANI bekletir — eski eğride 5 dakikaya kadar.
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintBeforeMF", "0x0020");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        // Beş tur: aralık BÜYÜMEMELİ.
        for (var i = 0; i < 5; i++)
        {
            await h.DrainOutboxAsync();
            _saatMs += 11_000;                       // 10 sn'lik aralığı geç
            Assert.Equal(i + 1, tasima.Cagrilar.Count);
        }
    }

    [Fact]
    public async Task PAHALI_basarisizlikta_aralik_USTEL_buyur()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintMF", "0xF003");
        tasima.Sirasinda = () => _saatMs += 16_000;   // W121: 16 sn süren deneme = PAHALI
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        await h.DrainOutboxAsync();                  // 1. deneme
        _saatMs += 11_000;                           // 11 sn: ucuz olsaydı yetecekti
        await h.DrainOutboxAsync();

        // ← ÇİVİ: pahalı denemede 11 sn YETMEZ (üstel eğri 30 sn ister). Ucuz/pahalı ayrımı
        // gerçekten aralığı belirliyor; tek bir sabite düşseydi biri diğerinin bedelini öderdi.
        Assert.Single(tasima.Cagrilar);
    }

    [Fact]
    public async Task BILINMEYEN_kod_PAHALI_sayilir_ve_SINIRLANIR()
    {
        // ← ÇİVİ: BU GERÇEK BİR KUSURDU. İlk yazımda "pahalıların listesi"ni tutuyordum ve
        // listede olmayan HER ŞEY sessizce ucuz sayılıyordu — istisna yolundan gelen
        // "TimeoutException" gibi bir tip adı ya da eşlenmemiş herhangi bir cihaz kodu
        // (2069, 2085, 0xF01C…) dahil. Yorum "bilinmeyeni ucuz saymak sınırı sessizce
        // kaldırmak olurdu" diyordu ama kod TAM OLARAK onu yapıyordu: sınırsız deneme,
        // üstelik her biri cihazı dakikalarca kilitleyebilecek denemeler.
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintMF", "0x0817");                 // eşlenmemiş cihaz kodu (2071)
        tasima.Sirasinda = () => _saatMs += 16_000;   // ve UZUN sürüyor → PAHALI
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        for (var i = 0; i < 12; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }

        // Bilinmeyen kod PAHALI sayılır → sekizde durur.
        Assert.True(LogVar("otomatik denemeler DURDURULDU"));
        Assert.Equal(8, tasima.Cagrilar.Count);
    }

    [Fact]
    public async Task ISTISNA_PAHALI_sayilir_ucuz_araliga_dusmez()
    {
        // ← ÇİVİ: istisnadan gelen "kod" bir TİP ADIdır (`TimeoutException` gibi), cihaz kodu
        // değil. Onu ucuz saymak, ne olduğunu BİLMEDİĞİMİZ bir başarısızlığı bedava ilan edip
        // 10 saniyede bir denemek olurdu — üstelik o denemelerin her biri cihazı dakikalarca
        // kilitleyebilir.
        var (h, tasima) = Kur();
        tasima.Patlat = new TimeoutException("cihaz cevap vermedi");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        await h.DrainOutboxAsync();                  // 1. deneme → istisna
        Assert.Single(tasima.Cagrilar);

        _saatMs += 11_000;                           // ucuz olsaydı yetecekti
        await h.DrainOutboxAsync();
        Assert.Single(tasima.Cagrilar);              // ← YETMEDİ: üstel eğri 30 sn istiyor

        _saatMs += 25_000;                           // toplam 36 sn
        await h.DrainOutboxAsync();
        Assert.Equal(2, tasima.Cagrilar.Count);
    }

    [Fact]
    public void Ilk_bekleme_VARSAYILANI_10_sn()
    {
        // ← ÇİVİ: diğer testler `ilkBeklemeSn`i AÇIKÇA veriyor, yani VARSAYILAN hiçbir yerde
        // sınanmıyordu — 5 dakikaya çıkarılsa kimse fark etmezdi. Sahada kullanılan değer bu.
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, now: _saatMs);

        Assert.Empty(_store.PendingFinishes(_saatMs + 9_000));    // 9 sn: HENÜZ değil
        Assert.Single(_store.PendingFinishes(_saatMs + 10_000));  // 10 sn: sırası geldi
    }

    [Fact]
    public void Tur_SIKLIGI_ucuz_aralikla_tutarli()
    {
        // ← ÇİVİ: tur, ucuz aralıktan SEYREK olursa 10 sn'lik aralık fiilen tur süresine
        // yuvarlanır ve kısaltmanın hiçbir anlamı kalmaz. İki sabit ayrı yerlerde duruyor
        // (biri Host'ta kullanılıyor); ilişkileri ancak burada çivilenebilir.
        Assert.True(AjanTuru.DrainAraligi <= TimeSpan.FromSeconds(10),
            $"tur {AjanTuru.DrainAraligi.TotalSeconds} sn — ucuz deneme aralığından (10 sn) seyrek olamaz");
    }

    [Fact]
    public async Task RECV_BUSY_UCUZ_sayilir_ve_SINIRLANMAZ_kagit_boyle_gorunebiliyor()
    {
        // ← ÇİVİ: SAHADA ISIRDI (2026-09-10 03:32–03:57). Kâğıt bitti ama cihaz bunu `0x0020`
        // ile DEĞİL `0xF01C RECV_BUSY` ile bildirdi — ekranında "Kağıt bitti" penceresi açıkken
        // komut kabul etmiyor. Eski kural kodu beyaz listede aramıştı, `0xF01C` yoktu, PAHALI
        // sayıldı ve süpürme SEKİZ denemede durdu. Fiş 990 kuruş tahsilatla açık kaldı ve
        // kullanıcının elle müdahalesi gerekti.
        //
        // Ölçüm o sınıflandırmayı çürüttü: her deneme 5,5 saniye (5489–5517 ms, altı ölçüm),
        // 5 dakikada bir → cihazın %1,8'i. Bu rehin alma değil.
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, Meshgul: true,
            new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "DEVICE_BUSY", 0), 0, "Start", "0xF01C");
        tasima.Sirasinda = () => _saatMs += 5_500;    // sahada ölçülen süre
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        for (var i = 0; i < 12; i++) { await h.DrainOutboxAsync(); _saatMs += 20_000; }

        // ← ÇİVİ: 5,5 saniye UCUZDUR. Sekizde durmaz; kullanıcının istediği gibi kâğıt gelene
        // kadar denemeye devam eder ve rulo takıldığı an fiş kendiliğinden kapanır.
        Assert.True(tasima.Cagrilar.Count > 8, $"deneme {tasima.Cagrilar.Count} — 5,5 sn ucuzdur, durmamalıydı");
        Assert.False(LogVar("otomatik denemeler DURDURULDU"));
    }

    [Fact]
    public async Task ZAMAN_ASIMI_denemeleri_PAHALI_kalir_ve_SINIRLANIR()
    {
        // ← ÇİVİ: kuralın diğer yarısı. 100 saniyelik zaman aşımı adımları 5 dakikada bir
        // koşarsa cihazın %58'ini yer — asıl korunmak istediğim buydu ve korunuyor.
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.MfVeKapat, TicketFinish.Tamamlanabilir, 5),
            0, "PrintMF", "0xF003");
        tasima.Sirasinda = () => _saatMs += 100_000;   // zaman aşımı: gerçekten pahalı
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        for (var i = 0; i < 12; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }

        Assert.True(LogVar("otomatik denemeler DURDURULDU"));
        Assert.Equal(8, tasima.Cagrilar.Count);
        Assert.Single(_store.PendingFinishes(_saatMs + (long)TimeSpan.FromDays(2).TotalMilliseconds));
    }

    [Fact]
    public async Task Cihaz_MESGULSE_alarm_yukselmez_yalnizca_ertelenir()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, Meshgul: true,
            new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "DEVICE_BUSY", 0), 0, "Start", "0xF01C");
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        for (var i = 0; i < 6; i++) { await h.DrainOutboxAsync(); _saatMs += 10 * 60_000; }

        // ← ÇİVİ: "cevap gelmedi" ile "denendi ve olmadı" AYRI olgular. İlkini alarma çevirmek,
        // hiç yapılmamış bir denemeyi başarısızlık diye raporlamak olurdu.
        Assert.True(LogVar("cihaz meşgul/cevapsız"));
        Assert.False(LogVar("denemedir kapanmıyor"));
        Assert.Single(_store.PendingFinishes(_saatMs));
    }

    [Fact]
    public async Task Odemesi_EKSIK_fis_alarm_degil_INSAN_KARARI_olarak_bildirilir()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, false, false,
            new FisBitirmePlani(FisBitirmeAdimi.Dokunma, TicketFinish.OdemeTamamlanmamis, 7), 0);
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        await h.DrainOutboxAsync();

        // ← ÇİVİ (şart ③): kalanı tahsil etmek KASANIN kararı, bir bakım turunun değil.
        // Otomatik yol burada durur ve durduğunu söyler.
        Assert.True(LogVar("TAMAMLANAMAZ — dokunulmadı, insan kararı"));
        Assert.Equal(7, Alan<int>(LogVeri("TAMAMLANAMAZ"), "ureticiAdimi"));
    }

    [Fact]
    public async Task Cihazda_fis_KALMAMISSA_kayit_duser_ama_KAPANDI_denmez()
    {
        var (h, tasima) = Kur();
        tasima.Sonuc = new StuckTicketResult(false, FisAcikDegil: true, false,
            new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "TICKET_NOT_OPEN", 0), 0);
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", null, null, 0, _saatMs);

        await h.DrainOutboxAsync();

        // ← ÇİVİ: fiş başka bir yoldan kapanmış olabilir (kasiyer, önceki tur). Kaydı düşürmek
        // doğru; "kapandı" diye deftere yazmak ise ÖLÇMEDİĞİMİZ bir kapanışı iddia etmek olurdu.
        Assert.Empty(_store.PendingFinishes(_saatMs));
        Assert.False(LogVar("TAMAMLANDI"));
        Assert.Empty(_store.PendingClosedTickets());
    }

    // ── İKİNCİ İŞARETLEME ───────────────────────────────────────────────────────

    [Fact]
    public void Ikinci_isaretleme_deneme_sayacini_SIFIRLAMAZ()
    {
        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", "PrintMF", "0xF003", 0, _saatMs);
        _store.MarkFinishAttempt("tkt_1", "PrintMF", "0xF003", _saatMs);
        _store.MarkFinishAttempt("tkt_1", "PrintMF", "0xF003", _saatMs);

        _store.MarkTicketNeedsFinish("tkt_1", "t1", "oturum-A", "PrintBeforeMF", "0xF004", 0, _saatMs);

        // ← ÇİVİ: sayaç sıfırlansaydı, tekrar tekrar patlayan bir fişte geri çekilme hiç
        // büyümez ve cihaza sonsuza kadar 15 saniyede bir gidilirdi.
        var r = Assert.Single(_store.PendingFinishes(_saatMs + 10_000_000));
        Assert.Equal(2, r.Attempts);
        Assert.Equal("PrintBeforeMF", r.LastStep);   // hata alanları YİNE DE tazelenir
    }

    // ── SAHTELER (yalnız derlenmek için; bu turda kullanılmıyorlar) ──────────────

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
        public Task<NotifyResult> NotifyAsync(string paymentId, string body, CancellationToken ct = default)
            => Task.FromResult(Ok());
        public Task<NotifyResult> NotifyTicketCancelAsync(string body, CancellationToken ct = default)
            => Task.FromResult(Ok());
        public Task<NotifyResult> NotifyTicketClosedAsync(string body, CancellationToken ct = default)
            => Task.FromResult(Ok());
    }
}
