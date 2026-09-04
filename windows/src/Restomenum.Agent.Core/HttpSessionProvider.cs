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
        HttpClient http, IDeviceKey key, string serverId, Uri endpoint, ClockOffset clock)
    {
        _http = http;
        _key = key;
        _serverId = serverId;
        _endpoint = endpoint;
        _clock = clock;
    }

    public async Task<SessionToken> AcquireAsync(CancellationToken ct = default)
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
