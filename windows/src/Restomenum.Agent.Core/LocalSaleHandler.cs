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
public sealed class LocalSaleHandler
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
    /// Satış öncesi eşleme tazeleyici (W29). <c>null</c> = kapalı (test/eski davranış): satış
    /// diskteki eşlemeyle yapılır.
    /// </summary>
    private readonly IMappingRefresher? _mappingRefresher;

    public LocalSaleHandler(
        IPaymentDetailClient amounts, AgentOrchestrator orch, CommandStore store,
        ILineDepartmentResolver departments, IPaymentMethodResolver paymentMethods,
        IResultNotifier notifier, Outbox outbox, ITerminalTransport transport,
        Func<DateTimeOffset>? now = null, Action<string, object?>? log = null,
        IMappingRefresher? mappingRefresher = null)
    {
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

        var pending = _store.Pending(unknownEsigi: esik);
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
                outcome = await _orch.HandleAsync(probe, k.ExpiresAt, ct);
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
            await NotifyAsync(k.PaymentId, body, ct);
            _log("[yerel] yarım komut çözüldü", new { k.CommandId, decision = outcome.Decision.ToString() });
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
        _outbox.Enqueue(eid, req.PaymentId ?? "", OutboxKinds.TicketCancel, govde, "");
        try
        {
            var bildirim = await _notifier.NotifyTicketCancelAsync(govde, ct);
            if (bildirim.IsFinal) _outbox.Confirm(eid);
            else _outbox.MarkAttempt(eid);
            if (bildirim.IsProblem)
                _log("[iptal] fiş iptali bildirimi SORUNU (alarm)", new
                {
                    req.PaymentId, outcome = bildirim.Outcome.ToString(),
                    bildirim.StatusCode, bildirim.Message,
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
    public async Task<string> HandleAsync(SaleToPoiRequest req, CancellationToken ct = default)
    {
        // 1. Tutarı çek — GET = ACK. Reddedilirse/ağ hatası → terminale GİTME.
        var fetch = await _amounts.FetchAsync(req.PaymentId, ct);
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
            BankBkmId: req.BankBkmId);

        // Terminal başına TEK işlem (değişmez #4): eşzamanlı iki satış cihaz fişini bozar.
        AgentOutcome outcome;
        await _islemKilidi.WaitAsync(ct);
        try { outcome = await _orch.HandleAsync(sale, d.ExpiresAtMs, ct); }
        finally { _islemKilidi.Release(); }

        // 4. Gövdeyi kur; ÖNCE platforma bildir, SONRA kasaya dön.
        var sonuc = ToTransportResult(outcome);
        var body = SaleToPoiResponseBuilder.BuildResult(req, sonuc, d.Exponent, _now(), taxMismatches);
        await NotifyAsync(req.PaymentId, body, ct);
        _log("[yerel] sonuç", new { req.PaymentId, decision = outcome.Decision.ToString(), state = outcome.State.ToString() });

        // Fiş bu ödemeyle KAPANDIYSA: satırların deftere yazılma anı budur (K-26).
        await FisKapandiBildirAsync(req, sonuc, ct);
        return body;
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
        _outbox.Enqueue(eid, req.PaymentId, OutboxKinds.TicketClosed, govde, "");
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
            var bildirim = await _notifier.NotifyTicketClosedAsync(govde, ct);
            if (bildirim.IsFinal) _outbox.Confirm(eid);
            else _outbox.MarkAttempt(eid);
            if (bildirim.IsProblem)
                _log("[yerel] fiş kapandı bildirimi SORUNU (alarm)", new
                {
                    req.PaymentId, fisId, outcome = bildirim.Outcome.ToString(),
                    bildirim.StatusCode, bildirim.Message,
                });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log("[yerel] fiş kapandı bildirimi gönderilemedi — outbox'ta kaldı (replay)",
                new { req.PaymentId, fisId, error = e.Message });
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
        foreach (var k in _store.PendingClosedTickets())
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
    public async Task DrainOutboxAsync(CancellationToken ct = default)
    {
        // ÖNCE yarım kalmış kapanışlar: gövdesi hiç kurulamamış fişler outbox'ta görünmez.
        KayipKapanislariTopla();

        foreach (var e in _outbox.Pending())
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
                if (res.IsFinal) _outbox.Confirm(e.EventId);
                else _outbox.MarkAttempt(e.EventId);
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
                _outbox.MarkAttempt(e.EventId);   // kalsın, bir sonraki drain'de tekrar
            }
        }
    }

    /// <summary>Sonucu platforma bildir — dayanıklı ÖNCE (outbox), sonra POST. Ağ/429 → outbox'ta kalır, replay.</summary>
    private async Task NotifyAsync(string paymentId, string body, CancellationToken ct)
    {
        var eid = paymentId + ":result";
        _outbox.Enqueue(eid, paymentId, OutboxKinds.Result, body, "");   // dayanıklı yazım ÖNCE (INSERT OR IGNORE)
        try
        {
            var res = await _notifier.NotifyAsync(paymentId, body, ct);
            if (res.IsFinal) _outbox.Confirm(eid);
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
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log("[yerel] bildirim gönderilemedi — outbox'ta kaldı (replay)", new { paymentId, error = e.Message });
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
