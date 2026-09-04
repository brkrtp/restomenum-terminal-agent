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

    /// <summary>Terminal başına seri hâle getirme — değişmez #4.</summary>
    private readonly Dictionary<string, SemaphoreSlim> _terminalKilitleri = new();
    private readonly object _kilitGate = new();

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
                await FrameIsleAsync(ch, raw, ct);
            }
        }
        finally
        {
            oturumCts.Cancel();
            try { await reauth; } catch (OperationCanceledException) { /* beklenen */ }
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

    private async Task KomutIsleAsync(IAgentChannel ch, JsonNode f, CancellationToken ct)
    {
        var requestId = f["requestId"]?.GetValue<string>() ?? "";
        var c = f["command"];
        var payload = c?["payload"];

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

        var gecerli = req.CommandId.Length > 0 && req.PaymentId.Length > 0
            && req.TerminalId.Length > 0 && req.Exponent >= 0;
        // KAYIT ÖNCE, onay SONRA (§12.2/1).
        var kabul = gecerli && _orch.Accept(req, expiresAt);
        await GonderAsync(ch, new { type = "command.ack", requestId, accepted = kabul }, ct);
        if (!kabul) return;

        // Satış okuma döngüsünü BLOKLAMAZ: 30 saniye burada beklemek reauth'u ve diğer frame'leri
        // durdurur, ACK penceresini kaçırır.
        var is_ = Task.Run(() => CalistirVeBildirAsync(req, expiresAt, ct), ct);
        lock (_isler) { _isler.RemoveAll(t => t.IsCompleted); _isler.Add(is_); }
    }

    private async Task CalistirVeBildirAsync(SaleRequest req, long expiresAt, CancellationToken ct)
    {
        var kilit = TerminalKilidi(req.TerminalId);
        await kilit.WaitAsync(ct);
        AgentOutcome sonuc;
        try
        {
            sonuc = await _orch.HandleAsync(req, expiresAt, ct);
        }
        catch (Exception e)
        {
            // Sessizce yutulmaz: sonucu bilmiyorsak bu BELİRSİZLİKTİR, başarısızlık değil.
            _log("[agent] komut çalıştırılamadı", new { req.CommandId, error = e.Message });
            sonuc = new AgentOutcome(AgentDecision.Unresolved, CommandState.UNKNOWN, Note: e.Message);
        }
        finally
        {
            kilit.Release();
        }

        var durum = DurumaCevir(sonuc.Decision);
        if (durum is null) return;   // RetryLater/Replayed: bildirilecek YENİ bir sonuç yok

        // Önce outbox'a — tel koparsa sonuç burada yaşar (§12.2/7).
        _outbox.Enqueue(
            eventId: $"{req.CommandId}:{durum}",
            paymentId: req.PaymentId,
            status: durum,
            payloadJson: SonucJson(sonuc),
            providerPluginId: req.ProviderPluginId ?? "");

        var ch = _channel;
        if (ch is not null && HandshakeCompleted) await OutboxBosaltAsync(ch, ct);
    }

    /// <summary>Bekleyen sonuçları sırayla gönderir. Silme <c>status.ack</c>'e bağlıdır.</summary>
    private async Task OutboxBosaltAsync(IAgentChannel ch, CancellationToken ct)
    {
        foreach (var e in _outbox.Pending())
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

    private SemaphoreSlim TerminalKilidi(string terminalId)
    {
        lock (_kilitGate)
        {
            if (!_terminalKilitleri.TryGetValue(terminalId, out var s))
            {
                s = new SemaphoreSlim(1, 1);
                _terminalKilitleri[terminalId] = s;
            }
            return s;
        }
    }

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
