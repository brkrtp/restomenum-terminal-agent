using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host;

/// <summary>
/// Cihaz eşlemesini eklentiden periyodik çeken arka plan servisi (üretim config kanalı, §20-I). Açılışta
/// bir kez + her <b>~30 dk ± %20 jitter</b> (aynı işletmede çok cihaz / servis restart aynı saniyede
/// vurmasın diye sapma ŞART). <b>Satış anında ÇEKME YOK</b> (K-21) — bu servis satış yolundan tamamen ayrı;
/// eşlenmemiş ürün/yöntem satışta fail-closed reddedilir, operatör düzeltir, SONRAKİ yoklama alır.
///
/// <para>Sonuç ele alışı: <c>Updated</c> → depoyu güncelle (+diske yaz); <c>NotModified</c> (304) → dokunma
/// (bedava); <c>AuthFailed</c> → GÖRÜNÜR ALARM (sır yenilenmiş olabilir) ama eski eşlemeyle DEVAM (satış
/// bloke olmaz); <c>Failed</c> → uyarı, yine eski eşlemeyle devam. Sessiz bayat-eşleme tam da kaçındığımız
/// şey; kesinti görünür olur ama satışı durdurmaz.</para>
/// </summary>
public sealed class DeviceConfigPoller : BackgroundService
{
    private readonly DeviceConfigClient _client;
    private readonly IDeviceMappingStore _store;
    private readonly ILogger<DeviceConfigPoller> _log;
    private readonly TimeSpan _base;
    private readonly double _jitter;
    private readonly Random _rng = new();

    public DeviceConfigPoller(DeviceConfigClient client, IDeviceMappingStore store,
        ILogger<DeviceConfigPoller> log, TimeSpan? interval = null, double jitter = 0.20)
    {
        _client = client;
        _store = store;
        _log = log;
        _base = interval ?? TimeSpan.FromMinutes(30);
        _jitter = jitter;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("cihaz eşleme yoklayıcısı başladı (aralık ~{Dk} dk ± %{Jit}); mevcut sürüm {Ver}",
            _base.TotalMinutes, (int)(_jitter * 100), _store.CurrentVersion?.ToString() ?? "(yok)");

        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct);
            try { await Task.Delay(NextDelay(), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var result = await _client.FetchMappingAsync(_store.CurrentVersion, ct);
            switch (result)
            {
                case MappingFetchResult.Updated up:
                    _store.Update(up.Mapping, up.RawJson);
                    _log.LogInformation("eşleme güncellendi: sürüm {Ver}", up.Mapping.Version);
                    break;
                case MappingFetchResult.NotModified:
                    _log.LogDebug("eşleme değişmedi (304)");
                    break;
                case MappingFetchResult.AuthFailed af:
                    // Görünür alarm — sır yenilenmiş olabilir. Eski eşlemeyle devam.
                    _log.LogError("cihaz config KİMLİK HATASI: {Detay} — yapılandırma bağlantısı koptu, " +
                        "eski eşlemeyle devam. Kurulum sırrını yenileyin.", af.Detail);
                    break;
                case MappingFetchResult.Failed f:
                    _log.LogWarning("eşleme çekilemedi: {Detay} — eski eşlemeyle devam.", f.Detail);
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "eşleme yoklaması hata verdi — eski eşlemeyle devam.");
        }
    }

    private TimeSpan NextDelay()
    {
        // base * (1 ± jitter): ör. 30dk, %20 → 24–36 dk arası.
        var factor = 1.0 + (_rng.NextDouble() * 2 - 1) * _jitter;
        return TimeSpan.FromMilliseconds(_base.TotalMilliseconds * factor);
    }
}
