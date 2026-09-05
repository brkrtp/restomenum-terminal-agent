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
    private readonly IResultNotifier _notifier;
    private readonly Outbox _outbox;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string, object?> _log;

    /// <summary>
    /// Terminal başına TEK işlem (değişmez #4) — silinen <c>AgentSession</c>'dan taşındı. İki satış
    /// aynı cihaz oturumunda eşzamanlı sürülemez: ikinci <c>StartTicket</c> birincinin fişini sessizce
    /// iptal eder ve üzerindeki para kaybolur. Yerel dinleyici istekleri eşzamanlı gelebildiği için şart.
    /// </summary>
    private readonly SemaphoreSlim _islemKilidi = new(1, 1);

    public LocalSaleHandler(
        IPaymentDetailClient amounts, AgentOrchestrator orch, CommandStore store,
        ILineDepartmentResolver departments, IResultNotifier notifier, Outbox outbox,
        Func<DateTimeOffset>? now = null, Action<string, object?>? log = null)
    {
        _amounts = amounts;
        _orch = orch;
        _store = store;
        _departments = departments;
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
        var pending = _store.Pending();
        if (pending.Count == 0) return;
        _log("[yerel] açılış kurtarması", new { adet = pending.Count });

        foreach (var k in pending)
        {
            if (ct.IsCancellationRequested) return;
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
            // Kasaya DÖNMÜYORUZ (çağrı bitti); yalnız platforma bildir. Exponent 2 (terminal sürüşü TR).
            var geri = new SaleToPoiRequest(k.CommandId, "", k.TerminalId, k.PaymentId, "", _now());
            var body = SaleToPoiResponseBuilder.BuildResult(geri, ToTransportResult(outcome), 2, _now());
            await NotifyAsync(k.PaymentId, body, ct);
            _log("[yerel] yarım komut çözüldü", new { k.CommandId, decision = outcome.Decision.ToString() });
        }
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
            return SaleToPoiResponseBuilder.BuildFailure(req, ECForReject(rej.Reason), $"GET:{rej.Reason}", _now());
        }
        var d = ((PaymentDetailResult.Ok)fetch).Detail;

        // 2. Kalemleri departmana çöz + kuruş dağıtımıyla fiş satırlarına dök.
        var lines = new List<FiscalLine>();
        foreach (var item in d.Items)
        {
            var match = _departments.Resolve(item.ProductCode, item.CategoryId);
            if (match is null)
            {
                // GET başarılıydı (deneme ACCEPTED) → platforma bildir ki takılı kalmasın. Ürünü SÖYLE.
                var reddi = SaleToPoiResponseBuilder.BuildFailure(req, "PaymentRestriction",
                    $"PRODUCT_UNMAPPED:{item.ProductCode}", _now());
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
            if (m.TaxRateBasisPoints is int deptRate
                && int.TryParse(item.TaxCode, out var taxPct)
                && taxPct * 100 != deptRate)
            {
                var reddi = SaleToPoiResponseBuilder.BuildFailure(req, "PaymentRestriction",
                    $"PROVIDER_CONFIG_INCOMPLETE:{item.ProductCode}", _now());
                await NotifyAsync(req.PaymentId, reddi, ct);
                _log("[yerel] departman KDV'si TaxCode ile çelişiyor — terminale gidilmedi (mali sapma önlendi)",
                    new { req.PaymentId, item.ProductCode, item.TaxCode, deptRate, dept = m.Index });
                return reddi;
            }
            lines.AddRange(FiscalLineBuilder.Build(item, m.Index));
        }

        // 3. SaleRequest kur + orkestratör (dedupe/durum-makinesi/UNKNOWN korunur; CommandId = ServiceID).
        var sale = new SaleRequest(
            CommandId: req.ServiceId, PaymentId: req.PaymentId, TerminalId: req.PoiId,
            AmountMinor: d.RequestedAmountMinor, Currency: d.Currency, Exponent: d.Exponent,
            ProviderPluginId: null, FiscalLines: lines);

        // Terminal başına TEK işlem (değişmez #4): eşzamanlı iki satış cihaz fişini bozar.
        AgentOutcome outcome;
        await _islemKilidi.WaitAsync(ct);
        try { outcome = await _orch.HandleAsync(sale, d.ExpiresAtMs, ct); }
        finally { _islemKilidi.Release(); }

        // 4. Gövdeyi kur; ÖNCE platforma bildir, SONRA kasaya dön.
        var body = SaleToPoiResponseBuilder.BuildResult(req, ToTransportResult(outcome), d.Exponent, _now());
        await NotifyAsync(req.PaymentId, body, ct);
        _log("[yerel] sonuç", new { req.PaymentId, decision = outcome.Decision.ToString(), state = outcome.State.ToString() });
        return body;
    }

    /// <summary>
    /// Outbox'ta bekleyen bildirimleri (ağ/429 nedeniyle gönderilememişler) yeniden dener. Worker
    /// açılışta ve periyodik çağırır — WSS'te oturum bağlanınca yapılan drain'in yerini alır.
    /// </summary>
    public async Task DrainOutboxAsync(CancellationToken ct = default)
    {
        foreach (var e in _outbox.Pending())
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var res = await _notifier.NotifyAsync(e.PaymentId, e.PayloadJson, ct);
                if (res.IsFinal) _outbox.Confirm(e.EventId);
                else _outbox.MarkAttempt(e.EventId);
                if (res.IsProblem)
                    _log("[yerel] outbox bildirim SORUNU (alarm)", new { e.PaymentId, outcome = res.Outcome.ToString(), res.StatusCode });
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
        _outbox.Enqueue(eid, paymentId, "result", body, "");   // dayanıklı yazım ÖNCE (INSERT OR IGNORE)
        try
        {
            var res = await _notifier.NotifyAsync(paymentId, body, ct);
            if (res.IsFinal) _outbox.Confirm(eid);
            else _outbox.MarkAttempt(eid);   // NetworkError/RateLimited → kalsın, arka plan replay eder
            if (res.IsProblem)
                _log("[yerel] bildirim SORUNU (alarm)", new { paymentId, outcome = res.Outcome.ToString(), res.StatusCode, res.Message });
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
        _ => new TransportResult(TransportOutcome.Unknown, ProviderResultCode: o.Note ?? o.Decision.ToString()),
    };

    /// <summary>GET reddi → <c>ErrorCondition</c>. Geçersiz/bitmiş ödeme kesin (Aborted); geçici/agent → unknown.</summary>
    private static string ECForReject(PaymentRejectReason r) => r switch
    {
        PaymentRejectReason.NotFound or PaymentRejectReason.Expired
            or PaymentRejectReason.NotActionable or PaymentRejectReason.AmountWindowClosed
            or PaymentRejectReason.SaleItemsUnavailable => "Aborted",
        // Unauthorized / RateLimited / Unknown(ağ) → geçici; unknown'a düşecek bir kod.
        _ => "UnreachableHost",
    };
}
