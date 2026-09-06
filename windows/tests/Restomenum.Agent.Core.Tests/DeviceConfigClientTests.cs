using System.Net;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

public class DeviceConfigClientTests
{
    private const string Mapping = """{ "version": 42, "paymentMethods": { "11-cash": 1 } }""";

    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Queue<(int status, string body)>> Responses = new();
        public readonly List<(string Method, string Path, string? Auth, string? IfNoneMatch)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Requests.Add((req.Method.Method, path, req.Headers.Authorization?.Parameter,
                req.Headers.TryGetValues("If-None-Match", out var inm) ? string.Join(",", inm) : null));
            _ = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            var (status, body) = Responses[path].Dequeue();
            var msg = new HttpResponseMessage((HttpStatusCode)status);
            if (status != 304) msg.Content = new StringContent(body);
            return msg;
        }
    }

    private static (DeviceConfigClient, StubHandler) Kur(DateTimeOffset? now = null)
    {
        var h = new StubHandler();
        var client = new DeviceConfigClient(new HttpClient(h), new Uri("https://plugin.test/"), "SECRET",
            now: now is null ? null : () => now.Value);
        return (client, h);
    }

    private static void Sess(StubHandler h, string token, int expiresIn = 900) =>
        Enq(h, "/api/device/session", 200, $$"""{ "accessToken": "{{token}}", "expiresIn": {{expiresIn}}, "connectorId": "conn_x" }""");

    private static void Enq(StubHandler h, string path, int status, string body)
    {
        if (!h.Responses.TryGetValue(path, out var q)) h.Responses[path] = q = new();
        q.Enqueue((status, body));
    }

    [Fact]
    public async Task Fetch_200_Updated_ve_Bearer_gonderir()
    {
        var (c, h) = Kur();
        Sess(h, "tok1");
        Enq(h, "/api/device/mapping", 200, Mapping);

        var r = await c.FetchMappingAsync(null);
        var up = Assert.IsType<MappingFetchResult.Updated>(r);
        Assert.Equal(42, up.Mapping.Version);
        Assert.Equal("/api/device/session", h.Requests[0].Path);         // önce oturum
        Assert.Equal("/api/device/mapping", h.Requests[1].Path);
        Assert.Equal("tok1", h.Requests[1].Auth);                        // Bearer jeton
    }

    [Fact]
    public async Task Fetch_304_NotModified_ve_IfNoneMatch_gonderir()
    {
        var (c, h) = Kur();
        Sess(h, "tok1");
        Enq(h, "/api/device/mapping", 304, "");

        var r = await c.FetchMappingAsync(currentVersion: 42);
        Assert.IsType<MappingFetchResult.NotModified>(r);
        Assert.Equal("W/\"v42\"", h.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task Fetch_401_yeniden_oturum_sonra_200()
    {
        var (c, h) = Kur();
        Sess(h, "tok1");
        Enq(h, "/api/device/mapping", 401, "");
        Sess(h, "tok2");                                     // 401 sonrası yeniden oturum
        Enq(h, "/api/device/mapping", 200, Mapping);

        var r = await c.FetchMappingAsync(null);
        Assert.IsType<MappingFetchResult.Updated>(r);
        Assert.Equal("tok1", h.Requests[1].Auth);            // ilk deneme eski jeton
        Assert.Equal("tok2", h.Requests[3].Auth);            // tekrar yeni jetonla
    }

    [Fact]
    public async Task Fetch_401_iki_kez_AuthFailed()
    {
        var (c, h) = Kur();
        Sess(h, "tok1");
        Enq(h, "/api/device/mapping", 401, "");
        Sess(h, "tok2");
        Enq(h, "/api/device/mapping", 401, "");

        Assert.IsType<MappingFetchResult.AuthFailed>(await c.FetchMappingAsync(null));
    }

    [Fact]
    public async Task Oturum_basarisiz_Failed()
    {
        var (c, h) = Kur();
        Enq(h, "/api/device/session", 500, "patladi");
        Assert.IsType<MappingFetchResult.Failed>(await c.FetchMappingAsync(null));
    }

    [Fact]
    public async Task Jeton_omru_icinde_yeniden_kullanilir()
    {
        var now = DateTimeOffset.UtcNow;
        var (c, h) = Kur(now);
        Sess(h, "tok1", expiresIn: 900);
        Enq(h, "/api/device/mapping", 200, Mapping);
        Enq(h, "/api/device/mapping", 200, Mapping);

        await c.FetchMappingAsync(null);
        await c.FetchMappingAsync(null);

        // İki fetch, tek oturum (jeton %75 ömrü içinde yeniden kullanıldı).
        Assert.Equal(1, h.Requests.Count(r => r.Path == "/api/device/session"));
        Assert.Equal(2, h.Requests.Count(r => r.Path == "/api/device/mapping"));
    }
}
