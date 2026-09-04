using System.Text.Json;
using System.Text.Json.Nodes;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Protokol döngüsü testleri — <b>sıralama kurallarını</b> çivilerler.
///
/// Bu dosyadaki her test bir sıralama hatasının bedeline karşılık gelir: yazmadan onaylamak komutu
/// kaybeder, satışı okuma döngüsünde beklemek ACK penceresini kaçırır ve komutun yeniden teslim
/// edilmesine yol açar, sonucu yazmadan göndermek tahsilatı deftere hiç düşürmez.
/// </summary>
public class AgentSessionTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"sess_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;
    private readonly Outbox _outbox;
    private readonly ClockOffset _clock = new();

    public AgentSessionTests()
    {
        _store = CommandStore.Open(_db);
        _outbox = Outbox.Open(_db);
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    // ── Sahte tel: gerçek soket olmadan protokolü yürütür ────────────────────
    private sealed class FakeChannel : IAgentChannel
    {
        private readonly Queue<string> _gelen = new();
        private readonly TaskCompletionSource _bitti = new();
        public List<JsonNode> Sent { get; } = new();
        public int? CloseCode { get; set; }
        public Func<JsonNode, string?>? OnSend { get; set; }

        public void Push(object frame) => _gelen.Enqueue(JsonSerializer.Serialize(frame));
        public void Finish() { _bitti.TrySetResult(); }

        public Task ConnectAsync(Uri uri, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(string json, CancellationToken ct = default)
        {
            var n = JsonNode.Parse(json)!;
            Sent.Add(n);
            var cevap = OnSend?.Invoke(n);
            if (cevap is not null) _gelen.Enqueue(cevap);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct = default)
        {
            for (var i = 0; i < 200; i++)
            {
                if (_gelen.Count > 0) return _gelen.Dequeue();
                if (_bitti.Task.IsCompleted) return null;
                await Task.Delay(5, ct);
            }
            return null;
        }

        public Task CloseAsync(int code, string reason, CancellationToken ct = default)
        { CloseCode = code; return Task.CompletedTask; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSessions : ISessionProvider
    {
        public int Calls;
        public Task<SessionToken> AcquireAsync(CancellationToken ct = default)
        { Calls++; return Task.FromResult(new SessionToken("jwt", 300, 1_700_000_000_000)); }
    }

    private static object Command(string commandId, long expiresAt, string terminalId = "t1") => new
    {
        type = "command",
        requestId = "req-" + commandId,
        command = new
        {
            commandId,
            paymentId = "pay-" + commandId,
            expiresAt,
            providerPluginId = "prov",
            payload = new { terminalId, amountMinor = 24000, currency = "TRY", exponent = 2 },
        },
    };

    private (AgentSession, FakeChannel, SimulatorTransport) Kur(
        SimulatorTransport? sim = null, long serverTime = 1_700_000_000_000)
    {
        sim ??= new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        var ch = new FakeChannel();
        var sess = new AgentSession(orch, _store, _outbox, _clock, new FakeSessions(),
            () => ch, new Uri("wss://x/v1/agent"));
        ch.Push(new { type = "hello.ok", gatewayId = "gw1", serverTime });
        return (sess, ch, sim);
    }

    /// <summary>Oturumu bir tur çalıştırır (kanal kapanınca döner).</summary>
    private static async Task Calistir(AgentSession sess, FakeChannel ch, int ms = 400)
    {
        using var cts = new CancellationTokenSource();
        var t = sess.RunAsync(cts.Token);
        await Task.Delay(ms);
        ch.Finish();
        cts.Cancel();
        try { await t; } catch (OperationCanceledException) { }
        await sess.DisposeAsync();
    }

    private static JsonNode? Ilk(FakeChannel ch, string tip) =>
        ch.Sent.FirstOrDefault(n => n?["type"]?.GetValue<string>() == tip);

    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ElSikisma_HelloGonderilir_SaatSunucudanKurulur()
    {
        var (sess, ch, _) = Kur(serverTime: 1_777_000_000_000);
        await Calistir(sess, ch);

        Assert.NotNull(Ilk(ch, "hello"));
        // §5.3 — cihaz saatine güvenilmez; offset sunucudan gelir.
        Assert.True(_clock.IsSynced);
    }

    [Fact]
    public async Task Komut_ONCE_yazilir_SONRA_onaylanir()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(
            TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN1"));
        var (sess, ch, _) = Kur(sim);
        ch.Push(Command("c1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000));
        await Calistir(sess, ch);

        var ack = Ilk(ch, "command.ack");
        Assert.NotNull(ack);
        Assert.True(ack!["accepted"]!.GetValue<bool>());
        // ← ÇİVİ: onay gönderildiyse komut dayanıklı olarak YAZILMIŞ olmalı (§12.2/1).
        Assert.NotNull(_store.Read("c1"));
    }

    [Fact]
    public async Task ACK_satisi_BEKLEMEZ()
    {
        // Kart işlemi sahada 20–32 sn sürüyor; ACK penceresi 3 sn. Beklenirse dispatcher kabul
        // görmez ve AYNI ÖDEME yeniden teslim edilir.
        var sim = new SimulatorTransport { Delay = TimeSpan.FromMilliseconds(250) };
        sim.Expect(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 24000));
        var (sess, ch, _) = Kur(sim);

        var ackAnda = new TaskCompletionSource<TimeSpan>();
        var basla = DateTime.UtcNow;
        ch.OnSend = n =>
        {
            if (n["type"]?.GetValue<string>() == "command.ack") ackAnda.TrySetResult(DateTime.UtcNow - basla);
            return null;
        };
        ch.Push(Command("c2", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000));
        await Calistir(sess, ch);

        Assert.True(ackAnda.Task.IsCompleted, "ACK hiç gönderilmedi");
        Assert.True(ackAnda.Task.Result < TimeSpan.FromMilliseconds(200),
            $"ACK satışı bekledi: {ackAnda.Task.Result.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Sonuc_ONCE_outboxa_SONRA_tele()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(
            TransportOutcome.Approved, ApprovedAmountMinor: 24000, Rrn: "RRN9"));
        var (sess, ch, _) = Kur(sim);
        ch.Push(Command("c3", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000));
        await Calistir(sess, ch);

        var st = Ilk(ch, "status");
        Assert.NotNull(st);
        Assert.Equal("approved", st!["status"]!.GetValue<string>());
        // ← ÇİVİ: gateway onaylamadı, bu yüzden outbox'ta DURUYOR (§12.2/7).
        Assert.Equal(1, _outbox.Depth());
    }

    [Fact]
    public async Task Outbox_YALNIZ_status_ack_ile_temizlenir()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(
            TransportOutcome.Approved, ApprovedAmountMinor: 24000));
        var (sess, ch, _) = Kur(sim);
        ch.OnSend = n => n["type"]?.GetValue<string>() == "status"
            ? JsonSerializer.Serialize(new
            { type = "status.ack", eventId = n["eventId"]!.GetValue<string>(), accepted = true })
            : null;
        ch.Push(Command("c4", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000));
        await Calistir(sess, ch);

        Assert.Equal(0, _outbox.Depth());
    }

    [Fact]
    public async Task Gateway_REDDEDERSE_sonuc_ELDE_KALIR()
    {
        var sim = new SimulatorTransport().Expect(new TransportResult(
            TransportOutcome.Approved, ApprovedAmountMinor: 24000));
        var (sess, ch, _) = Kur(sim);
        ch.OnSend = n => n["type"]?.GetValue<string>() == "status"
            ? JsonSerializer.Serialize(new
            { type = "status.ack", eventId = n["eventId"]!.GetValue<string>(), accepted = false })
            : null;
        ch.Push(Command("c5", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000));
        await Calistir(sess, ch);

        // ← ÇİVİ: "gönderdim" yeterli değil. Kabul edilmeyen sonuç kaybolamaz.
        Assert.Equal(1, _outbox.Depth());
        Assert.Equal(1, _outbox.Pending()[0].Attempts);
    }

    [Fact]
    public async Task Bozuk_komut_KABUL_EDILMEZ()
    {
        var (sess, ch, sim) = Kur();
        // `exponent` yok → -1. Tahmin etmek (2 varsaymak) JPY'de tutarı 100 katına çıkarırdı.
        ch.Push(new
        {
            type = "command",
            requestId = "r9",
            command = new
            {
                commandId = "c6", paymentId = "p6",
                expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000,
                payload = new { terminalId = "t1", amountMinor = 24000, currency = "TRY" },
            },
        });
        await Calistir(sess, ch);

        var ack = Ilk(ch, "command.ack");
        Assert.NotNull(ack);
        Assert.False(ack!["accepted"]!.GetValue<bool>());
        Assert.Empty(sim.SaleCalls);
    }

    [Fact]
    public async Task AyniTerminale_iki_satis_SERILESTIRILIR()
    {
        // Değişmez #4. Cihazın `StartTicket`'i açık fiş bulursa onu SESSİZCE iptal eder —
        // yani eşzamanlılık burada nezaket değil, VERİ KAYBI meselesi.
        var sim = new SimulatorTransport { Delay = TimeSpan.FromMilliseconds(120) };
        sim.Expect(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 1))
           .Expect(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 2));
        var (sess, ch, _) = Kur(sim);
        var exp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000;
        ch.Push(Command("c7", exp));
        ch.Push(Command("c8", exp));
        await Calistir(sess, ch, 700);

        Assert.Equal(2, sim.SaleCalls.Count);
        Assert.Equal(1, sim.MaxConcurrentSales);   // ← kilit yoksa 2 olur
    }

    [Fact]
    public async Task FARKLI_terminaller_PARALEL_calisir()
    {
        // Bir önceki testin karşı kontrolü. O test tek başına boşuna yeşil yanabilirdi: ölçüm
        // bozuk olsa ya da iki satış hiç çakışmasa da `MaxConcurrentSales` 1 çıkardı. Burada
        // çakışmanın GERÇEKTEN ölçülebildiği gösteriliyor — kilit terminal başına, global değil.
        var sim = new SimulatorTransport { Delay = TimeSpan.FromMilliseconds(120) };
        sim.Expect(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 1))
           .Expect(new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 2));
        var (sess, ch, _) = Kur(sim);
        var exp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600_000;
        ch.Push(Command("d1", exp, terminalId: "t1"));
        ch.Push(Command("d2", exp, terminalId: "t2"));
        await Calistir(sess, ch, 700);

        Assert.Equal(2, sim.SaleCalls.Count);
        Assert.Equal(2, sim.MaxConcurrentSales);   // iki farklı terminal birbirini beklemez
    }

    [Fact]
    public void KapanmaKodlari_dogru_yorumlanir()
    {
        // 4403 = cihaz devre dışı bırakıldı. Yeniden denemek, kapatılmış bir cihazın sonsuza
        // kadar kapı çalması olurdu.
        Assert.False(CloseCodes.ShouldReconnect(CloseCodes.SessionRevoked));
        Assert.True(CloseCodes.ShouldReconnect(CloseCodes.Unauthorized));
        Assert.True(CloseCodes.ShouldReconnect(null));
        // Sağlıklı devir teslimi cezalandırılmaz.
        Assert.True(CloseCodes.IsBenign(CloseCodes.Draining));
        Assert.True(CloseCodes.IsBenign(CloseCodes.Replaced));
        Assert.False(CloseCodes.IsBenign(CloseCodes.Unauthorized));
    }
}
