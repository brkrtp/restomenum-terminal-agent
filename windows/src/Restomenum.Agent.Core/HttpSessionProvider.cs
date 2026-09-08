using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Restomenum.Agent.Core;

/// <summary>
/// <c>POST /v1/connectors/session</c> ile kısa ömürlü JWT alır (§4.2).
///
/// <para><b>Kanonik dize uzunluk öneklidir</b> (<see cref="SessionSigning"/>): düz birleştirme
/// belirsizdir ve tek imzanın iki farklı isteği doğrulamasına yol açar.</para>
///
/// <para><b>`staleRequest` bir kez düzeltilir:</b> sunucu ±60 sn penceresi dışındaki damgayı
/// reddeder ve kendi zamanını verir. Offset düzeltilip <b>bir kez</b> tekrar denenir — sonsuz döngü
/// yapılmaz, çünkü ikinci ret artık saat sorunundan değildir.</para>
/// </summary>
public sealed class HttpSessionProvider : ISessionProvider
{
    private readonly HttpClient _http;
    private readonly IDeviceKey _key;
    private readonly string _serverId;
    private readonly Uri _endpoint;
    private readonly ClockOffset _clock;

    public HttpSessionProvider(
        HttpClient http, IDeviceKey key, string serverId, Uri endpoint, ClockOffset clock,
        Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _key = key;
        _serverId = serverId;
        _endpoint = endpoint;
        _clock = clock;
        _simdi = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Önbellek geçerliliği için duvar saati (W52) — testte sabitlenir.</summary>
    private readonly Func<DateTimeOffset> _simdi;

    /// <summary>
    /// Bu sağlayıcıdan giden TOPLAM oturum HTTP isteği (W51 — yalnız ölçüm, karar etkilemez).
    /// İçerideki tekrarı dışarıdan görünür kılar: bir <see cref="AcquireAsync"/> çağrısı iki
    /// istek üretmişse saat penceresi kaçmış demektir ve gecikmenin sebebi budur.
    /// </summary>
    public int HttpDenemeSayisi => _httpDeneme;
    private int _httpDeneme;

    /// <summary>
    /// Jetonun ömrünün sonuna bırakılan GÜVENLİK MARJI (W52). Jeton `ExpiresInSec` kadar geçerli
    /// ama son saniyesine kadar kullanmak, tam da uçta işlenirken dolmasına yol açardı — ve o
    /// hata satışın ortasında görünürdü.
    /// </summary>
    public static readonly TimeSpan Marj = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _jetonKilidi = new(1, 1);
    private SessionToken? _jeton;
    private DateTimeOffset _gecerlilikSonu;

    /// <summary>
    /// Elde tutulan jetonu atar (W52) — çağıran <c>401</c> aldığında.
    /// </summary>
    public void Invalidate()
    {
        lock (_atmaKilidi) { _jeton = null; _gecerlilikSonu = default; }
    }

    private readonly object _atmaKilidi = new();

    /// <summary>
    /// Oturum jetonu — <b>geçerliyse yeniden kullanılır</b> (W52).
    ///
    /// <para><b>Neden (ölçüm 2026-09-08 22:45):</b> önbellek yoktu; platforma giden HER çağrı
    /// baştan bir oturum POST'u atıyordu. Tek satışta 3 tur sayıldı (bulut günlüğü: 09.370 /
    /// 27.861 / 29.355). Sıcakken ~140 ms, SOĞUKKEN <b>4,59 sn</b> — o gün 20 saniyelik satışın
    /// yarısı buydu.</para>
    ///
    /// <para><b>Süreç yeniden başlayınca temiz:</b> jeton yalnız bellekte. Diske yazmak, süreç
    /// ölümünde geçersiz bir jetonla uyanma riski getirirdi ve kazancı yok.</para>
    /// </summary>
    public async Task<SessionToken> AcquireAsync(CancellationToken ct = default)
    {
        if (GecerliJeton() is { } hazir) return hazir;

        // Tek uçuş: eşzamanlı iki satış aynı anda jeton almasın (fırtına yok).
        await _jetonKilidi.WaitAsync(ct);
        try
        {
            // Kilidi beklerken başkası almış olabilir.
            if (GecerliJeton() is { } arada) return arada;
            var yeni = await AlAsync(ct);
            lock (_atmaKilidi)
            {
                _jeton = yeni;
                // `ExpiresInSec` 0/negatif gelirse önbellek KURULMAZ (her çağrı yeniden alır) —
                // uydurma bir ömür vermektense önbelleksiz çalışmak doğru.
                _gecerlilikSonu = yeni.ExpiresInSec > Marj.TotalSeconds
                    ? _simdi().AddSeconds(yeni.ExpiresInSec) - Marj
                    : default;
            }
            return yeni;
        }
        finally { _jetonKilidi.Release(); }
    }

    private SessionToken? GecerliJeton()
    {
        lock (_atmaKilidi)
            return _jeton is not null && _gecerlilikSonu > _simdi() ? _jeton : null;
    }

    private async Task<SessionToken> AlAsync(CancellationToken ct)
    {
        var (token, stale) = await DeneAsync(ct);
        if (token is not null) return token;
        if (!stale) throw new InvalidOperationException("session reddedildi");

        // Saat penceresi kaçtı; sunucu zamanıyla offset düzeltildi. TEK bir tekrar.
        var (ikinci, _) = await DeneAsync(ct);
        return ikinci ?? throw new InvalidOperationException("session reddedildi (offset düzeltmesinden sonra)");
    }

    private async Task<(SessionToken?, bool stale)> DeneAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _httpDeneme);   // W51: yalnız sayaç
        // Nonce tek kullanımlık ve tahmin edilemez olmalı — replay koruması buna dayanıyor.
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var ts = (_clock.IsSynced ? _clock.ServerNow() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .ToString();

        var kanonik = SessionSigning.CanonicalString(_key.ConnectorId, nonce, ts);
        var imza = Convert.ToBase64String(_key.Sign(Encoding.UTF8.GetBytes(kanonik)));

        var govde = new
        {
            serverId = _serverId,
            data = new
            {
                connectorId = _key.ConnectorId,
                nonce,
                timestamp = long.Parse(ts),
                fingerprint = _key.Fingerprint,
                signature = imza,
            },
        };

        using var yanit = await _http.PostAsJsonAsync(_endpoint, govde, ct);
        var metin = await yanit.Content.ReadAsStringAsync(ct);
        var j = JsonNode.Parse(metin);

        var sunucuZamani = j?["serverTime"]?.GetValue<long>() ?? j?["data"]?["serverTime"]?.GetValue<long>();
        if (sunucuZamani.HasValue) _clock.Sync(sunucuZamani.Value);

        var basarili = j?["success"]?.GetValue<bool>() ?? false;
        if (!basarili)
        {
            var mesaj = j?["message"]?.GetValue<string>() ?? "";
            // Ret gerekçesi bilinçli olarak tek tiptir (`plugin.connector.unauthorized`); yalnız
            // `staleRequest` ayrıdır çünkü DÜZELTİLEBİLİR bir durumdur.
            return (null, mesaj.Contains("stale", StringComparison.OrdinalIgnoreCase));
        }

        var d = j?["data"] ?? j;
        var jwt = d?["token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(jwt)) return (null, false);

        return (new SessionToken(
            jwt,
            d?["expiresInSec"]?.GetValue<int>() ?? 300,
            d?["serverTime"]?.GetValue<long>() ?? sunucuZamani ?? 0), false);
    }
}
