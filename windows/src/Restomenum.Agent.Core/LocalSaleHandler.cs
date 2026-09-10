namespace Restomenum.Agent.Core;

/// <summary>
/// Yerel ödeme akışı (yerel mimari, K-21): kasadan gelen <see cref="SaleToPoiRequest"/>'i işler.
/// <b>Taşımadan bağımsız karar mantığını (<see cref="AgentOrchestrator"/> + <see cref="Outbox"/>)
/// yeniden kullanır</b> — WSS yerine yerel HTTP + platform bildirimi.
///
/// <para>Sıra (peer sözleşmesi): tutarı ÇEK (GET=ACK, çekmeden terminali sürme) → departman çöz +
/// kuruş dağıtımıyla fiş satırları → orkestratör (dedupe/durum/UNKNOWN) → ÖNCE platforma bildir
/// (dayanıklı outbox + POST) → SONRA kasaya senkron dön. Ters sıra kasayı "belirsiz" gösterirdi.</para>
///
/// <para><b>2xx dışında terminale gidilmez.</b> Tutar alınamadıysa çekilecek doğru tutar bilinmez.
/// Eşlenmeyen kalem terminale GİTMEDEN reddedilir ve HANGİ ürün olduğu söylenir.</para>
/// </summary>
public sealed class LocalSaleHandler : IDeviceIdleGate
{
    private readonly IPaymentDetailClient _amounts;
    private readonly AgentOrchestrator _orch;
    private readonly CommandStore _store;
    private readonly ILineDepartmentResolver _departments;
    private readonly IPaymentMethodResolver _paymentMethods;
    private readonly IResultNotifier _notifier;
    private readonly Outbox _outbox;
    /// <summary>
    /// İptal için gereken DOĞRUDAN terminal erişimi. Satış yolu orkestratörden geçer (dedupe,
    /// durum makinesi, belirsizlik çözümü); iptalin böyle bir komut yaşam döngüsü YOK — cihazdaki
    /// fişe bakıp karar veren tek adımlık bir iş. Orkestratöre sıkıştırmak, satışın durum
    /// makinesine ait olmayan bir şeyi oraya sokmak olurdu.
    /// </summary>
    private readonly ITerminalTransport _transport;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string, object?> _log;

    /// <summary>
    /// Terminal başına TEK işlem (değişmez #4) — silinen <c>AgentSession</c>'dan taşındı. İki satış
    /// aynı cihaz oturumunda eşzamanlı sürülemez: ikinci <c>StartTicket</c> birincinin fişini sessizce
    /// iptal eder ve üzerindeki para kaybolur. Yerel dinleyici istekleri eşzamanlı gelebildiği için şart.
    /// </summary>
    private readonly SemaphoreSlim _islemKilidi = new(1, 1);

    /// <summary>
    /// <see cref="IDeviceIdleGate"/> (W56) — bakım işleri cihazı AYNI kilitten alır. İkinci bir kilit
    /// açsaydık "cihaz başına tek işlem" iki ayrı yerde tanımlanır ve biri diğerini görmezdi: bakım
    /// turu satışın ortasında <c>GetDepartments</c> çağırırdı.
    ///
    /// <para><b>BEKLEMEZ</b> (<c>Wait(0)</c>): satış her zaman öncelikli. Meşgulse tur atlanır ve
    /// ~30 dk sonra yeniden denenir; tabloyu bir tur geç görmek, satışı geciktirmekten iyidir.</para>
    /// </summary>
    public IDisposable? TryAcquire()
        => _islemKilidi.Wait(0) ? new KilidiBirak(_islemKilidi) : null;

    private sealed class KilidiBirak(SemaphoreSlim kilit) : IDisposable
    {
        private int _birakildi;
        public void Dispose()
        {
            // Çift Dispose semaforu 1'in ÜSTÜNE çıkarır ve o andan sonra iki iş cihazı paylaşır.
            if (Interlocked.Exchange(ref _birakildi, 1) == 0) kilit.Release();
        }
    }

    /// <summary>
    /// Aynı anda TEK kurtarma turu. Açılış kurtarması ile periyodik kurtarma (W31) aynı metodu
    /// çağırıyor ve uzun bir kuyrukta ÜST ÜSTE BİNİYORLARDI — canlıda görüldü (2026-09-07 20:59):
    /// açılış turu 7 komutu ~31 sn'de bir yoklarken 60 sn'lik periyodik tur başa dönüp aynı
    /// komutları yeniden sordu. Bozulma yok (her yoklama terminal kilidini alıyor) ama cihaza
    /// gereksiz tur bindiriyor ve kuyruk uzadıkça turlar üst üste yığılırdı.
    /// </summary>
    private readonly SemaphoreSlim _kurtarmaKilidi = new(1, 1);

    /// <summary>Açılışta yoklanacak <c>UNKNOWN</c> kayıtların yaş sınırı (saat).</summary>
    private const int UnknownKurtarmaSaati = 24;

    /// <summary>
    /// Kayıp kapanış kurtarmasının YAŞ EŞİĞİ (saniye, W76). Normal akışın kapanış→outbox aralığı
    /// sahada ~800 ms ölçüldü; 30 sn onun çok üstünde ve "süreç öldü" vakasını geciktirmesi
    /// önemsiz (o kayıt zaten bir sonraki drain turunda ya da açılışta toplanır).
    /// </summary>
    private const int KayipKapanisYasiSn = 30;

    /// <summary>
    /// Kaç başarısız tamamlama denemesinden sonra ALARM yükseltilir (W105). Geri çekilme eğrisiyle
    /// (30·60·120·240·300 sn) yaklaşık <b>12,5 dakika</b> eder — kasiyerin bir masayı beklemesi
    /// için uzun, ama cihazın geçici bir meşguliyetini alarma çevirmek için kısa değil.
    ///
    /// <para><b>Vazgeçme eşiği DEĞİL.</b> Bu fişte gerçek para var; denemeler sonsuza kadar
    /// (5 dakikada bir) sürer. Eşik yalnız sesin yükseldiği yerdir.</para>
    /// </summary>
    private const int TamamlamaAlarmEsigi = 5;

    /// <summary>
    /// <b>W108</b> — kaç denemeden sonra OTOMATİK dokunma bırakılır (kayıt SİLİNMEZ).
    ///
    /// <para><b>Neden sınır gerekiyor:</b> kâğıt gerçekten bittiyse her deneme <c>PrintMF</c>'te
    /// dakikalarca bloke olur ve cihaz o süre boyunca HERKESE kapalıdır. 5 dakikalık geri çekilme
    /// ile sonsuza kadar denemek, cihazı zamanın kabaca yarısında meşgul tutardı — kasiyer elle
    /// bile kurtaramaz. Sekiz deneme, geri çekilme eğrisiyle yaklaşık <b>yarım saatlik</b> bir
    /// otomatik pencere demek: birinin kâğıdı değiştirmesine yeter, cihazı rehin almaya yetmez.</para>
    ///
    /// <para><b>Sonrasında ne olur:</b> kayıt <see cref="TamamlamaErtelemesi"/> kadar ertelenir ve
    /// otomatik yol susar. Elle tamamlama her an çalışır; <c>TicketQuery</c> durumu göstermeye
    /// devam eder. <b>Vazgeçme yok:</b> fişte gerçek para var.</para>
    /// </summary>
    private const int TamamlamaOtomatikSiniri = 8;

    /// <summary>
    /// <b>W114 — hangi başarısızlıklar OTOMATİK SINIRA TABİ.</b>
    ///
    /// <para>Kullanıcının talimatı (2026-09-10): <i>"kâğıt bittiğini bildiriyorsa ... kalan 0 ise
    /// bir interval başlatıp fiş kapanışını otomatik yapsın BAŞARILI OLANA KADAR."</i> Bu, W108'in
    /// sekiz denemede durma kuralıyla çelişiyordu. Çelişkiyi çözen şey ölçüm oldu:</para>
    ///
    /// <para><b>W108'in gerekçesi denemenin PAHALI olduğu varsayımıydı</b> — her deneme cihazı
    /// dakikalarca kilitler. Bu, cihaz CEVAP VERMEDİĞİNDE doğru (zaman aşımı: adım başına 100 sn).
    /// Ama cihaz <c>0x0020</c> ile <b>hızlı ve kesin</b> cevap verdiğinde deneme UCUZDUR — saniye
    /// mertebesinde. Ucuz bir denemeyi sınırlamak, kullanıcının istediği "başarılı olana kadar"
    /// davranışını bedelsizce engellemek olurdu.</para>
    ///
    /// <para>Kural: <b>bedeli olan denemeler sınırlanır, bedeli olmayanlar sınırlanmaz.</b>
    /// Zaman aşımı ve bütçe bitişi PAHALI; cihazın kesin cevabı (kâğıt yok gibi) UCUZ.</para>
    /// </summary>
    /// <summary>
    /// <b>W121 — bir deneme PAHALI mı? Ölçütü KOD değil, ÖLÇÜLEN SÜRE.</b>
    ///
    /// <para><b>Neden değişti (sahada ölçüldü, 2026-09-10 03:32–03:57):</b> kâğıt bitti ama cihaz
    /// bunu <c>0x0020</c> ile DEĞİL <c>0xF01C RECV_BUSY</c> ile bildirdi (ekranında "Kağıt bitti"
    /// penceresi açıkken komut kabul etmiyor). <c>0xF01C</c> beyaz listede olmadığı için PAHALI
    /// sayıldı, süpürme sekiz denemede durdu ve fiş 990 kuruş tahsilatla açık kaldı —
    /// kullanıcının elle müdahalesi gerekti.</para>
    ///
    /// <para><b>Ama ölçüm o sınıflandırmayı çürüttü:</b> her <c>RECV_BUSY</c> denemesi
    /// <b>5,5 saniye</b> sürdü (5489–5517 ms, altı ölçüm) ve 5 dakikada bir koşuyordu — yani
    /// cihazın <b>%1,8'i</b>. Bu "rehin alma" değil. Asıl korktuğum şey 100 saniyelik zaman
    /// aşımlarıydı: onlar aynı ritimde cihazın <b>%58'ini</b> yer.</para>
    ///
    /// <para>Yani doğru ölçüt kodun kendisi değil, <b>denemenin gerçekte ne kadar sürdüğü</b>.
    /// Kod listesi önceden yazılmış bir tahmindi; süre ölçümdür ve tanımadığımız kodlar için de
    /// çalışır.</para>
    ///
    /// <para><b>Eşik 15 sn</b> — ölçülenleri geniş bir payla ayırıyor: <c>0x0020</c> 0,5 sn ve
    /// <c>0xF01C</c> 5,5 sn bir yanda; zaman aşımı adımları 100 sn ve bütçe 165 sn öte yanda.
    /// Arada ölçülmüş hiçbir şey yok.</para>
    ///
    /// <para>⚠️ Parametre <b>nullable DEĞİL</b> ve bilerek: süre her zaman ölçülüyor. Önce
    /// <c>TimeSpan?</c> yazıp "ölçülemezse pahalı say" diye bir dal koymuştum; mutasyon testi o
    /// dalın hiç çalışmadığını gösterdi — bir koruma değil, koruma GÖRÜNTÜSÜYDÜ. Aynı şey
    /// süpürmedeki `uygula &&` kapısında da olmuştu. Çalışmayan koruma, okuyanı yanıltır.</para>
    /// </summary>
    private static readonly TimeSpan UcuzDenemeEsigi = TimeSpan.FromSeconds(15);

    private static bool PahaliDeneme(TimeSpan sure) => sure > UcuzDenemeEsigi;


    /// <summary>
    /// <b>W116 — ucuz başarısızlıkta SABİT ve KISA aralık.</b>
    ///
    /// <para>Üstel geri çekilmenin varlık sebebi PAHALI denemeyi seyreltmektir. <c>0x0020</c>
    /// milisaniyelerde dönüyor; orada aralığın büyümesi hiçbir şey kazandırmaz, yalnız
    /// <b>kâğıdı takan insanı bekletir</b> — eski eğride 5 dakikaya kadar.</para>
    ///
    /// <para>10 sn, turun kendisiyle aynı: rulo takıldıktan sonra fiş en geç bir sonraki turda
    /// kapanır.</para>
    /// </summary>
    private static readonly TimeSpan UcuzDenemeAraligi = TimeSpan.FromSeconds(10);

    /// <summary>Bir sonraki denemenin aralığı — denemenin ÖLÇÜLEN bedeline göre.</summary>
    private static TimeSpan TamamlamaAraligi(int deneme, TimeSpan sure) =>
        PahaliDeneme(sure) ? Outbox.Bekleme(deneme) : UcuzDenemeAraligi;

    /// <summary>Otomatik sınır dolunca kaydın ertelendiği süre — insan ölçeği, cihaz ölçeği değil.</summary>
    private static readonly TimeSpan TamamlamaErtelemesi = TimeSpan.FromHours(24);

    /// <summary>
    /// <b>W109</b> — satış yolunda cihaz kilidini beklemenin ÜST sınırı.
    ///
    /// <para><b>Neden gerekli:</b> kilit eskiden tavansız bekleniyordu (<c>WaitAsync(ct)</c>).
    /// Kasanın POST zaman aşımı 180 sn; önceki satış hâlâ basıyorsa ikinci istek kilitte bekler,
    /// kasa pes eder ve <b>hiçbir şey öğrenemez</b> — zaman aşımı bütçesinin (<see cref="BaskiButcesi"/>)
    /// bütün faydası tam burada kaybolurdu. Tavan 180'in ALTINDA: kasa her hâlde KESİN bir cevap alır.</para>
    ///
    /// <para>Süre dolarsa ödeme <b>cihaza HİÇ GİTMEZ</b> — para hareket etmedi, kesin ret güvenli.</para>
    /// </summary>
    public static readonly TimeSpan SatisKilitTavaniVarsayilan = TimeSpan.FromSeconds(150);

    /// <summary>Etkin tavan — testte kısaltılabilir; üretimde varsayılan.</summary>
    private readonly TimeSpan _satisKilitTavani;

    /// <summary>
    /// Satış öncesi eşleme tazeleyici (W29). <c>null</c> = kapalı (test/eski davranış): satış
    /// diskteki eşlemeyle yapılır.
    /// </summary>
    private readonly IMappingRefresher? _mappingRefresher;

    public LocalSaleHandler(
        IPaymentDetailClient amounts, AgentOrchestrator orch, CommandStore store,
        ILineDepartmentResolver departments, IPaymentMethodResolver paymentMethods,
        IResultNotifier notifier, Outbox outbox, ITerminalTransport transport,
        Func<DateTimeOffset>? now = null, Action<string, object?>? log = null,
        IMappingRefresher? mappingRefresher = null, TimeSpan? satisKilitTavani = null)
    {
        _satisKilitTavani = satisKilitTavani ?? SatisKilitTavaniVarsayilan;
        _mappingRefresher = mappingRefresher;
        _transport = transport;
        _amounts = amounts;
        _orch = orch;
        _store = store;
        _departments = departments;
        _paymentMethods = paymentMethods;
        _notifier = notifier;
        _outbox = outbox;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? ((_, _) => { });
    }

    /// <summary>
    /// <b>Açılış kurtarması</b> — yeniden başlatmada kesin sonuca ulaşmamış komutları çözer.
    ///
    /// <para>Ajan kart penceresinde ölürse komut <c>SENT_TO_TERMINAL</c>'da kalır; kasa çağrısı çoktan
    /// bitmiştir (kasaya geri dönmeyiz). Terminale SORULUR (orkestratör dedupe→probe), sonuç platforma
    /// bildirilir. Böylece <b>çekilmiş bir kart sessizce kaybolmaz</b>. Bu yerel mimaride WSS
    /// <c>AgentSession.KurtarAsync</c>'ın yerini alır; aynı orkestratör+outbox mantığını kullanır.</para>
    /// </summary>
    public async Task RecoverPendingAsync(CancellationToken ct = default)
    {
        // Tur zaten koşuyorsa BEKLEME, ATLA: bekleseydik turlar sıraya girip aynı işi arka arkaya
        // tekrarlardı. Atlamak güvenli — koşan tur zaten aynı listeyi işliyor.
        if (!await _kurtarmaKilidi.WaitAsync(0, ct)) return;
        try { await KurtarmaTuruAsync(ct); }
        finally { _kurtarmaKilidi.Release(); }
    }

    private async Task KurtarmaTuruAsync(CancellationToken ct)
    {
        // 24 saat: kasanın deneme TTL'i. Ondan sonra kasa o ServiceID ile geri gelmez ve platform
        // denemeyi çoktan operatöre düşürmüştür; yoklamaya devam etmek yalnız açılışı geciktirir.
        var esik = _now().AddHours(-UnknownKurtarmaSaati).ToUnixTimeMilliseconds();
        var atlanan = _store.CountSkippedUnknown(esik);
        if (atlanan > 0)
            _log("[yerel] açılış kurtarması — yaş sınırı", new { atlanan, saat = UnknownKurtarmaSaati });

        // W62: BU SÜREÇTE ŞU AN İŞLENEN komutlar kurtarılmaz. Onların sonucu birazdan asıl satış
        // yolundan gidecek; kurtarmanın araya girmesi hem çift bildirim riski hem de hâlâ koşan bir
        // komutun yoklamasını KAPATMA riski (canlı görüldü, 2026-09-09 01:44).
        var hepsi = _store.Pending(unknownEsigi: esik);
        var pending = hepsi.Where(k => !_ucusta.ContainsKey(k.CommandId)).ToList();
        if (hepsi.Count != pending.Count)
            _log("[yerel] kurtarma — UÇUŞTAKİ komutlar atlandı", new
            {
                atlanan = hepsi.Count - pending.Count,
                komutlar = string.Join(",", hepsi.Where(k => _ucusta.ContainsKey(k.CommandId)).Select(k => k.CommandId)),
            });
        if (pending.Count == 0) return;
        _log("[yerel] açılış kurtarması", new { adet = pending.Count });

        foreach (var k in pending)
        {
            if (ct.IsCancellationRequested) return;

            // ── TERMİNAL KİLİDİ: kurtarma da sıraya girer ─────────────────────────────
            // Kurtarma eskiden kilidi ALMIYORDU ve uçuştaki bir satışla AYNI ANDA koşabiliyordu.
            // Sahada bunun bedeli ölçüldü (2026-09-07 16:36): kurtarma, kasanın o an yaptığı yeni
            // satışın fişini gördü ve eski bir komut o ödemeyi sahiplendi (hayalet onay).
            // Sahiplik kapısı artık bunu ayrıca engelliyor; bu kilit ikinci savunma ve aynı
            // zamanda cihazı iki iş arasında paylaştırmama kuralının (değişmez #4) gereği.
            //
            // Satışa ÖNCELİK: 60 sn'de kilit alınamazsa tur ATLANIR. Kayıt duruyor, bir sonraki
            // açılış/tur yeniden dener; kurtarmayı beklemek için satışı geciktirmek yanlış olurdu.
            if (!await _islemKilidi.WaitAsync(TimeSpan.FromSeconds(60), ct))
            {
                _log("[yerel] açılış kurtarması atlandı — terminal meşgul (satış öncelikli)",
                    new { k.CommandId });
                return;
            }

            AgentOutcome outcome;
            try
            {
                // Tutar/kalem probe'da kullanılmaz: HandleAsync dedupe → HandleDuplicate → terminale sorar.
                var probe = new SaleRequest(k.CommandId, k.PaymentId, k.TerminalId, 0, "", 0);

                // ── W39: ESKİ komutta ilk gecikmeyi ATLA ─────────────────────────────
                // Kurtarma turu terminal kilidini alıp politikanın ilk gecikmesi boyunca UYUYOR;
                // o sırada gelen canlı satış sırada bekliyor. Sahada ölçüldü (13:18): bir satış
                // 18,7 saniye bekledi ve o süre boyunca cihaz tamamen boştu.
                //
                // 30 saniyelik bekleme ödemeden HEMEN SONRAKİ kurtarma için doğru — cihazın
                // yerleşmesi gerekiyor. Saatler önce başarısız olmuş bir komut için o gerekçe
                // yok; orada bekleme yalnız kasayı geciktiriyor.
                // Yaş ölçüsü `ReceivedAt`: komut ne zaman geldi. Uçuştaki kurtarma (ödemeden
                // hemen sonra) buradan GEÇMİYOR — o `ApplyAsync → ResolveAsync` yolunda ve 30 sn
                // gecikmesini aynen koruyor. Buraya yalnız arka plan/açılış turu düşüyor; orada
                // "saniyeler önce gelmiş" tek gerçek vaka çökme sonrası açılıştır ve o hâlde
                // gecikme korunur.
                var yas = _now().ToUnixTimeMilliseconds() - k.ReceivedAt;
                var eski = yas >= (long)_orch.Recovery.InitialDelaySkipAge.TotalMilliseconds;
                outcome = await _orch.HandleAsync(probe, k.ExpiresAt, ct,
                    turPolitikasi: eski ? _orch.Recovery.WithoutInitialDelay() : null);
            }
            catch (Exception e)
            {
                // Çözülemeyen store'da KALIR — sonraki açılışta yeniden denenir.
                _log("[yerel] yarım komut çözülemedi", new { k.CommandId, error = e.Message });
                continue;
            }
            finally { _islemKilidi.Release(); }
            // Kasaya DÖNMÜYORUZ (çağrı bitti); yalnız platforma bildir. Exponent 2 (terminal sürüşü TR).
            var geri = new SaleToPoiRequest(k.CommandId, "", k.TerminalId, k.PaymentId, "", _now());
            var body = SaleToPoiResponseBuilder.BuildResult(geri, ToTransportResult(outcome), 2, _now());
            var bildirim = await NotifyAsync(k.PaymentId, body, ct);
            _log("[yerel] yarım komut çözüldü", new { k.CommandId, decision = outcome.Decision.ToString() });

            // ── W34: PLATFORM NİHAİ DEDİYSE YOKLAMAYI KAPAT ──────────────────────────
            // `Superseded` = "bu denemenin sonucu bende kesinleşti, gönderdiğin cevabı almıyorum".
            // Sormaya devam etmek her turda cihaza gidip terminal kilidini almak demek ve sonucu
            // hiçbir zaman kabul edilmeyecek. Ölçüldü (2026-09-08): 7 komut gece boyunca 148 kez
            // yoklandı, 148'i de Superseded döndü — tam olarak sıfır fayda.
            //
            // Komutun DURUMU değişmiyor (`UNKNOWN` yerel gerçek olarak kalıyor); yalnız yoklama
            // kapanıyor. Aynı ServiceID tekrar gelirse tekilleme deposu onu yine bulur.
            //
            // ── W64: 409 ÇELİŞKİSİ DE NİHAİDİR ───────────────────────────────────────
            // `Conflict` (409 `conflictingResult` vb.) "platform bu denemeyi BAŞKA bir sonuçla
            // kapattı" demek. `Superseded`'den tek farkı ret biçimi; sonucu aynı: gönderdiğimiz
            // cevap ASLA kabul edilmeyecek. Bugünkü hâlinde `RetryLater` kalıyor ve kayıt SONSUZA
            // KADAR yoklanıyor.
            //
            // Sahada ölçüldü (2026-09-09 02:19–02:25): `04cfa7cdd6` her 60 saniyede bir kurtarmaya
            // girdi, cihazda `OptionFlags`+`GetTicket` yaptı ve her turda 409
            // `plugin.payment.conflictingResult` aldı — yedi tur, `recovery_closed_at` NULL, durma
            // ihtimali YOK. Her tur terminal kilidini alıyor, yani gerçek satışla yarışıyor.
            //
            // ⚠️ YALNIZ 409. 5xx ve ağ hatası "platform şu an cevap veremedi" demek; onları
            // kapatmak, geçici bir kesintide gerçekten bildirilmemiş bir sonucu sessizce gömmek
            // olurdu. Ayrım bu satırın tamamı.
            var nihai = bildirim?.Outcome is NotifyOutcome.Superseded or NotifyOutcome.Conflict;
            if (nihai)
            {
                _store.CloseRecovery(k.CommandId);
                _log("[yerel] yoklama KAPATILDI — platform denemeyi nihai saydı", new
                {
                    k.CommandId, k.PaymentId, state = bildirim!.State ?? "(yok)",
                    reason = bildirim.Reason ?? "(yok)",
                    // HANGİ nihailik: `Superseded` (bayat/sırasız) mı, `Conflict` (409) mü?
                    // İkisi farklı sorunlara işaret ediyor ve tek satırda ayırt edilebilmeli.
                    nihaiSebep = bildirim.Outcome.ToString(),
                });
            }
        }
    }

    /// <summary>
    /// <b>Fiş/ödeme iptali.</b> Kasadaki "Fiş İptal" düğmesinin ucu.
    ///
    /// <para><b>Satış yolundan ayrı tutuldu.</b> Satışın bir komut yaşam döngüsü var (dedupe →
    /// durum makinesi → belirsizlik çözümü); iptalin yok. Ama <b>aynı terminal kilidini</b> alır:
    /// iptal, süren bir satışın fişini altından çekemez.</para>
    ///
    /// <para><b>Kararı cihaz verir, biz değil.</b> Hangi referansla geldiğine bakıp "bu ödemesizdir"
    /// diye varsaymayız; fiş okunur (<c>VoidAsync</c> içinde denetim izi olarak loglanır) ve
    /// <c>VoidAll</c> cihazın kendi korumasına çarpar (2069 = üzerinde tahsilat var). Yarım kalırsa
    /// sonuç <c>VOID_INCOMPLETE</c>'tir ve kasiyere "tekrar dene" DENMEZ.</para>
    /// </summary>
    public async Task<string> HandleReversalAsync(ReversalRequest req, CancellationToken ct = default)
    {
        // ── FİŞ BAZLI İPTAL (scope: ticket) ────────────────────────────────────────
        // Kullanıcı kararı: "Fiş İptal" = açık fişin TAMAMI. Referans/bağ eşleşmesi ARANMAZ —
        // kasiyer cihazın başında ve ekranda gördüğü fişi iptal ediyor; o fiş başka bir kasadan
        // kalmış olabilir. Ödeme yolundaki sahiplik kapısı burada geçerli DEĞİL: orada risk
        // "başkasının parasını sahiplenmek", burada gereklilik "kasiyerin cihazı kurtarabilmesi".
        if (string.Equals(req.Scope, "ticket", StringComparison.Ordinal))
            return await FisIptalAsync(req, ct);

        // Referans DOĞRULAMASI: açık-fiş dalında orijinal ServiceID bizim defterimizde olmalı.
        // Olmayan bir komut için iptal kabul etmek, "cihazda ne varsa iptal et" demek olurdu —
        // başka bir kasanın fişini silebilirdik.
        if (req.OriginalPoiTransactionId is null && req.OriginalServiceId is not null
            && _store.Read(req.OriginalServiceId) is null)
        {
            _log("[iptal] referans defterde yok — terminale gidilmedi",
                new { req.PaymentId, req.OriginalServiceId });
            return SaleToPoiResponseBuilder.BuildReversalResult(req,
                new TransportResult(TransportOutcome.Declined,
                    ProviderResultCode: $"UNKNOWN_REFERENCE:{req.OriginalServiceId}",
                    ErrorCondition: "NotFound"),
                2, _now());
        }

        // TEKİLLEME (W5/W9): iptalin KENDİ ServiceID'si var ve kasa ağ hatasında aynı zarfı yeniden
        // POST edebiliyor. Kayıt `kind='void'` ile yazılır; açılış kurtarması yalnız `kind='sale'`
        // okuduğu için bir iptal ASLA "yarım kalmış satış" sanılmaz.
        // `PaymentId!` güvenli: buraya YALNIZ ödeme kapsamı düşer (fiş kapsamı yukarıda dallandı)
        // ve ayrıştırıcı ödeme kapsamında paymentId'yi ZORUNLU tutuyor.
        var kayit = _store.Save(req.ServiceId, req.PaymentId!, req.PoiId,
            _now().ToUnixTimeMilliseconds() + 86_400_000, kind: CommandKinds.Void);
        if (kayit is SaveResult.Duplicate d && d.Command.State.IsFinal() && d.Command.ResultJson is string saklanan)
        {
            // İkinci POST cihaza GİTMEZ: ilk iptalin sonucu replay edilir. Aksi hâlde ikinci
            // `VoidAll` ya boşa çalışır ya da araya giren yeni bir fişi iptal ederdi.
            _log("[iptal] tekrar gelen istek — saklanan sonuç replay edildi", new { req.PaymentId, req.ServiceId });
            return saklanan;
        }

        await _islemKilidi.WaitAsync(ct);
        TransportResult sonuc;
        try
        {
            sonuc = await _transport.VoidAsync(ct);
        }
        catch (Exception e)
        {
            // Terminale ulaşılamadı: iptalin AKIBETİ BELİRSİZ. "Olmadı" demek yanlış olurdu —
            // VoidAll cihazda işlemiş ve cevap kaybolmuş olabilir.
            _log("[iptal] terminale ulaşılamadı", new { req.PaymentId, error = e.Message });
            sonuc = new TransportResult(TransportOutcome.Unknown,
                ProviderResultCode: $"VOID_UNREACHABLE:{e.GetType().Name}",
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete);
        }
        finally { _islemKilidi.Release(); }

        var govde = SaleToPoiResponseBuilder.BuildReversalResult(req, sonuc, 2, _now());

        // Sonucu KAYDET: ikinci POST'un replay edebilmesi için. Belirsiz sonuçta (VOID_INCOMPLETE)
        // kayıt UNKNOWN'da bırakılır — tekrar gelirse cihaza yeniden sorulabilsin.
        if (sonuc.Outcome is TransportOutcome.Approved or TransportOutcome.Declined)
        {
            _store.Advance(req.ServiceId, CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL);
            _store.Advance(req.ServiceId, CommandState.SENT_TO_TERMINAL, CommandState.COMPLETED,
                resultJson: govde);
        }

        _log("[iptal] sonuç", new
        {
            req.PaymentId, outcome = sonuc.Outcome.ToString(),
            errorCondition = sonuc.ErrorCondition, reason = sonuc.Reason, info = sonuc.Info,
        });
        // Platforma bildir: iptal defterde de görünmeli, yoksa kasa ile defter ıraksar.
        // `PaymentId!` güvenli: ödeme kapsamında ayrıştırıcı onu zorunlu tutuyor.
        await NotifyAsync(req.PaymentId!, govde, ct);
        return govde;
    }

    /// <summary>
    /// <b>ONAY GERİ ÇEKME</b> — daha önce onaylı bildirilmiş bir komutun düzeltmesini platforma
    /// yollar. Operatör aracı: cihaza DOKUNMAZ, yalnız defteri düzeltmeye çağırır.
    ///
    /// <para><b>Neden otomatik değil:</b> W14'ün sahiplik kapısından sonra hayalet onayın tekrar
    /// oluşması için bilinen bir yol kalmadı; "kendiliğinden fark et" diyebileceğim sağlam bir
    /// tetikleyici YOK. Uydurma bir tetikleyici koymak, geri çekmeyi yanlış vakalarda ateşler ve
    /// doğru onayları da bozardı. Bu yüzden karar insanda.</para>
    ///
    /// <para>Komut deposunda KALIR (durumu değişmez): geri çekme ayrı bir bildirimdir, yerel
    /// geçmişi silmek değil.</para>
    /// </summary>
    public async Task<bool> RetractLandedAsync(string commandId, CancellationToken ct = default)
    {
        var kayit = _store.Read(commandId);
        if (kayit is null)
        {
            _log("[geri-çekme] komut defterde YOK — bildirim gönderilmedi", new { commandId });
            return false;
        }

        var govde = SaleToPoiResponseBuilder.BuildLandedRetraction(
            serviceId: kayit.CommandId, saleId: "", poiId: kayit.TerminalId,
            paymentId: kayit.PaymentId, now: _now());

        var sonuc = await _notifier.NotifyAsync(kayit.PaymentId, govde, ct);
        _log("[geri-çekme] gönderildi", new
        {
            commandId, kayit.PaymentId, durum = kayit.State.ToString(),
            outcome = sonuc.Outcome.ToString(), sonuc.StatusCode, sonuc.Message,
        });
        return sonuc.Outcome == NotifyOutcome.Recorded;
    }

    /// <summary>Açık fişin tamamını iptal eder; sonucu kasaya döner ve platforma bildirir.</summary>
    private async Task<string> FisIptalAsync(ReversalRequest req, CancellationToken ct)
    {
        await _islemKilidi.WaitAsync(ct);
        TicketVoidResult sonuc;
        try
        {
            sonuc = await _transport.VoidTicketAsync(req.PoiId, ct);
        }
        catch (Exception e)
        {
            // Terminale ulaşılamadı: iptalin AKIBETİ BELİRSİZ. "Olmadı" demek yanlış olurdu —
            // VoidAll cihazda işlemiş ve cevap kaybolmuş olabilir.
            _log("[iptal] fiş iptalinde terminale ulaşılamadı", new { req.PaymentId, error = e.Message });
            sonuc = new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                ProviderResultCode: $"VOID_UNREACHABLE:{e.GetType().Name}");
        }
        finally { _islemKilidi.Release(); }

        var govde = SaleToPoiResponseBuilder.BuildTicketReversalResult(req, sonuc, 2, _now());
        _log("[iptal] fiş sonucu", new
        {
            req.PaymentId, outcome = sonuc.Outcome.ToString(), acikFisVarMiydi = sonuc.TicketWasOpen,
            odemeSayisi = sonuc.VoidedPaymentCount, tutar = sonuc.VoidedAmountMinor,
            iptalEdilenOturum = sonuc.CancelledSaleSessionId ?? "(bağ yok)",
            ticketCancelId = req.TicketCancelId ?? "(yok)",
        });

        // ── DAYANIKLI BİLDİRİM ────────────────────────────────────────────────────
        // Fiş iptali AYRI uca gider (ödeme ucu paymentId ile adresleniyor; iptal bir denemeye ait
        // değil). Ama asıl mesele dayanıklılık: cihazda fiş GERÇEKTEN iptal edildi. Bildirim o an
        // gidemezse defter iptali hiç görmez ve kasa ile defter kalıcı olarak ıraksar — üstelik
        // satışın aksine bunu sonradan keşfedecek bir kurtarma yolu YOK. O yüzden önce outbox'a
        // yazılır, sonra gönderilir; başarısızsa arka plan replay eder.
        var eid = (req.TicketCancelId ?? req.PaymentId) + ":ticket-cancel";
        // Fiş iptalinde paymentId olmayabilir (başka kasadan kalmış fiş). Outbox'ta yalnız
        // bilgi amaçlı taşınıyor; replay uca göre yönleniyor, paymentId'ye bakmıyor.
        _outbox.Enqueue(eid, req.PaymentId ?? "", OutboxKinds.TicketCancel, govde, "",
            anlikDenemeVar: true);   // W47: hemen aşağıda kendim deneyeceğim
        try
        {
            // ── W46: BİLDİRİMİN AKIBETİ HER DURUMDA YAZILIR ───────────────────────
            // Burada iki kör nokta vardı ve ikisi de sahada ısırdı (2026-09-08 19:13):
            //   • BAŞARI hiç loglanmıyordu → "iptal ne zaman gitti" sorusu cevapsız kaldı;
            //     kayıt `Confirm` ile silindiği için geriye dönüp bakılacak bir iz de yok.
            //   • AĞ HATASI da loglanmıyordu (`IsProblem` false, `IsFinal` false) → bildirim
            //     gitmemiş olmasına rağmen günlük SESSİZ. Sessizlik "gitti" ile "hiç denenmedi"yi
            //     ayırt edilemez yapıyordu — [[veri-yok-veri-degisti-ayrimi]] ile aynı hata.
            // Süre de yazılıyor: gecikmenin cihazda mı, bildirimde mi, panelde mi olduğunu
            // ancak bu sayı ayırır.
            var t0 = _now();
            var bildirim = await _notifier.NotifyTicketCancelAsync(govde, ct);
            var sureMs = (long)(_now() - t0).TotalMilliseconds;
            // W68: 400 → damgala ve SAKLA. Bu yol sahada tam olarak bunu yaşadı (2026-09-09 14:42):
            // fiş cihazda iptal edildi, bildirim `invalidResult` ile reddedildi, kayıt SİLİNDİ ve
            // iptalin platformda hiçbir izi kalmadı. Kayıt dursaydı düzeltme sonrası gönderilebilirdi.
            if (bildirim.IsPermanentGap) _outbox.MarkPermanentlyRejected(eid);
            else if (bildirim.IsFinal) _outbox.Confirm(eid);
            else _outbox.MarkAttempt(eid);
            if (bildirim.IsProblem)
                _log("[iptal] fiş iptali bildirimi SORUNU (alarm)", new
                {
                    req.PaymentId, outcome = bildirim.Outcome.ToString(),
                    bildirim.StatusCode, bildirim.Message, sureMs, oturumMs = bildirim.SessionMs, postMs = bildirim.PostMs,
                });
            else if (!bildirim.IsFinal)
                _log("[iptal] fiş iptali bildirimi GİTMEDİ — outbox'ta kaldı (replay)", new
                {
                    req.PaymentId, ticketCancelId = req.TicketCancelId ?? "(yok)",
                    outcome = bildirim.Outcome.ToString(), bildirim.StatusCode, sureMs, oturumMs = bildirim.SessionMs, postMs = bildirim.PostMs,
                });
            else
                _log("[iptal] fiş iptali bildirimi yazıldı", new
                {
                    req.PaymentId, ticketCancelId = req.TicketCancelId ?? "(yok)",
                    outcome = bildirim.Outcome.ToString(), bildirim.StatusCode,
                    state = bildirim.State ?? "(yok)",
                    replayed = bildirim.Replayed?.ToString() ?? "(yok)",
                    sureMs, oturumMs = bildirim.SessionMs, postMs = bildirim.PostMs,
                });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log("[iptal] bildirim gönderilemedi — outbox'ta kaldı (replay)",
                new { req.PaymentId, error = e.Message });
        }
        return govde;
    }

    /// <summary>Bir SaleToPOIRequest'i uçtan uca işler; kasaya dönecek <c>SaleToPOIResponse</c> JSON'unu verir.</summary>
    /// <summary>
    /// <b>W62 — bu SÜREÇTE şu an işlenen komutlar.</b> Kurtarma turu bunları ATLAR.
    ///
    /// <para><b>Neden gerekli (sahada ölçüldü, 2026-09-09 01:44):</b> satış yolu terminal kilidini
    /// yalnız CİHAZ çağrısı boyunca tutuyor ve bildirimden ÖNCE bırakıyor. Kurtarma turu o
    /// boşlukta kilidi aldı ve hâlâ uçuşta olan komutun (<c>039755a185</c>) adına P4 gönderdi —
    /// gerçek sonuçtan <b>184 ms önce</b>. O tur zarar vermedi çünkü platform <c>stale</c> dedi,
    /// ama aynı turda komutun <b>yoklaması KAPATILDI</b> (<c>recovery_closed_at</c>). Satış o an
    /// çökseydi komut <c>UNKNOWN</c> kalır <b>ve</b> kapatılmış olurdu → bir daha hiç yoklanmaz →
    /// sessiz kayıp. Tam da W34'ün önlemek için var olduğu şeyin tersi.</para>
    ///
    /// <para><b>Neden kilidi bildirime kadar uzatmadık:</b> o zaman kurtarma ve iptal, satışın
    /// BULUT turunu da beklerdi. Kilit cihazı korumak için var; defteri korumanın yolu cihazı
    /// daha uzun meşgul etmek değil.</para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ucusta = new();

    public async Task<string> HandleAsync(SaleToPoiRequest req, CancellationToken ct = default)
    {
        _ucusta[req.ServiceId] = 0;
        // `finally`: satış hangi yoldan çıkarsa çıksın (ret, istisna, iptal) kayıt kümeden DÜŞER.
        // Düşmezse o komut bir daha ASLA kurtarılamaz — koruma, koruduğu şeyi yok ederdi.
        try { return await IsleAsync(req, ct); }
        finally { _ucusta.TryRemove(req.ServiceId, out _); }
    }

    private async Task<string> IsleAsync(SaleToPoiRequest req, CancellationToken ct)
    {
        // ── W51: CİHAZ ÖNCESİ AŞAMALARIN SÜRESİ ─────────────────────────────────────
        // Ölçüm 2026-09-08 22:45:08: bir satış uçtan uca 20,2 sn sürdü, cihaz tarafı 7,8 sn'ydi.
        // Aradaki 10,9 sn cihaza gitmeden ÖNCE harcandı ve günlükte o aralığa dair tek satır
        // yoktu — hangi aşamada olduğunu söyleyemedik. Buradaki sayılar YALNIZ ölçüm: hiçbir
        // karar, sıra ya da zaman aşımı değişmiyor.
        OlcenSessionProvider.Sifirla();
        var tBas = _now();

        // 1. Tutarı çek — GET = ACK. Reddedilirse/ağ hatası → terminale GİTME.
        var fetch = await _amounts.FetchAsync(req.PaymentId, ct);
        var tutarMs = (long)(_now() - tBas).TotalMilliseconds;
        if (fetch is PaymentDetailResult.Rejected rej)
        {
            // GET reddinde deneme ACCEPTED olmadı (platform durumu otorite) → bildirim YOK, yalnız kasaya ret.
            _log("[yerel] GET reddi — terminale gidilmedi", new { req.PaymentId, reason = rej.Reason.ToString(), rej.StatusCode });
            // ErrorCondition `PaymentRestriction`: tutar alınamadığında `FP3_Payment` ÇAĞRILMADI, yani
            // sonuç kesindir. `UnreachableHost` demek (eski hâli) belirsiz sınıfına sokup denemeyi
            // gereksiz yere çözüm döngüsünde bırakırdı; sebep zaten `Restomenum.reason`'da yazılı.
            return SaleToPoiResponseBuilder.BuildFailure(req, "PaymentRestriction", $"GET:{rej.Reason}",
                _now(), RestomenumReasons.AmountFetchFailed);
        }
        var d = ((PaymentDetailResult.Ok)fetch).Detail;

        // 2. Kalemleri departmana çöz + kuruş dağıtımıyla fiş satırlarına dök.
        // ── W29: EŞLEMEYİ SATIŞTAN HEMEN ÖNCE TAZELE ────────────────────────────────
        // K-21'in ("satış anında çekme yok") bilinçli geri alınması. Sahada ölçüldü: operatör bir
        // eşleme hatasını düzeltti, hemen denedi, ~30 dakikalık yoklama aralığı yüzünden ajan hâlâ
        // eski sürümdeydi ve aynı reddi aldı — yani düzeltme DOĞRULANAMIYORDU.
        //
        // K-21'in koruduğu şey duruyor: bu çağrı satışı ASLA düşürmez ve beklemez (sert zaman
        // aşımı, hata yutulur, diskteki eşlemeyle devam). Bağımlılık gerekli değil FIRSATÇI.
        var tEsleme = _now();
        if (_mappingRefresher is not null)
        {
            // Sözleşme "istisna fırlatmaz" diyor ama BURADA DA YAKALIYORUZ. Sebebi ilkesel:
            // satışın hayatta kalması, yapılandırma kanalının uslu davranmasına bağlı OLMAMALI.
            // Test bunu yakaladı — sarmalayıcı yutuyordu ama arayüzün başka bir uygulaması
            // yutmayabilir ve o gün ödeme düşerdi.
            try
            {
                var yeniSurum = await _mappingRefresher.EnsureFreshAsync(ct);
                if (yeniSurum is int v)
                    _log("[yerel] satış öncesi eşleme tazelendi", new { req.PaymentId, surum = v });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log("[yerel] satış öncesi eşleme tazelenemedi — diskteki sürümle DEVAM",
                    new { req.PaymentId, error = e.Message });
            }
        }

        var lines = new List<FiscalLine>();
        // Ayrışan kalemler — YOKSA null kalır ve yanıta alan KONMAZ ("çelişki yok" ile
        // "boş liste" ayrı beyanlar olmasın diye).
        List<TaxMismatch>? taxMismatches = null;
        foreach (var item in d.Items)
        {
            var match = _departments.Resolve(item.ProductCode, item.CategoryId);
            if (match is null)
            {
                // GET başarılıydı (deneme ACCEPTED) → platforma bildir ki takılı kalmasın. Ürünü SÖYLE.
                var reddi = SaleToPoiResponseBuilder.BuildFailure(req, "PaymentRestriction",
                    $"PRODUCT_UNMAPPED:{item.ProductCode}", _now(), RestomenumReasons.ProductUnmapped,
                    productCode: item.ProductCode);
                await NotifyAsync(req.PaymentId, reddi, ct);
                _log("[yerel] eşlenmemiş ürün — terminale gidilmedi",
                    new { req.PaymentId, item.ProductCode, item.ProductLabel });
                return reddi;
            }
            var m = match.Value;

            // Sessiz mali sapma koruması (§30.12): oranı TAMAMEN departman belirliyor (fiş satırı VatRate=0,
            // cihaz KDV'yi departmandan türetir), TaxCode ise terminale hiç gitmiyor. Departman GET'teki
            // TaxCode ile çelişirse fişte bir oran, defterde başka oran olur ve HİÇBİR kapı yakalamaz. Burada
            // yakala: TaxCode YÜZDE-string ("10"), departman oranı BAZ-PUAN (1000) — birim dönüşümüyle
            // karşılaştır. Oran bilinmiyorsa (cihaz tablosu yok) ya da TaxCode sayı değilse doğrulama atlanır
            // (naif değil: yalnız gerçek, sayısal çelişkide ret; her kalemi düşürmez).
            // ── K-30: ÇELİŞKİ ARTIK REDDETMİYOR, BİLDİRİYOR ─────────────────────────
            // Kullanıcı kararı: "Eklenti ürünlerin kdv oranını değiştirmesin zaten, terminal
            // uygulaması bizim beyanımıza göre işlesin." Yani ESAS olan cihaz eşlemesi; fişe
            // bölümün oranı basılır ve bu istenen davranıştır — mali belge ÖKC fişidir.
            //
            // Önceki hâl (ret) fişle defteri hizada tutuyordu ama işletmeyi çalıştırmıyordu:
            // 88 üründen 87'si satılamıyordu. Sessizce geçirmek de olmazdı — hizasızlık
            // görünmez kalırdı. Üçüncü yol: GEÇİR ve HER SATIRI BİLDİR.
            //
            // Kural (`TaxRule`) DEĞİŞMEDİ, yalnız sonucu değişti: ret değil kayıt. Kuru prova
            // (`--check-tax`) da aynı çağrıyı yapıyor, iki taraf ıraksayamaz.
            if (TaxRule.Conflicts(item.TaxCode, m.TaxRateBasisPoints)
                && m.TaxRateBasisPoints is int deptRate
                && int.TryParse(item.TaxCode, out var urunYuzde))
            {
                (taxMismatches ??= new()).Add(new TaxMismatch(
                    item.ProductCode, urunYuzde * 100, deptRate, m.Index));
                _log("[uyarı] KDV çelişkisi — fişe BÖLÜMÜN oranı basılacak (K-30: ret değil bildirim)",
                    new { req.PaymentId, item.ProductCode, urunOrani = urunYuzde * 100,
                          bolumOrani = deptRate, dept = m.Index });
            }
            // ── W63: SATIR İNDİRİMİ KAPISI — cihaza GİTMEDEN ────────────────────────
            // Sözleşme: `RestomenumExt.DiscountAmount` KURUŞ tam sayısı ve `0 < x ≤ ItemAmount`
            // (ikisi de SATIR TOPLAMI). Geçersiz bir indirim sessizce kırpılamaz ya da yok
            // sayılamaz: kırpmak müşteriden yanlış tutar tahsil etmek, yok saymak indirimi
            // uygulamamak olurdu — ikisi de geri alınamaz mali kayıt üretir.
            var indirimHatasi = IndirimDogrula(item, d.Exponent);
            if (indirimHatasi is not null)
            {
                var reddi = SaleToPoiResponseBuilder.BuildFailure(req, "PaymentRestriction",
                    // Ürün kodu METNE de giriyor (`PRODUCT_UNMAPPED` ile aynı desen): operatör
                    // kasadaki tek satırdan HANGİ kalemin reddedildiğini görebilmeli; ayrı alanı
                    // okumak için gövdeyi açmak zorunda kalmasın.
                    $"DISCOUNT_INVALID:{item.ProductCode}:{indirimHatasi}", _now(), RestomenumReasons.DiscountInvalid,
                    productCode: item.ProductCode);
                await NotifyAsync(req.PaymentId, reddi, ct);
                _log("[yerel] geçersiz satır indirimi — terminale gidilmedi", new
                {
                    req.PaymentId, item.ProductCode, item.ItemAmountMinor,
                    indirim = item.DiscountAmountRaw?.ToString() ?? (item.DiscountMalformed ? "(sayı değil)" : "(yok)"),
                    hata = indirimHatasi,
                });
                return reddi;
            }

            lines.AddRange(FiscalLineBuilder.Build(item, m.Index));
        }

        // 3. Ödeme yöntemi → cihaz ödeme tipi (§20-I tek eksen). Platform PaymentMethodId'yi DİKTE eder
        // ve daima dolu gelir; eklenti eşlemesi onu GmpPaymentType'a (1/4/16) çevirir. Eşleme yoksa ya da
        // değeri bilinmeyen bir tipse: terminale GİTME, hangi yöntem olduğunu söyle (kart↔nakit güvenliği
        // eklenti UI'ında kurulur; burada eşlenmemiş/geçersiz yöntemi fail-closed reddederiz).
        var gmpPaymentType = _paymentMethods.Resolve(d.PaymentMethodId);
        if (gmpPaymentType is null || !GmpPaymentTypes.IsKnown(gmpPaymentType.Value))
        {
            var reddi = SaleToPoiResponseBuilder.BuildFailure(req, "PaymentRestriction",
                $"PAYMENT_METHOD_UNMAPPED:{d.PaymentMethodId}", _now(),
                RestomenumReasons.PaymentMethodUnmapped);
            await NotifyAsync(req.PaymentId, reddi, ct);
            _log("[yerel] eşlenmemiş/geçersiz ödeme yöntemi — terminale gidilmedi",
                new { req.PaymentId, d.PaymentMethodId, resolved = gmpPaymentType });
            return reddi;
        }

        // ── W61: BANKA KİMLİĞİ YALNIZ BANKA BACAĞI OLAN TİPTE TAŞINIR ────────────────
        // `bankBkmId` zarftan geliyor ve ayrıştırıcıda YALNIZ aralık doğrulanıyor
        // (`SaleToPoiRequestParser.cs:88` — > 0 ve cihaz alanına sığıyor mu). Tip ile çapraz
        // kontrol YOKTU: `{tip: 32 hediye çeki, bankBkmId: 46}` cihaza 46 ile giderdi ve cihazın
        // ne yapacağı ÖLÇÜLMEDİ — banka bacağı olmayan bir ödemede banka seçmeye çalışması
        // mümkün.
        //
        // REDDETMİYORUZ: kasanın göndermemesi gereken bir alan yüzünden satışı düşürmek, kasayı
        // manuel ödemeye indirir (kullanıcının değişmez şartı). GÖRÜNÜR ŞEKİLDE YOK SAYIYORUZ:
        // değer düşer, sebebi loga yazılır. Loga yalnız yöntem kimliği ve tip girer — kart/kimlik
        // verisi YOK.
        var bankBkmId = req.BankBkmId;
        if (bankBkmId is int gelenBkm && !GmpPaymentTypes.HasBankLeg(gmpPaymentType.Value))
        {
            _log("[yerel] banka kimliği YOK SAYILDI — bu ödeme tipinin banka bacağı yok", new
            {
                req.PaymentId, d.PaymentMethodId, tip = gmpPaymentType.Value, gelenBkm,
            });
            bankBkmId = null;
        }

        // 4. SaleRequest kur + orkestratör (dedupe/durum-makinesi/UNKNOWN korunur; CommandId = ServiceID).
        var sale = new SaleRequest(
            CommandId: req.ServiceId, PaymentId: req.PaymentId, TerminalId: req.PoiId,
            AmountMinor: d.RequestedAmountMinor, Currency: d.Currency, Exponent: d.Exponent,
            ProviderPluginId: null, FiscalLines: lines, PaymentType: gmpPaymentType.Value,
            // Kısmi tahsilat için: açık fişin sahibi (oturum) ve karşılaştırma toplamı.
            // Oturum kimliği YOKSA (eski platform) devam yolu kapalı kalır — taşıma katmanı
            // açık fişi "başka satış" sayar. Emin olmadan fişe ödeme eklemeyiz.
            SaleSessionId: req.SaleSessionId, SaleTotalMinor: d.SaleTotalAmountMinor,
            // Bankayı KASİYER seçer, biz taşırız. Zarfta yoksa cihaz seçer (bugünkü davranış).
            // W61: banka bacağı olmayan tipte bu değer yukarıda düşürülmüş olur.
            BankBkmId: bankBkmId);

        var eslemeMs = (long)(_now() - tEsleme).TotalMilliseconds;

        // Terminal başına TEK işlem (değişmez #4): eşzamanlı iki satış cihaz fişini bozar.
        AgentOutcome outcome;
        var tCihaz = _now();
        // W109: TAVANLI bekleme. Tavansız beklemek, kasanın 180 sn'lik kapısında cevapsız
        // kalmak demekti — üstelik cihaza hiç gitmemiş bir istek için.
        if (!await _islemKilidi.WaitAsync(_satisKilitTavani, ct))
        {
            // Cihaza HİÇ GİDİLMEDİ: para hareket etmedi, kesin ret GÜVENLİ. Kasanın "bilinmiyor"
            // ile uğraşması yerine net bir "şu an meşgul" alması her açıdan iyi.
            _log("[yerel] terminal kilidi alınamadı — ödeme cihaza GİTMEDİ", new
            {
                req.ServiceId, req.PaymentId, tavanSn = (int)_satisKilitTavani.TotalSeconds,
            });
            // `PaymentRestriction` = KESİN RET (platformun kesin-ret listesi). Burada kesinlik
            // HAK EDİLMİŞ: cihaza hiç gidilmedi, para hareket etmedi. `Busy` koşulu platformda
            // "bilinmiyor"a eşleniyor ve kasayı boş yere uzlaştırma turuna sokardı.
            // Gövde `paymentInvoked: false` taşır — "kart kesinlikle çekilmedi".
            return SaleToPoiResponseBuilder.BuildFailure(req, "PaymentRestriction", "TERMINAL_BUSY",
                _now(), reason: RestomenumReasons.TerminalBusy);
        }
        try { outcome = await _orch.HandleAsync(sale, d.ExpiresAtMs, ct); }
        finally { _islemKilidi.Release(); }
        var cihazMs = (long)(_now() - tCihaz).TotalMilliseconds;

        // ⚠️ `cihazMs` KİLİT BEKLEMESİNİ DE İÇERİR. Ayırmak için ölçümü kilidin içine almak
        // gerekirdi; o zaman da "cihaz mı meşguldü, biz mi bekledik" sorusu kaybolurdu. Kasiyerin
        // beklediği süre bu — bölmek değil, bütün olarak doğru sayı.
        var tBildirim = _now();

        // 4. Gövdeyi kur; ÖNCE platforma bildir, SONRA kasaya dön.
        var sonuc = ToTransportResult(outcome);
        var body = SaleToPoiResponseBuilder.BuildResult(req, sonuc, d.Exponent, _now(), taxMismatches);
        await NotifyAsync(req.PaymentId, body, ct);
        _log("[yerel] sonuç", new { req.PaymentId, decision = outcome.Decision.ToString(), state = outcome.State.ToString() });

        // Fiş bu ödemeyle KAPANDIYSA: satırların deftere yazılma anı budur (K-26).
        await FisKapandiBildirAsync(req, sonuc, ct);

        // ── W51b: DÖKÜM EN SONDA ────────────────────────────────────────────────────
        // İlk hâlinde bu satır cihaz çağrısından hemen SONRA yazılıyordu ve satışın son
        // parçasını — iki bildirimi — dışarıda bırakıyordu. Sahada ölçüldü (2026-09-08 23:08):
        // `toplamMs = 14.261` ama gerçek uçtan uca **15,76 sn**; fark 1,5 sn ve `oturumCagri`
        // yalnız 1 görünüyordu çünkü bildirimlerin oturum alımları satırdan sonra oluyordu.
        // Yani ölçüm aracının kendisi eksik ölçüyordu — W48'de düzelttiğimiz hatanın kardeşi.
        var bildirimMs = (long)(_now() - tBildirim).TotalMilliseconds;
        var oturum = OlcenSessionProvider.Son;
        _log("[yerel] süre dökümü", new
        {
            req.PaymentId,
            // Kasanın zarftaki damgasından ajanın isteği okumasına kadar. ⚠️ KASANIN saati;
            // ajanınkiyle aynı değilse negatif/şişkin çıkabilir (sahada −83 ms görüldü) — o
            // yüzden ayrı alan, `toplamMs`'e DAHİL DEĞİL.
            bekleMs = (long)(tBas - req.TimeStamp).TotalMilliseconds,
            tutarMs,
            eslemeMs,
            oturumMs = oturum?.ToplamMs ?? -1,          // -1 = ölçülmedi (0 ile karıştırılmasın)
            oturumCagri = oturum?.Cagri ?? 0,
            oturumHttp = oturum?.HttpDeneme ?? -1,      // Cagri'dan büyükse içeride tekrar olmuş
            jetonOmurSn = oturum?.OmurSn ?? -1,         // W53: sunucunun bildirdiği ömür; -1 = taze alım yok
            cihazMs,
            bildirimMs,                                  // sonuç + kapanış bildirimleri
            toplamMs = (long)(_now() - tBas).TotalMilliseconds,
        });
        return body;
    }

    /// <summary>
    /// W63 — satır indirimini doğrular. <c>null</c> = geçerli (indirim yok ya da kurallara uyuyor);
    /// dolu ise reddin İNSAN OKUR sebebi.
    ///
    /// <para><b>Sözleşme:</b> <c>DiscountAmount</c> KURUŞ tam sayısıdır ve <c>0 &lt; x ≤ ItemAmount</c>
    /// — ikisi de <b>SATIR TOPLAMI</b> (birim fiyat değil; <c>ItemAmount</c> otorite, birim fiyat ondan
    /// türetiliyor).</para>
    ///
    /// <para><b>Neden 0 GEÇERLİ sayılıyor:</b> sözleşme <c>0 &lt; x</c> diyor ama açıkça sıfır gelen bir
    /// indirim yanlış mali kayıt ÜRETMEZ — "indirim yok" ile aynı şeydir. Onu reddetmek, zararsız bir
    /// değer yüzünden satışı düşürüp kasayı manuel ödemeye indirmek olurdu (kullanıcının değişmez
    /// şartı). Negatif, ondalık ya da tutarı aşan değerler ise gerçekten reddedilir.</para>
    /// </summary>
    private static string? IndirimDogrula(SaleLine item, int exponent)
    {
        // "Alan var ama sayı değil" ≠ "alan yok". İlki sözleşme ihlali, ikincisi indirimsiz satış.
        if (item.DiscountMalformed) return "sayı değil";
        if (item.DiscountAmountRaw is not decimal ham) return null;   // alan yok → indirim yok

        if (ham < 0) return $"negatif ({ham})";

        // KURUŞ ALTI SAPMA. Değer ana birimde geliyor (`ItemAmount` gibi); `Exponent` ile kuruşa
        // çevrildiğinde TAM olmalı. `0.995` → 99,5 kuruş eder ve `Money.ToMinor` onu 100'e
        // yuvarlar — yani müşteriden yarım kuruş fazla alınır. Hangi yöne yuvarlarsak yuvarlayalım
        // biri yanlış; tek doğru davranış reddetmek.
        if (Money.ToWire(item.DiscountMinor, exponent) != ham)
            return $"kuruş altı sapma ({ham}) — {exponent} ondalık basamak bekleniyor";

        if (item.DiscountMinor > item.ItemAmountMinor)
            return $"satır toplamını aşıyor (indirim {item.DiscountMinor} > tutar {item.ItemAmountMinor})";
        return null;
    }

    /// <summary>
    /// Fiş KAPANDI bildirimi (K-26/P27) — ödemeler deftere burada, tek seferde yazılıyor.
    ///
    /// <para><b>Neden dayanıklı (outbox ÖNCE):</b> fiş cihazda GERÇEKTEN kapandı ve mali kayıt
    /// oluştu. Bildirim o an gidemezse o fişin ödemelerinin hiçbiri deftere yazılmaz — tahsilat
    /// yapılmış, satır yok. Satıştan farklı olarak bunu sonradan keşfedecek bir kurtarma turu da
    /// YOK: kapanmış fişi cihaza sorup "ödemeleri neydi" diye öğrenemeyiz.</para>
    ///
    /// <para>Tekilleştirme anahtarı <c>ticketId</c>: aynı fiş için ikinci bildirim güvenlidir
    /// (platform tekrar sayar). <c>paymentId</c> anahtar OLAMAZDI — bildirim bir denemeye değil
    /// bir FİŞE ait.</para>
    /// </summary>
    private async Task FisKapandiBildirAsync(SaleToPoiRequest req, TransportResult sonuc, CancellationToken ct)
    {
        if (!string.Equals(sonuc.TicketState, "CLOSED", StringComparison.Ordinal)) return;
        if (sonuc.TicketId is not string fisId)
        {
            // Kimlik yoksa bildirim GÖNDERİLMEZ: platform tekilleştirmeyi ticketId'ye dayıyor,
            // uydurulmuş bir kimlik aynı fişi iki kez yazdırabilirdi. Sessiz kalmıyoruz: alarm.
            _log("[yerel] fiş KAPANDI ama kimlik yok — bildirim gönderilmedi (ALARM)",
                new { req.PaymentId, req.PoiId });
            return;
        }

        var govde = SaleToPoiResponseBuilder.BuildTicketClosed(
            terminalId: req.PoiId, ticketId: fisId, saleSessionId: req.SaleSessionId,
            payments: sonuc.ClosedTicketPayments ?? Array.Empty<TicketPaymentRow>(),
            totalMinor: sonuc.DeviceTicketTotalMinor, paidMinor: sonuc.DevicePaidMinor);

        var eid = fisId + ":ticket-closed";
        _outbox.Enqueue(eid, req.PaymentId, OutboxKinds.TicketClosed, govde, "",
            anlikDenemeVar: true);   // W47
        // Gövde artık outbox'ta: gönderim garantisi oraya DEVREDİLDİ. `closed_tickets` yalnız
        // "bağ silindi ama gövde henüz yazılmadı" aralığını koruyordu, o aralık kapandı.
        _store.ConfirmTicketClosed(fisId);
        _log("[yerel] fiş KAPANDI", new
        {
            req.PaymentId, fisId, odemeSayisi = sonuc.ClosedTicketPayments?.Count ?? 0,
            cihazTahsil = sonuc.DevicePaidMinor, cihazToplam = sonuc.DeviceTicketTotalMinor,
        });
        try
        {
            var t0 = _now();
            var bildirim = await _notifier.NotifyTicketClosedAsync(govde, ct);
            var sureMs = (long)(_now() - t0).TotalMilliseconds;
            // W68: fiş kapanışı reddedilirse boşluk daha da ağır — kapanmış bir fişin ödemeleri
            // deftere hiç yazılmaz ve bunu keşfedecek bir kurtarma turu YOK (kapanmış fişi cihaza
            // sorup "ödemeleri neydi" diye öğrenemeyiz). Kayıt saklanır.
            if (bildirim.IsPermanentGap) _outbox.MarkPermanentlyRejected(eid);
            else if (bildirim.IsFinal) _outbox.Confirm(eid);
            else _outbox.MarkAttempt(eid);
            if (bildirim.IsProblem)
                _log("[yerel] fiş kapandı bildirimi SORUNU (alarm)", new
                {
                    req.PaymentId, fisId, outcome = bildirim.Outcome.ToString(),
                    bildirim.StatusCode, bildirim.Message, sureMs, oturumMs = bildirim.SessionMs, postMs = bildirim.PostMs,
                });
            // W46: AĞ HATASI "yazıldı" diye loglanıyordu. `IsProblem` false + `IsFinal` false
            // birlikte "gitmedi, kuyrukta kaldı" demek; alt daldaki başarı metnine düşüyordu.
            // Alanlar doğruyu söylüyordu (outcome=NetworkError, statusCode=0) ama BAŞLIK yanlıştı
            // ve günlüğü tarayan önce başlığı okur.
            else if (!bildirim.IsFinal)
                _log("[yerel] fiş kapandı bildirimi GİTMEDİ — outbox'ta kaldı (replay)", new
                {
                    req.PaymentId, fisId, outcome = bildirim.Outcome.ToString(),
                    bildirim.StatusCode, sureMs, oturumMs = bildirim.SessionMs, postMs = bildirim.PostMs,
                });
            else
                // BAŞARIYI DA YAZ. Bu yol W22'de atlanmıştı ve bedeli ölçüldü (2026-09-07 23:51):
                // ilk gerçek fiş kapanışında bildirimin gittiği görülüyordu ama platformun NE
                // dediği görünmüyordu — sessizlik hem "yazıldı" hem "tekrar sayıldı" olabilirdi.
                // Kapanış bildirimi fişin BÜTÜN ödemelerini taşıyor; en çok görünürlük gereken yer.
                _log("[yerel] fiş kapandı bildirimi yazıldı", new
                {
                    req.PaymentId, fisId, outcome = bildirim.Outcome.ToString(),
                    bildirim.StatusCode, state = bildirim.State ?? "(yok)",
                    replayed = bildirim.Replayed?.ToString() ?? "(yok)",
                    zatenYazilan = bildirim.AlreadyPosted?.ToString() ?? "(yok)",
                    sureMs, oturumMs = bildirim.SessionMs, postMs = bildirim.PostMs,
                });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log("[yerel] fiş kapandı bildirimi gönderilemedi — outbox'ta kaldı (replay)",
                new { req.PaymentId, fisId, error = e.Message });
        }
    }

    /// <summary>
    /// Gönderilemeyen kaydı üstel geri çekilmeyle erteler ve <b>takılı kalanı GÖRÜNÜR kılar</b>.
    ///
    /// <para>Uyarı 10. denemede ve sonra her 10'da bir: her turda loglamak, alarmın kendisini
    /// gürültüye çevirir ve okuyan kişi bir süre sonra bakmaz olur.</para>
    /// </summary>
    private void Ertele(OutboxEntry e)
    {
        var deneme = _outbox.MarkAttempt(e.EventId);
        if (deneme >= 10 && deneme % 10 == 0)
            _log("[uyarı] outbox kaydı gönderilemiyor — takılı kalıyor", new
            {
                tur = e.Status, anahtar = e.EventId, deneme,
                sonrakiDenemeSn = (int)Outbox.Bekleme(deneme).TotalSeconds,
            });
    }

    /// <summary>
    /// <b>W110 — ELLE FİŞ TAMAMLAMA</b> (<c>TicketFinish</c> zarfı).
    ///
    /// <para><b>Neden kasadan tetikleniyor:</b> otomatik süpürme sekiz denemeden sonra susup
    /// insanı bekliyor (W108) — kâğıt gerçekten bittiyse cihazı süresiz meşgul etmek kasiyeri de
    /// kilitlerdi. <b>Kâğıdı değiştiren kişi doğru anı bilir, süpürme bilmez.</b></para>
    ///
    /// <para><b>Üç koruma:</b>
    /// ① <b>Tekrar anahtarı</b> (<c>ticketFinishId</c>) — ikinci POST cihaza GİTMEZ, saklanan
    ///   sonuç replay edilir. Anahtarsız istek ayrıştırıcıda reddedilir.
    /// ② <b>Kilit tavanı</b> — satışla aynı kilit, aynı tavan; satış sürüyorsa beklenmez.
    /// ③ <b>Süre bütçesi</b> — kurtarma zinciri de 310 sn'ye çıkabiliyor ve kasanın 180 sn'lik
    ///   kapısını aşardı; düzeltmeye çalıştığımız arızanın aynısını bu kez kurtarma yolunda
    ///   üretirdi.</para>
    /// </summary>
    public async Task<string> HandleTicketFinishAsync(TicketFinishRequest req, CancellationToken ct = default)
    {
        // ① TEKRAR ANAHTARI. `kind = finish`: açılış kurtarması yalnız `sale` okur, bu yüzden
        // bir tamamlama isteği ASLA "yarım kalmış satış" sanılmaz.
        var kayit = _store.Save(req.TicketFinishId, "", req.PoiId,
            _now().ToUnixTimeMilliseconds() + 86_400_000, kind: CommandKinds.Finish);
        if (kayit is SaveResult.Duplicate d && d.Command.State.IsFinal() && d.Command.ResultJson is string saklanan)
        {
            _log("[bitir] tekrar gelen istek — saklanan sonuç replay edildi",
                new { req.TicketFinishId, req.PoiId });
            return saklanan;
        }

        if (_transport is not IStuckTicketFinisher tamamlayici)
        {
            _log("[bitir] bu taşıma fiş tamamlamayı DESTEKLEMİYOR", new { req.TicketFinishId });
            return TicketFinishParser.Govde(req,
                new StuckTicketResult(false, false, false,
                    new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "NOT_SUPPORTED", 0), 0),
                not: "CAPABILITY_NOT_SUPPORTED");
        }

        // ② KİLİT — satışla AYNI kilit ve AYNI tavan. Satış her zaman öncelikli.
        if (!await _islemKilidi.WaitAsync(_satisKilitTavani, ct))
        {
            _log("[bitir] terminal kilidi alınamadı — cihaza GİDİLMEDİ", new { req.TicketFinishId });
            return TicketFinishParser.Govde(req,
                new StuckTicketResult(false, false, Meshgul: true,
                    new FisBitirmePlani(FisBitirmeAdimi.Dokunma, RestomenumReasons.TerminalBusy, 0), 0),
                not: RestomenumReasons.TerminalBusy);
        }

        StuckTicketResult sonuc;
        try
        {
            // ③ BÜTÇE — kurtarma zinciri de kasanın kapısını aşmamalı.
            sonuc = tamamlayici.FinishStuckTicket(uygula: true, butce: new BaskiButcesi(BaskiButcesi.Varsayilan));
        }
        catch (Exception e)
        {
            _log("[bitir] tamamlama hata verdi", new { req.TicketFinishId, hata = e.Message });
            sonuc = new StuckTicketResult(false, false, false,
                new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "EXCEPTION", 0), 0, "EXCEPTION", e.GetType().Name);
        }
        finally { _islemKilidi.Release(); }

        if (sonuc.Kapandi) _store.ClearPendingFinish(sonuc.TicketId ?? req.TicketFinishId);

        _log("[bitir] elle tamamlama sonucu", new
        {
            req.TicketFinishId, req.PoiId, kapandi = sonuc.Kapandi, acikDegil = sonuc.FisAcikDegil,
            bayraklar = TicketFlags.Yazdirilabilir(sonuc.Bayraklar),
            ureticiAdimi = sonuc.Plan.UreticiAdimi, neden = sonuc.Plan.Neden,
            adim = sonuc.BasarisizAdim, kod = sonuc.Kod,
        });

        var govde = TicketFinishParser.Govde(req, sonuc,
            sonuc.Kapandi || sonuc.FisAcikDegil ? null : sonuc.Plan.Neden);

        // ── SONUÇ YALNIZ KESİNSE SAKLANIR ─────────────────────────────────────────
        // Fiş kapandıysa ya da zaten açık değilse sonuç KESİNDİR ve ikinci POST onu replay
        // etmeli — cihaza tekrar gitmek, `FP3_Start` ile yoklama fişi açtırıp MALİ NUMARA
        // tüketmek olurdu.
        //
        // ⚠️ Başarısızlıkta kayıt BİLEREK `RECEIVED`da bırakılır: kâğıt değiştirilip komut
        // tekrar gönderildiğinde cihaza YENİDEN gidilebilmeli. Başarısız bir denemeyi
        // "kesin sonuç" diye saklamak, elle kurtarmayı tek denemeye indirirdi — oysa bu
        // zarfın bütün varlık sebebi insanın doğru anı seçmesi.
        if (sonuc.Kapandi || sonuc.FisAcikDegil)
        {
            _store.Advance(req.TicketFinishId, CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL);
            _store.Advance(req.TicketFinishId, CommandState.SENT_TO_TERMINAL, CommandState.COMPLETED,
                resultJson: govde);
        }
        return govde;
    }

    /// <summary>
    /// <b>W106 — SALT-OKUNUR fiş durumu sorgusu.</b> Kasa bayat kaydını bununla tazeler.
    ///
    /// <para><b><c>async</c> DEĞİL ve bilerek:</b> hiçbir I/O yok — ne cihaz, ne ağ, ne kilit.
    /// Cevap tamamen defterden. Sahte bir <c>Task</c> döndürmek, çağırana "bu bekleyebilir"
    /// diye yanlış bir beklenti kurardı; oysa bu çağrı satışın ortasında bile anında döner.</para>
    ///
    /// <para><b>Neden cihaza gitmiyor:</b> <c>FP3_Start</c> açık fiş YOKKEN yeni fiş açar ve
    /// kapatılsa bile mali numarayı tüketir. Her masa kapanışında bir sorgu = her sorguda bir
    /// mali numara. Ayrıca cihaza gitmek terminal kilidini alır ve satışı geciktirirdi.</para>
    /// </summary>
    public string HandleTicketQuery(TicketQueryRequest istek)
    {
        // W115: işlem kilidi tutuluyorsa cihazda uçuşta bir iş VAR. `CurrentCount == 0` =
        // semafor alınmış. Kilidi ALMIYORUZ — sorgunun satışı beklemesi ya da geciktirmesi
        // kabul edilemez; yalnız o anki sayacı okuyoruz.
        var sonuc = TicketQueryHandler.Cevapla(_store, istek, cihazMesgul: _islemKilidi.CurrentCount == 0);
        _log("[sorgu] fiş durumu", new
        {
            istek.PoiId, istenen = istek.TicketId ?? "(açık fiş)", durum = sonuc.Durum,
            fis = sonuc.TicketId, defter = sonuc.LedgerWritten, bekleyen = sonuc.PendingFinishAttempts,
        });
        return TicketQueryHandler.Govde(istek, sonuc, _now());
    }

    /// <summary>
    /// <b>W105 — CİHAZDA YARIM KALMIŞ FİŞİ OTOMATİK TAMAMLAR.</b>
    ///
    /// <para><b>Neden var:</b> baskı zinciri patlayınca fiş ÜZERİNDE GERÇEK PARAYLA açık kalıyor
    /// ve bugüne kadar onu tekrar deneyecek hiçbir şey yoktu. Ajan çalışmaya devam ediyor, kasa
    /// masayı kapatamıyor, arıza insan müdahalesine kadar sürüyordu — 2026-09-09'da
    /// <b>83 dakika</b> (23:07 → 00:30).</para>
    ///
    /// <para><b>Neden YOKLAMA değil:</b> cihaza periyodik "açık fiş var mı" diye sormak,
    /// Türkiye'de NORMAL olan kısmi ödeme aradurumunu da her turda cihaza sordururdu — kasiyer
    /// ikinci bacağı eklerken cihaz boş yere meşgul edilirdi. Tetik <b>kanıttır</b>: kayıt yalnız
    /// baskı GERÇEKTEN patladığında yazılır.</para>
    ///
    /// <para><b>Üç güvenlik şartı:</b>
    /// ① <b>Satış her zaman öncelikli</b> — cihaz kilidi <c>Wait(0)</c> ile alınır, meşgulse tur
    ///   ATLANIR (beklemek, satışın önüne geçmek olurdu). Ayrıca kaydın kendi bekleme süresi var
    ///   (ilk deneme +15 sn): baskıdan saniyesinde üstüne gitmek aynı duvara toslamaktır.
    /// ② <b>Geri çekilme ve alarm</b> — her başarısız deneme üstel olarak ertelenir
    ///   (<see cref="Outbox.Bekleme"/> ile AYNI eğri) ve <see cref="TamamlamaAlarmEsigi"/>'nden
    ///   sonra ses yükselir. <b>Vazgeçme YOK:</b> fişte para var.
    /// ③ <b>Ödemesi tamamlanmamış fişe DOKUNULMAZ</b> ve <c>VoidAll</c> hiçbir dalda yok — kararı
    ///   <see cref="TicketFinish.Planla"/> veriyor, bu tur yalnız onu çalıştırıyor.</para>
    ///
    /// <para><b>Cihaz meşgulse başarısızlık SAYILMAZ:</b> <c>RECV_BUSY</c>/zaman aşımında fiş
    /// hakkında hiçbir şey öğrenilmemiştir; alarm yükseltmek "denendi ve olmadı" demek olurdu.</para>
    /// </summary>
    private void YarimFisleriTamamla()
    {
        if (_transport is not IStuckTicketFinisher tamamlayici) return;
        // Saat turun BAŞINDA bir kez okunur ve her yazmaya AYNI değer geçilir. Deponun kendi
        // varsayılanı `DateTimeOffset.UtcNow`; ona bırakmak, sınıfın enjekte edilmiş saatini
        // (kasa saat farkı düzeltmesi burada sürülüyor) sessizce baypas etmek olurdu.
        var simdi = _now().ToUnixTimeMilliseconds();
        var bekleyenler = _store.PendingFinishes(simdi);
        if (bekleyenler.Count == 0) return;

        foreach (var f in bekleyenler)
        {
            // ŞART ①: satış sürüyorsa CİHAZA GİDİLMEZ. Beklemek yok — bir sonraki tur var.
            using var kilit = TryAcquire();
            if (kilit is null)
            {
                _log("[tamamla] cihaz satışta — tur atlandı", new { f.TicketId, f.Attempts });
                return;
            }

            StuckTicketResult s;
            // W121: denemenin BEDELİ ÖLÇÜLÜR — sınıflandırmayı bu belirliyor, kod değil.
            // Ölçüm ENJEKTE EDİLMİŞ saatle (`_now`), `Stopwatch` ile DEĞİL: kronometre gerçek
            // zamanı okur ve testte 15 saniyeyi geçmek için gerçekten 15 saniye beklemek
            // gerekirdi. Sınıflandırmanın kendisi test edilebilir olmalı — yoksa eşik sessizce
            // kayar ve kimse fark etmez.
            var baslangic = _now();
            // W110: süpürme denemesine de BÜTÇE. Kasa beklemiyor ama cihaz KİLİTLİ: bütçesiz
            // bir deneme cihazı 310 sn'ye kadar tutabilir ve o sırada gelen satış kilit
            // tavanını (150 sn) doldurup `TERMINAL_BUSY` alır. Bütçeyle en kötü tutma ~175 sn.
            // Kesilen deneme kaybolmaz — geri çekilmeyle zaten tekrar denenecek.
            try { s = tamamlayici.FinishStuckTicket(uygula: true, butce: new BaskiButcesi(BaskiButcesi.Varsayilan)); }
            catch (Exception e)
            {
                // Kayıt DURUYOR. Silmek, üzerinde para olan bir fişi unutmak olurdu.
                // İstisna PAHALI sayılır: ne olduğunu bilmiyoruz, ne kadar sürdüğünü de.
                var n = _store.MarkFinishAttempt(f.TicketId, "EXCEPTION", e.GetType().Name, simdi,
                    Outbox.Bekleme(f.Attempts + 1));
                _log("[tamamla] deneme hata verdi — kayıt duruyor", new { f.TicketId, deneme = n, hata = e.Message });
                continue;
            }

            if (s.FisAcikDegil)
            {
                // Fiş başka bir yoldan kapanmış (kasiyer, önceki tur). "Kapandı" diye deftere
                // yazmıyoruz — bu tur onu kapatmadı ve kapanışı ölçmedi. Yalnız kaydı düşürüyoruz.
                _store.ClearPendingFinish(f.TicketId);
                _log("[tamamla] cihazda açık fiş yok — bekleyen kayıt düşürüldü", new { f.TicketId });
                continue;
            }

            if (s.Kapandi)
            {
                // Defter işini taşıma yaptı (`TicketFinishLedger.Yaz`), bekleyen kayıt orada düştü.
                // Yine de savunmacı: taşıma bağı çözemediyse kayıt burada düşer, sonsuz dönmesin.
                _store.ClearPendingFinish(f.TicketId);
                _log("[tamamla] ✓ yarım kalmış fiş TAMAMLANDI", new
                {
                    f.TicketId, deneme = f.Attempts + 1, ureticiAdimi = s.Plan.UreticiAdimi,
                    bayraklar = TicketFlags.Yazdirilabilir(s.Bayraklar),
                    toplam = s.ToplamMinor, tahsil = s.TahsilMinor,
                });
                continue;
            }

            // ŞART ②: ertelenir, sayılır; eşiği aşınca ses yükselir. Vazgeçilmez.
            var sure = _now() - baslangic;
            var deneme = _store.MarkFinishAttempt(f.TicketId, s.BasarisizAdim, s.Kod, simdi,
                TamamlamaAraligi(f.Attempts + 1, sure));

            // W108/W114: otomatik pencere YALNIZ PAHALI denemeler için dolar. Cihaz hızlı ve
            // kesin cevap veriyorsa (ör. `0x0020` kâğıt yok) denemek cihazı rehin almaz ve
            // kullanıcının istediği gibi "başarılı olana kadar" sürer.
            if (deneme >= TamamlamaOtomatikSiniri && PahaliDeneme(sure))
            {
                _store.DeferFinish(f.TicketId, TamamlamaErtelemesi, simdi);
                _log("[uyarı] yarım fiş OTOMATİK tamamlanamadı — otomatik denemeler DURDURULDU " +
                    "(kayıt SİLİNMEDİ, üzerinde PARA VAR). Kâğıt/yazıcı düzeltilince elle tamamlayın: " +
                    "--finish-ticket --onayla", new
                    {
                        f.TicketId, deneme, adim = s.BasarisizAdim, kod = s.Kod,
                        sonrakiOtomatikSaat = (int)TamamlamaErtelemesi.TotalHours,
                    });
                continue;
            }
            var veri = new
            {
                f.TicketId, deneme, adim = s.BasarisizAdim, kod = s.Kod,
                neden = s.Plan.Neden, ureticiAdimi = s.Plan.UreticiAdimi,
                // W116: ucuz başarısızlıkta sabit 10 sn, pahalıda üstel eğri.
                sureMs = (int)sure.TotalMilliseconds,
                bedel = PahaliDeneme(sure) ? "PAHALI" : "ucuz",
                sonrakiDenemeSn = (int)TamamlamaAraligi(deneme, sure).TotalSeconds,
            };

            if (s.Meshgul)
                // Denenmedi bile: cihaz cevap vermedi. Bunu "başarısız" saymak, olmamış bir
                // denemeyi alarma çevirmek olurdu.
                _log("[tamamla] cihaz meşgul/cevapsız — ertelendi (alarm DEĞİL)", veri);
            else if (s.Plan.Adim == FisBitirmeAdimi.Dokunma)
                // ŞART ③: ödemesi eksik ya da okuma bozuk. Otomatik yol burada DURUR ve durmalı;
                // kalanı tahsil etmek kasanın kararıdır, bir bakım turunun değil.
                _log("[uyarı] yarım fiş TAMAMLANAMAZ — dokunulmadı, insan kararı gerekiyor", veri);
            else if (deneme >= TamamlamaAlarmEsigi)
                _log("[uyarı] yarım fiş {0} denemedir kapanmıyor — ÜZERİNDE PARA VAR, yazıcıyı kontrol edin"
                    .Replace("{0}", deneme.ToString()), veri);
            else
                _log("[tamamla] deneme başarısız — ertelendi", veri);
        }
    }

    /// <summary>
    /// Fişi kapanmış ama kapanış gövdesi outbox'a HİÇ yazılamamış vakaları kurtarır.
    ///
    /// <para><b>Hangi aralık:</b> taşıma katmanı fişi kapatıp bağı sildikten sonra, üst katman
    /// gövdeyi kurup outbox'a yazana kadar süreç ölürse o fiş hiçbir kuyrukta görünmez —
    /// tahsilat yapılmış, satır yok, ve bunu keşfedecek başka bir yol da yok. Kalıcı
    /// <c>closed_tickets</c> kaydı bu boşluğun tek izidir.</para>
    ///
    /// <para>Gövde kayıttan YENİDEN kurulur: ödemeler <c>ticket_payments</c>'ta, tutarlar
    /// kapanış anında okunmuş hâliyle kayıtta. Cihaza tekrar sorulmaz — fiş kapandı, orada
    /// sorulacak bir şey kalmadı.</para>
    /// </summary>
    private void KayipKapanislariTopla()
    {
        // W76: YALNIZ eski kapanışlar. Normal akışta kapanış ile bildirimin outbox'a yazılması
        // arasında ~800 ms var; drain o aralıkta dönerse satırı "kayıp" sanıp gövdeyi ikinci kez
        // gönderiyordu (sahada görüldü 15:44:50). Bu kurtarma "süreç öldü" vakası için var ve o
        // 800 ms'de çözülmez — bekleyerek hiçbir şey kaybetmiyoruz.
        var esik = _now().AddSeconds(-KayipKapanisYasiSn).ToUnixTimeMilliseconds();
        foreach (var k in _store.PendingClosedTickets(esik))
        {
            try
            {
                var govde = SaleToPoiResponseBuilder.BuildTicketClosed(
                    terminalId: k.TerminalId, ticketId: k.TicketId, saleSessionId: k.SaleSessionId,
                    payments: _store.ReadTicketPayments(k.TicketId),
                    totalMinor: k.TotalMinor, paidMinor: k.PaidMinor);
                // paymentId burada YOK (kapanış bir denemeye ait değil) ve replay türe göre
                // yönleniyor, paymentId'ye bakmıyor.
                _outbox.Enqueue(k.TicketId + ":ticket-closed", "", OutboxKinds.TicketClosed, govde, "");
                _store.ConfirmTicketClosed(k.TicketId);
                _log("[yerel] yarım kalmış fiş kapanışı kurtarıldı", new { k.TicketId, k.TerminalId });
            }
            catch (Exception e)
            {
                // Kayıt DURUYOR: bir sonraki turda tekrar denenir. Silmek, tahsilatı kalıcı
                // olarak deftersiz bırakmak olurdu.
                _log("[yerel] fiş kapanışı kurtarılamadı — kayıt duruyor", new { k.TicketId, error = e.Message });
            }
        }
    }

    /// <summary>
    /// Outbox'ta bekleyen bildirimleri (ağ/429 nedeniyle gönderilememişler) yeniden dener. Worker
    /// açılışta ve periyodik çağırır — WSS'te oturum bağlanınca yapılan drain'in yerini alır.
    /// </summary>
    /// <param name="ignoreBackoff">
    /// Bağlantı yeniden kurulduğunda <c>true</c>: geri çekilme BİR TURLUK atlanır. Bekleme sebebi
    /// ağ kesintisiyse, ağ geri geldiği anda beklemenin anlamı kalmaz.
    /// </param>
    public async Task DrainOutboxAsync(CancellationToken ct = default, bool ignoreBackoff = false)
    {
        // EN ÖNCE cihazda yarım kalmış fişler: kapanış gövdesi ancak fiş KAPANDIKTAN sonra doğar.
        YarimFisleriTamamla();

        // Sonra yarım kalmış kapanışlar: gövdesi hiç kurulamamış fişler outbox'ta görünmez.
        KayipKapanislariTopla();

        foreach (var e in _outbox.Pending(ignoreBackoff: ignoreBackoff))
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                // TÜRE GÖRE UÇ: fiş iptali AYRI uca gider (paymentId ile adreslenmiyor).
                // Hepsini ödeme ucuna göndermek, replay edilen her iptali 404/409'a düşürürdü.
                var res = e.Status switch
                {
                    OutboxKinds.TicketCancel => await _notifier.NotifyTicketCancelAsync(e.PayloadJson, ct),
                    OutboxKinds.TicketClosed => await _notifier.NotifyTicketClosedAsync(e.PayloadJson, ct),
                    _ => await _notifier.NotifyAsync(e.PaymentId, e.PayloadJson, ct),
                };
                // W68: "tekrar anlamsız" ≠ "kayıt silinsin". 400'de kayıt DAMGALANIR, silinmez —
                // platformda karşılığı yok ve kendiliğinden oluşmayacak.
                if (res.IsPermanentGap) _outbox.MarkPermanentlyRejected(e.EventId);
                else if (res.IsFinal) _outbox.Confirm(e.EventId);
                else Ertele(e);
                if (res.IsProblem)
                    _log("[yerel] outbox bildirim SORUNU (alarm)", new { e.PaymentId, outcome = res.Outcome.ToString(), res.StatusCode });
                else if (res.IsFinal)
                    _log("[yerel] outbox bildirimi yazıldı", new
                    {
                        e.PaymentId, tur = e.Status, outcome = res.Outcome.ToString(), res.StatusCode,
                        state = res.State ?? "(yok)",
                        replayed = res.Replayed?.ToString() ?? "(yok)",
                        zatenYazilan = res.AlreadyPosted?.ToString() ?? "(yok)",
                    });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Ertele(e);   // kalsın, geri çekilmeyle birlikte bir sonraki drain'de tekrar
            }
        }
    }

    /// <summary>Sonucu platforma bildir — dayanıklı ÖNCE (outbox), sonra POST. Ağ/429 → outbox'ta kalır, replay.</summary>
    private async Task<NotifyResult?> NotifyAsync(string paymentId, string body, CancellationToken ct)
    {
        var eid = paymentId + ":result";
        // dayanıklı yazım ÖNCE (INSERT OR IGNORE). W47: anlık deneme benim, drain araya girmesin.
        _outbox.Enqueue(eid, paymentId, OutboxKinds.Result, body, "", anlikDenemeVar: true);
        try
        {
            NotifyResult res;
            res = await _notifier.NotifyAsync(paymentId, body, ct);
            // W68: 400 → damgala ve SAKLA (defter boşluğu); diğer kesin sonuçlar → sil.
            if (res.IsPermanentGap) _outbox.MarkPermanentlyRejected(eid);
            else if (res.IsFinal) _outbox.Confirm(eid);
            else _outbox.MarkAttempt(eid);   // NetworkError/RateLimited → kalsın, arka plan replay eder
            if (res.IsProblem)
                _log("[yerel] bildirim SORUNU (alarm)", new { paymentId, outcome = res.Outcome.ToString(), res.StatusCode, res.Message });
            else
                // BAŞARIYI DA YAZ (W22). Yalnız sorunları loglamak, "bildirim gitti mi" sorusunu
                // sahada cevapsız bırakıyordu: sessizlik hem "her şey yolunda" hem "hiç denenmedi"
                // demek olabiliyordu. Platformun kendi kararı (`state`) da burada — biz "Approved"
                // deyip platform "REJECTED" yazdıysa ikisi ancak yan yana görülünce fark edilir.
                _log("[yerel] bildirim yazıldı", new
                {
                    paymentId, outcome = res.Outcome.ToString(), res.StatusCode,
                    state = res.State ?? "(yok)", reason = res.Reason ?? "(yok)",
                    // "yazıldı" ile "zaten yazılıydı" ayrımı: replay fırtınası ancak böyle görülür.
                    replayed = res.Replayed?.ToString() ?? "(yok)",
                });
            return res;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log("[yerel] bildirim gönderilemedi — outbox'ta kaldı (replay)", new { paymentId, error = e.Message });
            return null;
        }
    }

    /// <summary>
    /// Orkestratör kararını yanıt üreticisinin anladığı <see cref="TransportResult"/>'a çevirir.
    /// Approved/Declined/Replayed gerçek sonucu taşır; kalan kararlar (Expired/ClockUnsynced/RetryLater/
    /// Unresolved) terminale ulaşmamış ya da belirsiz — GÜVENLİ taraf <see cref="TransportOutcome.Unknown"/>
    /// (platformda unknown → operatör incelemesi; kasiyeri yeniden denemeye İTMEZ, çift-çekim yok).
    /// </summary>
    private static TransportResult ToTransportResult(AgentOutcome o) => o.Decision switch
    {
        AgentDecision.Approved or AgentDecision.Declined or AgentDecision.Replayed
            => o.Result ?? new TransportResult(TransportOutcome.Unknown, ProviderResultCode: o.Decision.ToString()),
        // Koşul, taşınabildiyse korunur (2086 → UnreachableHost); yoksa belirsiz.
        _ => new TransportResult(TransportOutcome.Unknown,
                ProviderResultCode: o.Note ?? o.Decision.ToString(),
                ErrorCondition: o.Result?.ErrorCondition,
                // Ödeme çağrılıp çağrılmadığı bilgisi KAYBOLMAZ; taşınamıyorsa güvenli taraf
                // "çağrıldı" (yanlış bir kesinlik yaymaktansa belirsiz kal).
                PaymentInvoked: o.Result?.PaymentInvoked ?? true,
                Reason: o.Result?.Reason,
                // ⚠️ `Info` BURADA DÜŞÜYORDU (W30). Sahada ölçüldü (2026-09-07 20:26): W25 doğru
                // çalıştı, yoklama "fişe para yazılmadı" dedi ve `info:NOT_LANDED` üretti — ama bu
                // dal yeni bir `TransportResult` kurarken `Info`yu KOPYALAMIYORDU. Sonuç: platform
                // P34 dalına hiç girmedi, deneme `bankReviewNeeded` almadan UNKNOWN'da kaldı ve
                // yoklama sürdü. `ErrorCondition` ve `Reason` taşındığı için hata GÖRÜNMEDİ:
                // gövde doğru görünüyor, yalnız bir alan eksik.
                Info: o.Result?.Info,
                UsedBankBkmId: o.Result?.UsedBankBkmId),
    };
}
