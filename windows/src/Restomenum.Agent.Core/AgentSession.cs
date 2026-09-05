using System.Text.Json;
using System.Text.Json.Nodes;

namespace Restomenum.Agent.Core;

/// <summary>
/// Agent'ın gateway ile konuştuğu protokol döngüsü (§3): el sıkışma, komut kabulü, sonuç gönderimi,
/// kimlik tazeleme.
///
/// ## Üç sıralama kuralı — üçü de gerçek bir para hatasına karşılık gelir
///
/// <b>1. `command.ack` yazımdan SONRA (§12.2/1).</b> Önce onaylayıp sonra yazmak, çökme anında
/// komutu hem kaybetmek hem "aldım" demiş olmaktır.
///
/// <b>2. Kabul ile çalıştırma AYRI.</b> Gateway ACK'i 3 saniyede bekler, kartlı ödeme sahada 20–32
/// saniye sürer. Satışı okuma döngüsünde beklemek her komutu zaman aşımına uğratır; dispatcher
/// kabul görmediği için mesajı yeniden teslim eder ve <b>aynı ödeme tekrar tekrar denenir</b>.
///
/// <b>3. Sonuç önce outbox'a, sonra tele (§12.2/7).</b> Gönderip sonra yazmak, ağ koptuğu anda
/// tahsilatı deftere hiç yazmamaktır. Outbox kaydı yalnız <c>status.ack</c> geldikten sonra silinir.
///
/// ## Terminal başına tek işlem (değişmez #4)
///
/// Aynı terminale iki satış aynı anda gönderilemez. Bu <b>nezaket sırası değil, veri kaybı
/// korumasıdır</b>: cihazın <c>StartTicket</c>'i açık bir fiş bulursa onu <b>sessizce iptal eder</b>
/// (`VoidAll`), yani ikinci satış birincinin fişini yok eder. Sahada bu koruma bugün hiçbir yerde
/// yok — tek dayanak istemcinin disiplini.
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly AgentOrchestrator _orch;
    private readonly CommandStore _store;
    private readonly Outbox _outbox;
    private readonly ClockOffset _clock;
    private readonly ISessionProvider _sessions;
    private readonly Func<IAgentChannel> _channelFactory;
    private readonly Backoff _backoff;
    private readonly Uri _gatewayUri;
    private readonly Action<string, object?> _log;

    /// <summary>
    /// Finansal işlem kilidi — değişmez #4.
    ///
    /// <para><b>Kilit terminal KİMLİĞİNE değil, TAŞIMAYA aittir.</b> Bu oturumun tek bir
    /// <see cref="ITerminalTransport"/>'u var ve o da tek bir fiziksel cihaza tek oturumla
    /// bağlanıyor. Kilidi <c>terminalId</c> dizesine göre kursaydık, farklı kimlik taşıyan iki
    /// komut <b>aynı cihaz oturumu üzerinde eşzamanlı</b> çalışırdı; ikinci <c>StartTicket</c>
    /// birincinin fişini sessizce iptal eder ve üzerindeki para kaybolurdu.</para>
    ///
    /// <para>Bir connector ileride birden çok terminal sürerse çözüm kilidi bölmek değil,
    /// <b>terminal başına ayrı taşıma</b> kaydetmektir; kilit o zaman da taşımayla birlikte gelir.</para>
    /// </summary>
    private readonly SemaphoreSlim _islemKilidi = new(1, 1);

    private IAgentChannel? _channel;
    private readonly List<Task> _isler = new();

    public AgentSession(
        AgentOrchestrator orch, CommandStore store, Outbox outbox, ClockOffset clock,
        ISessionProvider sessions, Func<IAgentChannel> channelFactory, Uri gatewayUri,
        Backoff? backoff = null, Action<string, object?>? log = null)
    {
        _orch = orch;
        _store = store;
        _outbox = outbox;
        _clock = clock;
        _sessions = sessions;
        _channelFactory = channelFactory;
        _gatewayUri = gatewayUri;
        _backoff = backoff ?? new Backoff();
        _log = log ?? ((_, _) => { });
    }

    public string AgentVersion { get; init; } = "1.0.3";
    public bool HandshakeCompleted { get; private set; }

    /// <summary>
    /// **Açılış kurtarması** — yeniden başlatmada kesin sonuca ulaşmamış komutları çözer.
    ///
    /// <para><b>Bu olmadan sessiz para kaybı vardır:</b> agent kart penceresinde ölürse komut
    /// <c>SENT_TO_TERMINAL</c>'da kalır. Gateway <c>command.ack</c>'i çoktan almıştır, bu yüzden
    /// komutu <b>yeniden teslim etmez</b>. Kimse sormazsa para hareket etmiş olabilir ve hiçbir
    /// yere yazılmaz — ne agent bilir, ne platform.</para>
    ///
    /// <para>Ödeme öncesi anlık görüntü diskte tutulduğu için (§12.3) fark hâlâ alınabilir; yani
    /// çoğu vaka <b>otomatik</b> çözülür, insana yalnız gerçekten belirsiz olanlar çıkar.</para>
    /// </summary>
    public async Task KurtarAsync(CancellationToken ct = default)
    {
        var yarim = _store.Pending();
        if (yarim.Count == 0) return;
        _log("[agent] açılış kurtarması", new { adet = yarim.Count });

        foreach (var k in yarim)
        {
            if (ct.IsCancellationRequested) return;
            // Terminale gitmemiş komutlar (RECEIVED) için de sormak güvenli: cevabı "işlenmedi"
            // olur ve komut serbest kalır. Tahmin etmekten iyidir.
            var req = new SaleRequest(k.CommandId, k.PaymentId, k.TerminalId, 0, "", 0);
            var kilit = TerminalKilidi(k.TerminalId);
            await kilit.WaitAsync(ct);
            try
            {
                var sonuc = await _orch.HandleAsync(req, k.ExpiresAt, ct);
                var durum = DurumaCevir(sonuc.Decision);
                if (durum is not null)
                {
                    _outbox.Enqueue($"{k.CommandId}:{durum}", k.PaymentId, durum,
                        SonucJson(sonuc), "");
                    _log("[agent] yarım komut çözüldü", new { k.CommandId, durum });
                }
            }
            catch (Exception e)
            {
                // Kurtarılamayan komut store'da KALIR — bir sonraki açılışta yeniden denenir.
                _log("[agent] yarım komut çözülemedi", new { k.CommandId, error = e.Message });
            }
            finally { kilit.Release(); }
        }
    }

    /// <summary>Kopunca jitter'lı geri çekilmeyle yeniden bağlanır; <c>4403</c>'te kalıcı durur.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            int? kapanis = null;
            try
            {
                kapanis = await BirOturumAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Bağlantı hataları normaldir (ağ, gateway yeniden dağıtımı). Yutulmaz, loglanır.
                _log("[agent] oturum hatası", new { error = e.Message });
            }

            if (!CloseCodes.ShouldReconnect(kapanis))
            {
                _log("[agent] oturum iptal edildi — yeniden bağlanılmayacak", new { code = kapanis });
                return;
            }
            // Sağlıklı devir teslimi (drain/replaced) cezalandırmayız: sayaç sıfırlanır.
            if (CloseCodes.IsBenign(kapanis)) _backoff.Reset();

            await Task.Delay(TimeSpan.FromMilliseconds(_backoff.Next()), ct);
        }
    }

    /// <summary>Tek bir bağlantı ömrü. Kapanma kodunu döndürür.</summary>
    private async Task<int?> BirOturumAsync(CancellationToken ct)
    {
        var oturum = await _sessions.AcquireAsync(ct);
        await using var ch = _channelFactory();
        _channel = ch;
        HandshakeCompleted = false;

        await ch.ConnectAsync(_gatewayUri, ct);
        await GonderAsync(ch, new { type = "hello", token = oturum.Token, version = AgentVersion }, ct);

        using var oturumCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reauth = ReauthDonguAsync(ch, oturum, oturumCts.Token);

        try
        {
            while (true)
            {
                var raw = await ch.ReceiveAsync(ct);
                if (raw is null) break;
                try
                {
                    await FrameIsleAsync(ch, raw, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // TEK bir bozuk frame oturumu düşürmemeli. Düşseydi gateway aynı frame'i
                    // yeniden teslim eder, o da yine düşürürdü — terminalin tamamını kilitleyen
                    // bir zehirli-mesaj döngüsü. Frame atlanır, bağlantı ayakta kalır.
                    _log("[agent] frame işlenemedi, atlandı", new { error = e.Message });
                }
            }
        }
        finally
        {
            oturumCts.Cancel();
            // TÜM hatalar yutulur, yalnız iptal değil. Yoksa şu olur: oturum iptal edilince
            // session ucu önce reauth'u reddeder (görev hata verir), sonra gateway 4403 ile
            // kapatır; buradan fırlayan hata `return ch.CloseCode`'a hiç ulaşmaz ve agent
            // "kapanma kodu yok" sanıp SONSUZA KADAR yeniden bağlanır — engellemeye
            // çalıştığımız davranışın ta kendisi.
            try { await reauth; }
            catch (Exception e) { _log("[agent] reauth sonlandı", new { error = e.Message }); }
            _channel = null;
        }

        return ch.CloseCode;
    }

    /// <summary>JWT ömrünün YARISINDA kimlik tazelenir — soket kapatılmaz (§3.3).</summary>
    private async Task ReauthDonguAsync(IAgentChannel ch, SessionToken ilk, CancellationToken ct)
    {
        var ttl = ilk.ExpiresInSec;
        while (!ct.IsCancellationRequested)
        {
            // Yarısı: `exp` anında yenilemek, saat sapması ya da ağ gecikmesinde geç kalır.
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, ttl / 2)), ct);
            var yeni = await _sessions.AcquireAsync(ct);
            ttl = yeni.ExpiresInSec;
            await GonderAsync(ch, new { type = "reauth", token = yeni.Token }, ct);
        }
    }

    private async Task FrameIsleAsync(IAgentChannel ch, string raw, CancellationToken ct)
    {
        JsonNode? f;
        try { f = JsonNode.Parse(raw); }
        catch (JsonException) { _log("[agent] JSON olmayan frame", new { raw }); return; }
        var tip = f?["type"]?.GetValue<string>();

        switch (tip)
        {
            case "hello.ok":
                // §5.3 — cihaz saatine GÜVENİLMEZ. Offset kurulmadan komut çalıştırmak yasak;
                // atlanırsa `expiresAt` kararı cihazın (yanlış olabilen) saatine dayanırdı.
                var st = f?["serverTime"]?.GetValue<long>();
                if (st.HasValue) _clock.Sync(st.Value);
                HandshakeCompleted = true;
                await OutboxBosaltAsync(ch, ct);   // kopukken biriken sonuçlar ÖNCE gider
                break;

            case "command":
                await KomutIsleAsync(ch, f!, ct);
                break;

            case "status.ack":
                // Yalnız gateway "dayanıklı olarak yazdım" dediğinde silinir.
                var eid = f?["eventId"]?.GetValue<string>();
                var kabul = f?["accepted"]?.GetValue<bool>() ?? false;
                if (!string.IsNullOrEmpty(eid))
                {
                    if (kabul) _outbox.Confirm(eid);
                    else _outbox.MarkAttempt(eid);   // elde kalır, tekrar denenir
                }
                break;

            case "reauth.ok":
                break;

            default:
                // İleriye dönük uyumluluk: tanımadığımız frame'i hata sayıp bağlantıyı düşürmeyiz.
                _log("[agent] bilinmeyen frame", new { tip });
                break;
        }
    }

    /// <summary>
    /// Bu agent'ın <b>çalıştırabileceği tek</b> komut tipi. Başka her tip reddedilir.
    ///
    /// <para><b>Neden fail-closed:</b> tip hiç okunmasaydı, platformun belirsizlik çözüm döngüsünün
    /// gönderdiği bir <c>PAYMENT_STATUS_LOOKUP</c> aynı payload'la geldiğinde <b>satış olarak
    /// çalıştırılırdı</b> — yani tam da ilk ödemenin başarılı olduğundan şüphelenilen anda ikinci
    /// kez kart çekilirdi. Tanımadığımız bir tipi çalıştırmaktansa reddetmek her zaman doğrudur.</para>
    /// </summary>
    private const string DESTEKLENEN_TIP = "PAYMENT_SALE";

    private async Task KomutIsleAsync(IAgentChannel ch, JsonNode f, CancellationToken ct)
    {
        var requestId = f["requestId"]?.GetValue<string>() ?? "";
        var c = f["command"];
        var payload = c?["payload"];

        // Tip eksikse ESKİ sürüm sayılır ve satış varsayılır — HAYIR. Eksik tip de reddedilir.
        var tip = c?["type"]?.GetValue<string>() ?? "";
        if (tip != DESTEKLENEN_TIP)
        {
            _log("[agent] desteklenmeyen komut tipi REDDEDİLDİ", new { tip, requestId });
            await GonderAsync(ch, new { type = "command.ack", requestId, accepted = false, reason = "CAPABILITY_NOT_SUPPORTED" }, ct);
            return;
        }

        var req = new SaleRequest(
            CommandId: c?["commandId"]?.GetValue<string>() ?? "",
            PaymentId: c?["paymentId"]?.GetValue<string>() ?? "",
            TerminalId: payload?["terminalId"]?.GetValue<string>() ?? "",
            AmountMinor: payload?["amountMinor"]?.GetValue<long>() ?? 0,
            Currency: payload?["currency"]?.GetValue<string>() ?? "",
            // Eksikse -1: tahmin etmektense komutu reddetmek doğrudur (yanlış basamak = 100× tutar).
            Exponent: payload?["exponent"]?.GetValue<int>() ?? -1,
            ProviderPluginId: c?["providerPluginId"]?.GetValue<string>());
        var expiresAt = c?["expiresAt"]?.GetValue<long>() ?? 0;

        // `amountMinor > 0` ve `currency` de zorunlu: eksik tutar 0'a düşer ve mali cihaza
        // sıfır tutarlı bir ödeme gider.
        var gecerli = req.CommandId.Length > 0 && req.PaymentId.Length > 0
            && req.TerminalId.Length > 0 && req.Exponent >= 0
            && req.AmountMinor > 0 && req.Currency.Length > 0;
        if (!gecerli)
        {
            await GonderAsync(ch, new { type = "command.ack", requestId, accepted = false, reason = "INVALID_COMMAND" }, ct);
            return;
        }

        // TERMİNAL MEŞGULSE KABUL ETME (§8.4: "kuyruğa alma YOK"). Kabul edip sıraya koymak,
        // kasiyere hiçbir geri bildirim vermeden komutu süresi dolana kadar bekletmek olurdu.
        // `accepted:false` ise komut JetStream'de kalır ve daha sonra yeniden teslim edilir.
        var kilit = TerminalKilidi(req.TerminalId);
        if (!await kilit.WaitAsync(0, ct))
        {
            await GonderAsync(ch, new { type = "command.ack", requestId, accepted = false, reason = "TERMINAL_BUSY" }, ct);
            return;
        }

        // KAYIT ÖNCE, onay SONRA (§12.2/1).
        var kabul = _orch.Accept(req, expiresAt);
        await GonderAsync(ch, new { type = "command.ack", requestId, accepted = kabul }, ct);
        if (!kabul) { kilit.Release(); return; }

        // Satış okuma döngüsünü BLOKLAMAZ ve **kapanış jetonuna bağlanmaz**: iptal edilirse
        // belirsizlik çözümü yarıda kesilir ve çözülebilir bir vaka insana çıkar. Sınırı
        // kurtarma bütçesi (~105 sn) ve buradaki sert tavan koyar.
        var is_ = Task.Run(() => CalistirVeBildirAsync(req, expiresAt), CancellationToken.None);
        lock (_isler) { _isler.RemoveAll(t => t.IsCompleted); _isler.Add(is_); }
    }

    private async Task CalistirVeBildirAsync(SaleRequest req, long expiresAt)
    {
        // Sert tavan: hem kurtarma bütçesini (~105 sn) hem kartlı ödemeyi (20–32 sn) rahat kapsar.
        // Sonsuz beklemeyi engeller ama kapanış sinyaliyle KESİLMEZ.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        AgentOutcome sonuc;
        try
        {
            sonuc = await _orch.HandleAsync(req, expiresAt, cts.Token);
        }
        catch (Exception e)
        {
            // Sessizce yutulmaz: sonucu bilmiyorsak bu BELİRSİZLİKTİR, başarısızlık değil.
            _log("[agent] komut çalıştırılamadı", new { req.CommandId, error = e.Message });
            sonuc = new AgentOutcome(AgentDecision.Unresolved, CommandState.UNKNOWN, Note: e.Message);
        }
        finally
        {
            TerminalKilidi(req.TerminalId).Release();
        }

        // `Replayed`: sonuç ZATEN saklı ama outbox'a yazılamamış olabilir (disk dolu, dosya kilidi).
        // O durumda store `COMPLETED` derken outbox boştur ve tahsilat hiçbir zaman bildirilmez.
        // Saklananı yeniden kuyruğa koymak `INSERT OR IGNORE` sayesinde zararsızdır ve bu deliği
        // kendi kendine kapatır.
        if (sonuc.Decision == AgentDecision.Replayed)
        {
            var kayit = _store.Read(req.CommandId);
            if (kayit?.ResultJson is not null)
            {
                try
                {
                    _outbox.Enqueue($"{req.CommandId}:approved", req.PaymentId, "approved",
                        kayit.ResultJson, req.ProviderPluginId ?? "");
                }
                catch (Exception e)
                {
                    _log("[agent] saklanan sonuç yeniden kuyruğa konamadı", new { req.CommandId, error = e.Message });
                }
                var kanal = _channel;
                if (kanal is not null && HandshakeCompleted) await OutboxBosaltAsync(kanal, CancellationToken.None);
            }
            return;
        }

        var durum = DurumaCevir(sonuc.Decision);
        if (durum is null) return;

        // Önce outbox'a — tel koparsa sonuç burada yaşar (§12.2/7).
        //
        // **Yazma başarısızlığı sessiz kalamaz.** Yutulsaydı store'da `COMPLETED` bir sonuç
        // dururken outbox boş kalır ve tahsilat hiçbir zaman bildirilmezdi; kurtarmanın tek yolu
        // bir insanın SQLite dosyasını açması olurdu.
        try
        {
            _outbox.Enqueue(
                eventId: $"{req.CommandId}:{durum}",
                paymentId: req.PaymentId,
                status: durum,
                payloadJson: SonucJson(sonuc),
                providerPluginId: req.ProviderPluginId ?? "");
        }
        catch (Exception e)
        {
            _log("[agent] KRİTİK: sonuç outbox'a yazılamadı", new { req.CommandId, durum, error = e.Message });
            return;
        }

        var ch = _channel;
        if (ch is not null && HandshakeCompleted) await OutboxBosaltAsync(ch, CancellationToken.None);
    }

    /// <summary>Bekleyen sonuçları sırayla gönderir. Silme <c>status.ack</c>'e bağlıdır.</summary>
    private async Task OutboxBosaltAsync(IAgentChannel ch, CancellationToken ct)
    {
        // Boşalana kadar: tek tur 50 satırla sınırlıydı, kesintiden sonra 51. sonuç bir sonraki
        // bağlantıya ya da bir sonraki satışa kadar bildirilmeden beklerdi.
        for (var tur = 0; tur < 40; tur++)
        {
        var bekleyenler = _outbox.Pending();
        if (bekleyenler.Count == 0) return;
        foreach (var e in bekleyenler)
        {
            if (ct.IsCancellationRequested) return;
            await GonderAsync(ch, new
            {
                type = "status",
                eventId = e.EventId,
                paymentId = e.PaymentId,
                status = e.Status,
                providerPluginId = e.ProviderPluginId,
                payload = JsonNode.Parse(e.PayloadJson),
            }, ct);
        }
        // `status.ack` asenkron gelir; onaylananlar silinene kadar bekle, yoksa aynı satırları
        // durmadan yeniden göndeririz.
        await Task.Delay(250, ct);
        if (_outbox.Pending().Count == bekleyenler.Count) return;   // ilerleme yok, bırak
        }
    }

    /// <summary>
    /// Agent kararını sağlayıcı sözlüğüne çevirir. <c>RetryLater</c> ve <c>Replayed</c> <b>bilinçli
    /// olarak null</b>: ilkinde henüz bir sonuç yoktur, ikincisinde sonuç zaten bildirilmiştir.
    /// </summary>
    private static string? DurumaCevir(AgentDecision d) => d switch
    {
        AgentDecision.Approved => "approved",
        AgentDecision.Declined => "declined",
        AgentDecision.Expired => "cancelled",
        AgentDecision.ClockUnsynced => "cancelled",
        AgentDecision.Unresolved => "unknown",
        // Terminale SORULDU ve ödeme işlenmediği KANITLANDI. Bildirmemek, kasiyeri komutun
        // süresi dolana kadar ekran başında bekletmek olurdu; para hareket etmediği için
        // "cancelled" doğru ve kesin cevaptır.
        AgentDecision.RetryLater => "cancelled",
        // `Replayed` burada null KALIR — sonucu tekrar üretmeyiz, saklananı yeniden kuyruğa
        // koyarız (bkz. çağıran).
        _ => null,
    };

    /// <summary>Sonucun taşınabilir hâli. <b>Kart verisi TAŞIMAZ</b> (§12.3).</summary>
    private static string SonucJson(AgentOutcome o) => JsonSerializer.Serialize(new
    {
        approvedAmountMinor = o.Result?.ApprovedAmountMinor,
        rrn = o.Result?.Rrn,
        approvalCode = o.Result?.ApprovalCode,
        cardLast4 = o.Result?.CardLast4,
        scheme = o.Result?.Scheme,
        providerResultCode = o.Result?.ProviderResultCode,
        note = o.Note,
    });

    private SemaphoreSlim TerminalKilidi(string terminalId) => _islemKilidi;

    private static Task GonderAsync(IAgentChannel ch, object frame, CancellationToken ct) =>
        ch.SendAsync(JsonSerializer.Serialize(frame), ct);

    public async ValueTask DisposeAsync()
    {
        Task[] bekleyen;
        lock (_isler) { bekleyen = _isler.ToArray(); }
        // Uçuştaki satışlar bırakılmaz: yarıda kesmek, sonucu outbox'a hiç yazmamak olurdu.
        try { await Task.WhenAll(bekleyen); } catch { /* tek tek zaten loglandı */ }
        if (_channel is not null) await _channel.DisposeAsync();
    }
}
