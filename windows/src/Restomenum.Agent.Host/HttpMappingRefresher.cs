using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host;

/// <summary>
/// <see cref="IMappingRefresher"/>'ın gerçek uygulaması — koşullu GET (<c>If-None-Match</c>/304),
/// <b>sert zaman aşımıyla</b>.
///
/// <para><b>Zaman aşımı sözleşmenin kendisidir, ayar detayı değil.</b> Onsuz, yanıt vermeyen bir
/// yapılandırma ucu her satışı kendi zaman aşımına kadar bekletir; yani kasa, hiçbir ödemeyle
/// ilgisi olmayan bir servis yüzünden durur. Süre dolduğunda diskteki eşlemeyle DEVAM edilir —
/// "taze veriyi alamadım" satışı reddetmek için bir sebep DEĞİL.</para>
///
/// <para>304 (değişmemiş) beklenen ve ucuz hâldir: tek gidiş-dönüş, gövde yok. Değişiklik olduğu
/// anda ise satış YENİ eşlemeyle yapılır — W29'un bütün amacı bu tek satır.</para>
/// </summary>
public sealed class HttpMappingRefresher : IMappingRefresher
{
    private readonly DeviceConfigClient _client;
    private readonly IDeviceMappingStore _store;
    private readonly ILogger _log;
    private readonly TimeSpan _timeout;

    public HttpMappingRefresher(DeviceConfigClient client, IDeviceMappingStore store,
        ILogger log, TimeSpan? timeout = null)
    {
        _client = client;
        _store = store;
        _log = log;
        // 2 sn: 304'lük bir gidiş-dönüş için fazlasıyla yeterli, kart penceresinin (20–32 sn)
        // yanında görünmez. Uzatmak satışı yapılandırma kanalına bağımlı kılmaya başlar.
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    public async Task<int?> EnsureFreshAsync(CancellationToken ct = default)
    {
        var oncekiSurum = _store.CurrentVersion;
        try
        {
            using var sure = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sure.CancelAfter(_timeout);

            var sonuc = await _client.FetchMappingAsync(oncekiSurum, sure.Token);
            switch (sonuc)
            {
                case MappingFetchResult.Updated up:
                    _store.Update(up.Mapping, up.RawJson);
                    _log.LogInformation("[config] satış öncesi eşleme TAZELENDİ: {Eski} → {Yeni}",
                        oncekiSurum?.ToString() ?? "(yok)", up.Mapping.Version);
                    return up.Mapping.Version;

                case MappingFetchResult.NotModified:
                    return null;   // beklenen hâl; gürültü yapma

                default:
                    // Kimlik/başka hata: GÖRÜNÜR ama engelleyici değil.
                    _log.LogWarning("[config] satış öncesi tazeleme başarısız ({Sonuc}) — " +
                        "diskteki sürüm {Ver} ile devam", sonuc.GetType().Name,
                        oncekiSurum?.ToString() ?? "(yok)");
                    return null;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Zaman aşımı. Satış BEKLEMEZ — bu tam olarak korumak istediğimiz şey.
            _log.LogWarning("[config] satış öncesi tazeleme {Sn} sn'de yanıt vermedi — " +
                "diskteki sürüm {Ver} ile devam", _timeout.TotalSeconds,
                oncekiSurum?.ToString() ?? "(yok)");
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning("[config] satış öncesi tazeleme hatası: {Hata} — diskteki sürüm {Ver} ile devam",
                e.Message, oncekiSurum?.ToString() ?? "(yok)");
            return null;
        }
    }
}
