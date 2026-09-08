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

        // ── AÇILIŞ KURTARMASI DİNLEYİCİDEN SONRA, ARKA PLANDA ───────────────────────────
        // ⚠️ Bu ikisi eskiden `listener.Start()`'tan ÖNCE ve BEKLEYEREK çalışıyordu; ölçülen bedeli:
        // 3 takılı komutla ~4 dk, 4 takılı komutla ~2 dk 20 sn boyunca kasa ajana HİÇ ulaşamadı
        // (2026-09-07). Süre takılı komut sayısıyla büyüyor, yani "bir şeyler ters gitti, yeniden
        // başlatalım" anında kasa EN UZUN süre kör kalıyordu — tam da en kötü zamanda.
        //
        // Arka planda koşmaları güvenli: terminale erişim `LocalSaleHandler`'ın terminal-başına TEK
        // işlem kilidinden geçiyor, dolayısıyla araya giren bir satış kurtarmayla ÇAKIŞMAZ, sıraya
        // girer. Kurtarmanın kendisi zaten hiçbir zaman SALE tekrarlamıyor; yalnız soruyor.
        var acilisIsleri = Task.Run(async () =>
        {
            try { await _handler.RecoverPendingAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogError(e, "açılış kurtarması hata verdi"); }
            // AÇILIŞ = bağlantının yeniden kurulduğu an. Geri çekilme BİR TURLUK atlanıyor:
            // kuyrukta bekleyen kayıtların bekleme sebebi çoğu zaman ağın gitmiş olmasıdır ve
            // ağ geri geldiğinde beklemeye devam etmenin bir anlamı yok (W20).
            try { await _handler.DrainOutboxAsync(ct, ignoreBackoff: true); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogError(e, "açılış drain hata verdi"); }
            Temizle();
        }, ct);

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
            try { await acilisIsleri; } catch { /* kapanış */ }
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

            // KATEGORİYE GÖRE YOL AYRIMI. Zarfın başındaki `MessageCategory` okunur ve iki ayrı
            // ayrıştırıcıdan biri seçilir. Ödeme yolu paranın geçtiği yol ve çalışıyor; iptali oraya
            // `if` ile eklemek, tek bir hatanın satışı da bozması demekti.
            if (ReversalRequestParser.PeekCategory(body) == "Reversal")
            {
                var iptal = ReversalRequestParser.Parse(body);
                if (iptal is ReversalParseResult.Invalid ig)
                {
                    _log.LogWarning("geçersiz ReversalRequest: {Reason} — {Detail}", ig.Reason, ig.Detail);
                    Respond(ctx, 400, $"{{\"error\":\"{ig.Reason}\"}}");
                    return;
                }
                var iptalReq = ((ReversalParseResult.Ok)iptal).Request;
                _log.LogInformation("[istek] Reversal ServiceID={Svc} SaleID={Sale} paymentId={Pay}",
                    iptalReq.ServiceId, Bos(iptalReq.SaleId), iptalReq.PaymentId);
                Respond(ctx, 200, await _handler.HandleReversalAsync(iptalReq, ct));
                return;
            }

            var parse = SaleToPoiRequestParser.Parse(body);
            if (parse is SaleToPoiParseResult.Invalid inv)
            {
                _log.LogWarning("geçersiz SaleToPOIRequest: {Reason} — {Detail}", inv.Reason, inv.Detail);
                // Kasa HTTP koduna bakmaz; yine de biçim hatasında 400 net. (Gerçek kasadan beklenmez.)
                Respond(ctx, 400, $"{{\"error\":\"{inv.Reason}\"}}");
                return;
            }

            var req = ((SaleToPoiParseResult.Ok)parse).Request;

            // KOMUT KAYNAĞI (W18). "Bu satışı kim tetikledi" sorusu bugün üç kez çıktı ve her
            // seferinde cevaplayamadım: istekte KİM olduğuna dair hiçbir şey loglanmıyordu.
            // Kişisel veri/PAN YOK — yalnız zarfın kimlik alanları.
            _log.LogInformation("[istek] Payment ServiceID={Svc} SaleID={Sale} saleSessionId={Oturum} paymentId={Pay}",
                req.ServiceId, Bos(req.SaleId), Bos(req.SaleSessionId), req.PaymentId);

            var resp = await _handler.HandleAsync(req, ct);   // GET→terminal→ÖNCE bildir→SONRA dön
            Respond(ctx, 200, resp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "istek işlenemedi");
            try { Respond(ctx, 500, "{\"error\":\"internal\"}"); } catch { /* bağlantı kopmuş olabilir */ }
        }
    }

    /// <summary>Boş/eksik alanı logda "(yok)" göster — boş dize ile eksik alan ayırt edilsin.</summary>
    private static string Bos(string? s) => string.IsNullOrWhiteSpace(s) ? "(yok)" : s;

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
    /// <summary>
    /// Kesin sonuca ulaşmış ESKİ komut kayıtlarını siler (§12.3 retention).
    ///
    /// <para><b>Pencere neden 7 gün:</b> iki yönlü bir kısıt var. KISA olursa kasanın geç gelen bir
    /// tekrarı "yeni komut" sanılır ve aynı tahsilat İKİNCİ KEZ yapılır. UZUN (ya da sonsuz) olursa
    /// — bugüne kadarki hâli, çünkü <c>Purge</c> hiçbir yerden çağrılmıyordu — kasanın 48 saat sonra
    /// yeniden kullanabileceği bir ServiceID eski kayda çarpar ve satış hiç yapılmadan "saklanan
    /// sonuç" olarak REPLAY edilir; kasa başarılı sanır, para alınmaz. 7 gün, kasanın 24 saatlik
    /// tekrar penceresinin üstünde ve nexo'nun 48 saatlik tekillik asgarisinin rahatça ilerisinde.</para>
    ///
    /// <para><b>Uçuştaki kayıtlar ASLA silinmez</b> (<see cref="CommandStore.Purge"/> yalnız
    /// COMPLETED/EXPIRED/REJECTED siler): <c>UNKNOWN</c> bir kaydı atmak, çözülmemiş bir tahsilatı
    /// kaybetmektir.</para>
    /// </summary>
    private void Temizle()
    {
        try
        {
            var sinir = DateTimeOffset.UtcNow.AddDays(-RetentionGun).ToUnixTimeMilliseconds();
            var silinen = _store.Purge(sinir);
            _sonTemizlik = DateTimeOffset.UtcNow;
            if (silinen > 0) _log.LogInformation("komut kaydı temizliği: {Adet} eski kayıt silindi ({Gun} günden eski)", silinen, RetentionGun);
        }
        catch (Exception e) { _log.LogError(e, "komut kaydı temizliği hata verdi"); }
    }

    private const int RetentionGun = 7;
    private DateTimeOffset _sonTemizlik = DateTimeOffset.MinValue;
    /// <summary>Son periyodik kurtarma (W31). Açılış kurtarması ayrı koşar, burayı beklemez.</summary>
    private DateTimeOffset _sonKurtarma = DateTimeOffset.UtcNow;

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }

            // Günde bir: eski kayıtları temizle. Sürekli açık kalan bir kasada açılış tek başına yetmez.
            if (DateTimeOffset.UtcNow - _sonTemizlik >= TimeSpan.FromDays(1)) Temizle();
            try { await _handler.DrainOutboxAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogWarning(e, "periyodik drain hata verdi"); }

            // ── W31: ÇÖZÜLMEMİŞ KOMUTLAR PERİYODİK YOKLANIR ──────────────────────────
            // Kurtarma ŞİMDİYE KADAR YALNIZ AÇILIŞTA koşuyordu. Yani belirsiz kalmış bir deneme,
            // ajan yeniden başlatılana kadar öyle kalıyordu — kasa da o denemeyi bekleyerek
            // takılıyordu (7 Eylül'de ölçüldü: iki kart denemesi saatlerce UNKNOWN'da kaldı).
            //
            // Sıklık 30 sn DEĞİL, 60 sn: her yoklama cihaza gidiyor ve terminal kilidini alıyor.
            // Daha sık koşmak, takılı tek bir komut yüzünden terminali sürekli meşgul edip
            // gerçek satışı geciktirirdi. 60 sn, kasiyerin beklemesi ile cihazın boş kalması
            // arasındaki dengede: yeniden başlatmayı beklemekten kat kat iyi, cihazı yormaz.
            if (DateTimeOffset.UtcNow - _sonKurtarma >= TimeSpan.FromSeconds(60))
            {
                _sonKurtarma = DateTimeOffset.UtcNow;
                try { await _handler.RecoverPendingAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                { _log.LogWarning(e, "periyodik kurtarma hata verdi"); }
            }
        }
    }
}
