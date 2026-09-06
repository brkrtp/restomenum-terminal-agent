using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Restomenum.Agent.Core;

/// <summary>Eşleme çekme sonucu. Poller buna göre depoyu günceller / dokunmaz / alarm verir.</summary>
public abstract record MappingFetchResult
{
    /// <summary>200 — yeni eşleme geldi.</summary>
    public sealed record Updated(DeviceMapping Mapping, string RawJson) : MappingFetchResult;
    /// <summary>304 — değişmedi, gövde yok. Mevcut eşleme geçerli.</summary>
    public sealed record NotModified : MappingFetchResult;
    /// <summary>401 + yeniden oturum da başarısız — sır yenilenmiş olabilir. GÖRÜNÜR ALARM; eski eşlemeyle devam.</summary>
    public sealed record AuthFailed(string Detail) : MappingFetchResult;
    /// <summary>Ağ/şekil hatası — eski eşlemeyle devam (satış bloke olmaz), ama alarm.</summary>
    public sealed record Failed(string Detail) : MappingFetchResult;
}

/// <summary>
/// Ingenico eklentisinin cihaz uçlarına HTTP istemcisi (üretim config kanalı, §20-I). Kimlik = kurulum
/// sırrı → kısa ömürlü Bearer jeton (<c>POST /api/device/session</c>). Eşleme <c>GET /api/device/mapping</c>
/// ETag/If-None-Match ile çekilir (304 = değişmedi, bedava). Departman tablosu (yalnız eşliyken okunabilir)
/// <c>POST /api/device/departments</c> ile bildirilir.
///
/// <para><b>Jeton yönetimi:</b> ömrünün %75'inde yenilenir (saat kaymasına dayanıklı); istek sırasında
/// <c>401</c> gelirse BİR KEZ sessizce yeniden oturum açılıp tekrarlanır, yine 401 ise
/// <see cref="MappingFetchResult.AuthFailed"/> (görünür alarm). <b>Satış anında ÇAĞRILMAZ</b> (K-21):
/// bu istemci yalnız açılışta ve periyodik yoklamada çalışır.</para>
/// </summary>
public sealed class DeviceConfigClient
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _enrollmentSecret;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string, object?> _log;

    private string? _token;
    private DateTimeOffset _tokenRenewAt;

    public DeviceConfigClient(HttpClient http, Uri baseUri, string enrollmentSecret,
        Func<DateTimeOffset>? now = null, Action<string, object?>? log = null)
    {
        _http = http;
        _baseUri = baseUri;
        _enrollmentSecret = enrollmentSecret;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? ((_, _) => { });
    }

    /// <summary>Eşlemeyi çeker. <paramref name="currentVersion"/> biliniyorsa If-None-Match gönderir (304 → değişmedi).</summary>
    public async Task<MappingFetchResult> FetchMappingAsync(int? currentVersion, CancellationToken ct = default)
    {
        try
        {
            var token = await EnsureTokenAsync(force: false, ct);
            if (token is null) return new MappingFetchResult.Failed("oturum jetonu alınamadı");

            var (status, body, notModified) = await GetMappingAsync(token, currentVersion, ct);
            if (status == (int)HttpStatusCode.Unauthorized)
            {
                // 401: jeton düşmüş/sır yenilenmiş olabilir — BİR KEZ yeniden oturum + tekrar.
                token = await EnsureTokenAsync(force: true, ct);
                if (token is null) return new MappingFetchResult.AuthFailed("yeniden oturum başarısız (sır yenilenmiş olabilir)");
                (status, body, notModified) = await GetMappingAsync(token, currentVersion, ct);
                if (status == (int)HttpStatusCode.Unauthorized)
                    return new MappingFetchResult.AuthFailed("yeniden oturum sonrası hâlâ 401 (sır geçersiz)");
            }

            if (notModified) return new MappingFetchResult.NotModified();
            if (status != 200) return new MappingFetchResult.Failed($"HTTP {status}: {Kisalt(body)}");

            return DeviceMappingParser.Parse(body) switch
            {
                DeviceMappingParseResult.Ok ok => new MappingFetchResult.Updated(ok.Mapping, body),
                DeviceMappingParseResult.Invalid inv => new MappingFetchResult.Failed($"eşleme ayrıştırılamadı: {inv.Reason}"),
                _ => new MappingFetchResult.Failed("beklenmedik ayrıştırma sonucu"),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new MappingFetchResult.Failed($"ağ/istisna: {e.Message}");
        }
    }

    /// <summary>Cihazın departman tablosunu (yalnız EŞLİYKEN okunur) + kimliğini platforma bildirir.</summary>
    public async Task<bool> ReportDepartmentsAsync(
        IReadOnlyList<DeviceDepartment> departments, GmpDeviceInfo? deviceInfo, CancellationToken ct = default)
    {
        try
        {
            var token = await EnsureTokenAsync(force: false, ct);
            if (token is null) { _log("[config] departman bildirimi: oturum alınamadı", null); return false; }

            var payload = new
            {
                departments = departments.Select(d => new { index = d.Index, name = d.Name, taxRateBasisPoints = d.TaxRateBasisPoints }),
                deviceInfo = deviceInfo is null ? null : new { brand = deviceInfo.Brand, model = deviceInfo.Model, serial = deviceInfo.Serial, version = deviceInfo.Version },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/device/departments"))
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) { _log("[config] departman tablosu bildirildi", new { adet = departments.Count }); return true; }
            _log("[config] departman bildirimi reddedildi", new { status = (int)resp.StatusCode });
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log("[config] departman bildirimi hata", new { error = e.Message });
            return false;
        }
    }

    private async Task<(int status, string body, bool notModified)> GetMappingAsync(string token, int? currentVersion, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "api/device/mapping"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (currentVersion is int v)
            req.Headers.TryAddWithoutValidation("If-None-Match", $"W/\"v{v}\"");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotModified) return (304, "", true);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return ((int)resp.StatusCode, body, false);
    }

    /// <summary>Geçerli jetonu döner; yoksa/yenilenmesi gerekiyorsa (ömrün %75'i) yeniden oturum açar.</summary>
    private async Task<string?> EnsureTokenAsync(bool force, CancellationToken ct)
    {
        if (!force && _token is not null && _now() < _tokenRenewAt) return _token;

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/device/session"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { enrollmentSecret = _enrollmentSecret }), Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log("[config] oturum açılamadı", new { status = (int)resp.StatusCode });
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // Platform zarfı: {success, data:{accessToken,...}} — data varsa oradan oku, yoksa kökten (test/düz).
            var d = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object ? dataEl : root;
            var accessToken = d.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(accessToken)) { _log("[config] oturum yanıtında accessToken yok", null); return null; }
            var expiresIn = d.TryGetProperty("expiresIn", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 900;
            _token = accessToken;
            // Ömrün %75'inde yenile (saat kaymasına dayanıklı).
            _tokenRenewAt = _now().AddSeconds(expiresIn * 0.75);
            if (d.TryGetProperty("connectorId", out var c)) _log("[config] oturum açıldı", new { connectorId = c.GetString() });
            return _token;
        }
        catch (JsonException) { _log("[config] oturum yanıtı ayrıştırılamadı", null); return null; }
    }

    private static string Kisalt(string s) => s.Length <= 160 ? s : s[..160];
}
