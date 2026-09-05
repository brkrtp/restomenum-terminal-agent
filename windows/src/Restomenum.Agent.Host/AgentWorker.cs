using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host;

/// <summary>
/// Agent'ın yaşam döngüsü: bağlan, komutları işle, kapanırken <b>uçuştaki işi bitir</b>.
///
/// <para><b>Kapanış en riskli an.</b> Kartlı ödeme sahada 20–32 saniye, belirsizlik kurtarması ise
/// 100 saniyeye kadar sürüyor. Varsayılan 5 saniyelik host kapanış süresiyle çalışırsa Windows
/// servisi tam kart çekilirken süreci keser: para terminalde hareket eder, sonuç outbox'a hiç
/// yazılamaz ve tahsilat deftere düşmez. Bu yüzden süre <see cref="Program"/>'da açıkça uzatılır.</para>
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly AgentOptions _opt;
    private readonly ILogger<AgentWorker> _log;
    private readonly IDeviceKey _key;
    private readonly ITerminalTransport _transport;
    private readonly IHostApplicationLifetime _lifetime;

    private readonly CommandStore _store;
    private readonly Outbox _outbox;

    public AgentWorker(
        IOptions<AgentOptions> opt, ILogger<AgentWorker> log, IDeviceKey key,
        ITerminalTransport transport, IHostApplicationLifetime lifetime,
        CommandStore store, Outbox outbox)
    {
        _opt = opt.Value;
        _log = log;
        _key = key;
        _transport = transport;
        _lifetime = lifetime;
        // DI'dan geliyorlar ve BURADA AÇILMIYOR: taşıma katmanı da aynı komut deposunu
        // `ITicketSnapshotStore` olarak görüyor. Worker kendi kopyasını açsaydı iki ayrı örnek
        // olur ve ödeme öncesi fiş görüntüsü taşımanın yazdığı yerde kalmazdı.
        _store = store;
        _outbox = outbox;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dbPath = _opt.ResolveStorePath();
        _log.LogInformation("agent başlıyor: connector={ConnectorId} store={Store}",
            _opt.ConnectorId, dbPath);

        var store = _store;
        var outbox = _outbox;

        // Kapanmamış işler açılışta görünür olmalı: outbox'ta bekleyen bir sonuç, defterine
        // yazılmamış bir tahsilattır. Sessizce devam etmek onu gözden kaçırmak olurdu.
        var bekleyen = outbox.Depth();
        if (bekleyen > 0)
            _log.LogWarning("outbox'ta {Adet} bildirilmemiş sonuç var — bağlanınca gönderilecek", bekleyen);

        // Kesin sonuca ulaşmamış komutlar: bunlar için para hareket etmiş OLABİLİR ve gateway
        // komutu yeniden teslim etmez (ACK verilmişti). Görünür olmaları şart.
        var yarim = store.Pending().Count;
        if (yarim > 0)
            _log.LogWarning("{Adet} komut yarım kalmış — açılışta terminale sorulacak", yarim);

        var clock = new ClockOffset();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var sessions = new HttpSessionProvider(http, _key, _opt.ServerId, new Uri(_opt.SessionUrl), clock);
        var orch = new AgentOrchestrator(store, _transport, clock);

        await using var session = new AgentSession(
            orch, store, outbox, clock, sessions,
            () => new WebSocketChannel(), new Uri(_opt.GatewayUrl),
            log: (m, d) => _log.LogInformation("{Mesaj} {Detay}", m, d))
        {
            AgentVersion = _opt.Version,
        };

        try
        {
            // BAĞLANMADAN ÖNCE: yeniden başlatmada yarım kalmış komutlar çözülür. Sonra bağlanınca
            // outbox zaten boşaltılır. Sırayı ters çevirmek, çözülmüş sonuçların ilk `hello.ok`
            // boşaltmasını kaçırıp bir sonraki satışa kadar beklemesi demek olurdu.
            await session.KurtarAsync(stoppingToken);
            await session.RunAsync(stoppingToken);
            // `RunAsync` yalnız iptalde ya da oturum KALICI olarak reddedildiğinde döner (4403).
            if (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError("oturum kalıcı olarak reddedildi — cihaz devre dışı bırakılmış olabilir");
                // Sonsuz döngüde dönmek yerine servis durur: yeniden denemek, kapatılmış bir
                // cihazın sürekli kapı çalması olurdu ve gerçek sorunu gizlerdi.
                _lifetime.StopApplication();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _log.LogInformation("agent kapanıyor");
        }
        finally
        {
            var kalan = outbox.Depth();
            if (kalan > 0)
                _log.LogWarning("KAPANIŞTA {Adet} sonuç hâlâ gönderilmemiş — kayıp DEĞİL, açılışta tekrar denenecek", kalan);
        }
    }
}
