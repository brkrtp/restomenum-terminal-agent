using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host;

/// <summary>
/// Yerel agent yaşam döngüsü (yerel mimari, K-21): açılışta yarım komutları kurtar + outbox'ı drain et,
/// sonra <b>yerel HTTP dinleyici</b> aç — kasa <c>POST /nexo</c> ile SaleToPOIRequest yollar, ajan
/// senkron <c>SaleToPOIResponse</c> döner ve sonucu platforma bildirir (<see cref="LocalSaleHandler"/>).
///
/// <para><b>Kapanış en riskli an</b> (kart 20–32 sn, kurtarma ~100 sn): host kapanış süresi
/// <see cref="Program"/>'da uzatılır, yoksa süreç tam kart çekilirken kesilir ve sonuç yazılamaz.</para>
///
/// <para><b>Bağlantı yanıta kadar açık:</b> HttpListener isteği ben yanıt yazana dek tutar; kartın
/// PIN/imza/onayı (kasa 180 sn bekliyor) bu süre içinde. Erken kapatmak kasada terminalUnreachable
/// üretir. Kasa HTTP koduna BAKMAZ — sonucu <c>Result</c>/<c>ErrorCondition</c> ile taşırız.</para>
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly AgentOptions _opt;
    private readonly ILogger<AgentWorker> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LocalSaleHandler _handler;
    private readonly Outbox _outbox;
    private readonly CommandStore _store;

    public AgentWorker(
        IOptions<AgentOptions> opt, ILogger<AgentWorker> log, IHostApplicationLifetime lifetime,
        LocalSaleHandler handler, Outbox outbox, CommandStore store)
    {
        _opt = opt.Value;
        _log = log;
        _lifetime = lifetime;
        _handler = handler;
        _outbox = outbox;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("yerel agent başlıyor: dinleyici={Prefix} yol={Path} store={Store}",
            _opt.ListenPrefix, _opt.ListenPath, _opt.ResolveStorePath());

        var bekleyen = _outbox.Depth();
        if (bekleyen > 0) _log.LogWarning("outbox'ta {Adet} bildirilmemiş sonuç var — drain edilecek", bekleyen);
        var yarim = _store.Pending().Count;
        if (yarim > 0) _log.LogWarning("{Adet} komut yarım kalmış — açılışta terminale sorulacak", yarim);

        // Açılış: ÖNCE kurtarma (yarım komutları terminale sorup çöz), SONRA outbox drain.
        try { await _handler.RecoverPendingAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { _log.LogError(e, "açılış kurtarması hata verdi"); }
        try { await _handler.DrainOutboxAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { _log.LogError(e, "açılış drain hata verdi"); }

        HttpListener listener;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add(_opt.ListenPrefix);
            listener.Start();
        }
        catch (Exception e)
        {
            // Port/URL-ACL/yetki: LocalSystem servisi olarak çalıştır. Sessizce devam etmek yerine dur.
            _log.LogCritical(e, "yerel dinleyici {Prefix} açılamadı (URL ACL / port / yetki?) — servis duruyor", _opt.ListenPrefix);
            _lifetime.StopApplication();
            return;
        }

        _log.LogInformation("yerel dinleyici ayakta: {Prefix} (yol {Path})", _opt.ListenPrefix, _opt.ListenPath);

        using var iptalKaydi = ct.Register(() => { try { listener.Stop(); } catch { /* kapanış */ } });
        var drainLoop = DrainLoopAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) when (ct.IsCancellationRequested) { break; }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                // Eşzamanlı işle — terminal başına TEK işlem kilidi LocalSaleHandler'da; accept döngüsü bloklanmaz.
                _ = HandleRequestAsync(ctx, ct);
            }
        }
        finally
        {
            try { listener.Close(); } catch { /* kapanış */ }
            try { await drainLoop; } catch { /* kapanış */ }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var yol = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (!string.Equals(yol, _opt.ListenPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            { Respond(ctx, 404, "{\"error\":\"not found\"}"); return; }
            if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            { Respond(ctx, 405, "{\"error\":\"POST only\"}"); return; }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync(ct);

            var parse = SaleToPoiRequestParser.Parse(body);
            if (parse is SaleToPoiParseResult.Invalid inv)
            {
                _log.LogWarning("geçersiz SaleToPOIRequest: {Reason} — {Detail}", inv.Reason, inv.Detail);
                // Kasa HTTP koduna bakmaz; yine de biçim hatasında 400 net. (Gerçek kasadan beklenmez.)
                Respond(ctx, 400, $"{{\"error\":\"{inv.Reason}\"}}");
                return;
            }

            var req = ((SaleToPoiParseResult.Ok)parse).Request;
            var resp = await _handler.HandleAsync(req, ct);   // GET→terminal→ÖNCE bildir→SONRA dön
            Respond(ctx, 200, resp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "istek işlenemedi");
            try { Respond(ctx, 500, "{\"error\":\"internal\"}"); } catch { /* bağlantı kopmuş olabilir */ }
        }
    }

    private static void Respond(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    /// <summary>Periyodik outbox drain — WSS'te oturum bağlanınca yapılan drain'in yerini alır.</summary>
    private async Task DrainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
            try { await _handler.DrainOutboxAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning(e, "periyodik drain hata verdi"); }
        }
    }
}
