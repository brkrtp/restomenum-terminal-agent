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

    private readonly IKurulumIslemleri? _kurulum;
    private readonly DeviceConfigSetupKaynakBilgisi? _eklentiKaynak;
    private readonly BakimKapisi _bakim;
    private readonly KurulumIstekIsleyici? _kurulumIsleyici;

    public AgentWorker(
        IOptions<AgentOptions> opt, ILogger<AgentWorker> log, IHostApplicationLifetime lifetime,
        LocalSaleHandler handler, Outbox outbox, CommandStore store, IKurulumIslemleri? kurulum = null,
        DeviceConfigSetupKaynakBilgisi? eklentiKaynak = null, BakimKapisi? bakim = null)
    {
        _bakim = bakim ?? new BakimKapisi();
        _eklentiKaynak = eklentiKaynak;
        _opt = opt.Value;
        _log = log;
        _lifetime = lifetime;
        _handler = handler;
        _outbox = outbox;
        _kurulum = kurulum;
        _store = store;
        // RM-0389: kurulum istek anahtarı süreç başına üretilir (sayfaya gömülür, her POST'ta geri gelmeli).
        // Bekleyen iş YAŞ SINIRI OLMADAN ve kalıcı reddedilenler dahil sayılır (denetim 2, Codex 4).
        if (kurulum is not null)
            _kurulumIsleyici = new KurulumIstekIsleyici(kurulum, _bakim, KurulumGuvenlik.YeniAnahtar(), DinleyiciPortu(),
                () => new BekleyenIsler(_outbox.Depth(), _outbox.RejectedCount(), _store.Pending().Count), BakimBeklemesi);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("yerel agent başlıyor: dinleyici={Prefix} yol={Path} store={Store}",
            _opt.ListenPrefix, _opt.ListenPath, _opt.ResolveStorePath());

        var bekleyen = _outbox.Depth();
        if (bekleyen > 0) _log.LogWarning("outbox'ta {Adet} bildirilmemiş sonuç var — drain edilecek", bekleyen);

        // ── W68: KALICI RET AÇILIŞTA GÖRÜNÜR ──────────────────────────────────────
        // 2026-09-09 14:42: başarılı bir fiş iptalinin bildirimi 400 ile reddedildi, kayıt silindi
        // ve kuyruk 0 gösterdi — defter platformda eksikken her şey yolunda görünüyordu ve bu
        // saatlerce fark edilmedi. Sayı artık saklanıyor ve BURADA söyleniyor: sessizlik, bu sınıf
        // için "sorun yok" ile "sorun görünmez" arasındaki farkı yok ediyordu.
        var reddedilen = _outbox.RejectedCount();
        if (reddedilen > 0)
            _log.LogError("{Adet} bildirim KALICI REDDEDİLDİ (platform gövdeyi 400 ile geri çevirdi) — " +
                "DEFTER PLATFORMDA EKSİK OLABİLİR. Bunlar kendiliğinden gönderilmez; " +
                "sebep giderildikten sonra operatör yeniden göndermeli.", reddedilen);
        var yarim = _store.Pending().Count;
        if (yarim > 0) _log.LogWarning("{Adet} komut yarım kalmış — açılışta terminale sorulacak", yarim);

        // RM-0389: servis kurtarma ayarı yoksa kurulum sayfası ajanı yeniden başlatamaz — açılışta söyle.
        var yenidenBaslatmaEngeli = _kurulum?.YenidenBaslatmaEngeli();
        if (yenidenBaslatmaEngeli is not null)
            _log.LogCritical("KURULUM SAYFASI YENİDEN BAŞLATAMAZ: {Engel}", yenidenBaslatmaEngeli);

        HttpListener listener;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add(_opt.ListenPrefix);
            // W149: kurulum sayfası ajanı yeniden başlatabiliyor; yeni süreç, eskisi portu
            // bırakana kadar bağlanamaz. Kısa ve SINIRLI yeniden deneme — sonsuz değil.
            for (var deneme = 1; ; deneme++)
            {
                try { listener.Start(); break; }
                catch (HttpListenerException) when (deneme < 10)
                {
                    await Task.Delay(500, ct);
                    listener = new HttpListener();
                    listener.Prefixes.Add(_opt.ListenPrefix);
                }
            }
        }
        catch (Exception e)
        {
            // Port/URL-ACL/yetki: LocalSystem servisi olarak çalıştır. Sessizce devam etmek yerine dur.
            _log.LogCritical(e, "yerel dinleyici {Prefix} açılamadı (URL ACL / port / yetki?) — servis duruyor", _opt.ListenPrefix);
            _lifetime.StopApplication();
            return;
        }

        _log.LogInformation("yerel dinleyici ayakta: {Prefix} (yol {Path})", _opt.ListenPrefix, _opt.ListenPath);
        // W150: kanalın kaynağı görünür olsun — "dosya BOZUK" sessizce eski adrese dönmek değil, uyarıdır.
        if (_eklentiKaynak is not null)
        {
            if (_eklentiKaynak.Host is null && _eklentiKaynak.Kaynak != "yok")
                _log.LogWarning("eklenti config kanalı KAPALI: {Kaynak}", _eklentiKaynak.Kaynak);
            else
                _log.LogInformation("eklenti config kanalı: {Kaynak} → {Host}", _eklentiKaynak.Kaynak, _eklentiKaynak.Host ?? "(yok)");
        }
        KurulumSayfasiniGerekirseAc();

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
            try { await KapidanAsync(() => _handler.RecoverPendingAsync(ct)); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogError(e, "açılış kurtarması hata verdi"); }
            // AÇILIŞ = bağlantının yeniden kurulduğu an. Geri çekilme BİR TURLUK atlanıyor:
            // kuyrukta bekleyen kayıtların bekleme sebebi çoğu zaman ağın gitmiş olmasıdır ve
            // ağ geri geldiğinde beklemeye devam etmenin bir anlamı yok (W20).
            try { await KapidanAsync(() => _handler.DrainOutboxAsync(ct, ignoreBackoff: true)); }
            catch (Exception e) when (e is not OperationCanceledException) { _log.LogError(e, "açılış drain hata verdi"); }
            Temizle();
        }, ct);

        using var iptalKaydi = ct.Register(() => { try { listener.Stop(); } catch (Exception e) { KapanisHatasi("dinleyici durdurma", e); } });
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
            try { listener.Close(); } catch (Exception e) { KapanisHatasi("dinleyici kapatma", e); }
            try { await drainLoop; } catch (Exception e) { KapanisHatasi("periyodik döngü", e); }
            try { await acilisIsleri; } catch (Exception e) { KapanisHatasi("açılış işleri", e); }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var yol = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";

            // W149: KURULUM SAYFASI — /nexo'dan ÖNCE ve ondan bağımsız. Kayıtsız/eşleşmemiş
            // ajanın da bu sayfayı sunabilmesi bütün özelliğin varlık sebebi.
            if (yol.StartsWith(KurulumIstek.Yol, StringComparison.OrdinalIgnoreCase))
            { await KurulumAsync(ctx, yol, ct); return; }

            if (!string.Equals(yol, _opt.ListenPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            { Respond(ctx, 404, "{\"error\":\"not found\"}"); return; }
            if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            { Respond(ctx, 405, "{\"error\":\"POST only\"}"); return; }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync(ct);

            // W45 — REDDEDİLEN isteğin KAYNAĞI. Kabul edilenler için bu bilgi W18'de eklenmişti
            // ([istek] satırı), reddedilenler dışarıda kalmıştı: 18:57:53'te gelen bozuk isteğin
            // kimden geldiğini SÖYLEYEMEDİK. Yalnız reddetme dallarında kullanılıyor — kabul edilen
            // istek yolunda tek bir satır bile değişmiyor.
            // ⚠️ GÖVDE YOK: `IstekKaynagi.Tanimla`'nın gövde parametresi bile yok. Reddedilmiş bir
            // gövde de kart/kişisel veri taşıyabilir; biçim bozukluğu içeriği zararsız yapmaz.
            string Kaynak() => IstekKaynagi.Tanimla(
                ctx.Request.RemoteEndPoint?.ToString(), ctx.Request.UserAgent, ctx.Request.ContentLength64);

            // KATEGORİYE GÖRE YOL AYRIMI. Zarfın başındaki `MessageCategory` okunur ve iki ayrı
            // ayrıştırıcıdan biri seçilir. Ödeme yolu paranın geçtiği yol ve çalışıyor; iptali oraya
            // `if` ile eklemek, tek bir hatanın satışı da bozması demekti.
            // W106 — SALT-OKUNUR FİŞ SORGUSU. Ödeme ve iptal yollarının İKİSİNDEN de önce
            // bakılıyor çünkü hiçbir şey DENEMİYOR: cihaza gitmez, kilit almaz, defteri
            // değiştirmez. Bu yüzden satış sürerken bile anında cevaplanabilir.
            var kategori = ReversalRequestParser.PeekCategory(body);
            if (kategori == TicketQueryParser.Category)
            {
                var sorgu = TicketQueryParser.Parse(body);
                if (sorgu is null)
                {
                    _log.LogWarning("geçersiz TicketQuery [{Kaynak}]", Kaynak());
                    Respond(ctx, 400, "{\"error\":\"MALFORMED\"}");
                    return;
                }
                _log.LogInformation("[istek] TicketQuery ServiceID={Svc} POIID={Poi} ticketId={Fis}",
                    sorgu.ServiceId, sorgu.PoiId, sorgu.TicketId ?? "(açık fiş)");
                Respond(ctx, 200, _handler.HandleTicketQuery(sorgu));
                return;
            }

            // RM-0389: para/fiş değiştiren her istek BAKIM KAPISINDAN girer ve yanıtı (bildirim dahil)
            // dönene kadar içeride sayılır. Kapı kapalıysa istek KABUL EDİLMEZ — hiçbir şey denenmez,
            // kasa "ulaşılamadı" görür; kurulum işlemi bitince ajan yeniden başlar.
            using var giris = _bakim.Gir();
            if (giris is null)
            {
                _log.LogWarning("istek reddedildi: ajan bakımda (kurulum işlemi) — kategori {Kategori} [{Kaynak}]", kategori ?? "Payment", Kaynak());
                Respond(ctx, 503, "{\"error\":\"MAINTENANCE\"}");
                return;
            }

            // W110 — ELLE FİŞ TAMAMLAMA. Ödeme yolundan AYRI: niyeti para almak değil, alınmış
            // parayı taşıyan fişi tamamlamak. İkisini aynı ayrıştırıcıya koymak, tek bir `if`
            // hatasının satışı da bozması demekti.
            if (kategori == TicketFinishParser.Category)
            {
                var bitir = TicketFinishParser.Parse(body, out var redNedeni);
                if (bitir is null)
                {
                    // W129: SEBEP YAZILIR. 2026-09-10 18:32'de kasa geçerli bir istek gönderdi,
                    // reddettim ve log yalnız "ticketFinishId zorunlu" dedi — alan yok muydu,
                    // yanlış yerde miydi, çok mu uzundu belli değildi. Karşı taraf kendi
                    // tarafında hata aradı; oysa kusur bendeydi (sınır 64, anahtar 66).
                    _log.LogWarning("geçersiz TicketFinish: {Neden} [{Kaynak}]", redNedeni ?? "bilinmiyor", Kaynak());
                    Respond(ctx, 400, "{\"error\":\"MALFORMED\"}");
                    return;
                }
                _log.LogInformation("[istek] TicketFinish ServiceID={Svc} POIID={Poi} finishId={Fid}",
                    bitir.ServiceId, bitir.PoiId, bitir.TicketFinishId);
                Respond(ctx, 200, await _handler.HandleTicketFinishAsync(bitir, ct));
                return;
            }

            if (kategori == "Reversal")
            {
                var iptal = ReversalRequestParser.Parse(body);
                if (iptal is ReversalParseResult.Invalid ig)
                {
                    _log.LogWarning("geçersiz ReversalRequest: {Reason} — {Detail} [{Kaynak}]",
                        ig.Reason, ig.Detail, Kaynak());
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
                _log.LogWarning("geçersiz SaleToPOIRequest: {Reason} — {Detail} [{Kaynak}]",
                    inv.Reason, inv.Detail, Kaynak());
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
            // Yanıt yazımı zaten başlamış ya da bağlantı kopmuş olabilir: 500 yazılamazsa istisna YUTULMAZ,
            // türü uyarı olarak loglanır (proje kuralı: boş catch yok). Asıl hata yukarıda loglandı.
            try { Respond(ctx, 500, "{\"error\":\"internal\"}"); }
            catch (Exception yazim) { _log.LogWarning("500 yanıtı yazılamadı: {Tur}", yazim.GetType().Name); }
        }
    }

    /// <summary>
    /// Kapanış hatası YUTULMAZ (boş catch yok) ama kapanışı durdurmaz. Beklenen (iptal, kapatılmış
    /// nesne) Debug, kalanı Warning; yalnız tür + adım loglanır — mesaj istek gövdesi taşıyabilir.
    /// </summary>
    private void KapanisHatasi(string adim, Exception e)
    {
        if (e is OperationCanceledException or ObjectDisposedException)
            _log.LogDebug("kapanış: {Adim} — beklenen {Tur}", adim, e.GetType().Name);
        else
            _log.LogWarning("kapanış: {Adim} hata verdi — {Tur}", adim, e.GetType().Name);
    }

    /// <summary>Boş/eksik alanı logda "(yok)" göster — boş dize ile eksik alan ayırt edilsin.</summary>
    private static string Bos(string? s) => string.IsNullOrWhiteSpace(s) ? "(yok)" : s;

    // ══ W149 — KURULUM SAYFASI ═══════════════════════════════════════════════════
    //
    // ⚠️ /setup/enroll ve /setup/plugin GÖVDELERİ HİÇBİR YERE LOGLANMAZ — kayıt kodu 10 dakika
    // boyunca cihaz bağlama yetkisi, kurulum dizesi ise eklenti sırrını taşır. Bu yordamda gövde
    // yalnız ayrıştırılır ve işleme geçer; hata sebepleri de sırrı içermez (Core garantisi).
    private async Task KurulumAsync(HttpListenerContext ctx, string yol, CancellationToken ct)
    {
        if (_kurulumIsleyici is null || _kurulum is null)
        { Respond(ctx, 404, "{\"error\":\"setup not available on this platform\"}"); return; }

        // Gövde yalnız POST'ta okunur; yöntem/güvenlik/doğrulama kararları Core'da (KurulumIstekIsleyici).
        var govde = "";
        if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
            govde = await reader.ReadToEndAsync(ct);
        }
        var istek = new KurulumIstegi(
            ctx.Request.HttpMethod,
            yol.Length > KurulumIstek.Yol.Length ? yol[(KurulumIstek.Yol.Length + 1)..] : "",
            ctx.Request.RemoteEndPoint is { } uc && IPAddress.IsLoopback(uc.Address),
            ctx.Request.Headers["Host"], ctx.Request.Headers["Origin"], ctx.Request.ContentType,
            ctx.Request.Headers[KurulumGuvenlik.AnahtarBasligi], govde);

        var yanit = await _kurulumIsleyici.IsleAsync(istek, ct);
        if (yanit.Log is not null)
        {
            if (yanit.Log.StartsWith("KRİTİK", StringComparison.Ordinal)) _log.LogCritical("[kurulum] {Mesaj}", yanit.Log);
            else if (yanit.Durum >= 400) _log.LogWarning("[kurulum] {Mesaj} (istemci {Istemci})", yanit.Log, ctx.Request.RemoteEndPoint?.ToString() ?? "?");
            else _log.LogInformation("[kurulum] {Mesaj}", yanit.Log);
        }

        // Yanıt GİTTİKTEN sonra yeniden başlat — tarayıcı "yeniden başlıyor" mesajını görsün. Yanıtın
        // ulaşması koşul DEĞİL: tarayıcı kapanmış olsa da işlem tamamlanmıştır, yeniden başlatma kurulur.
        try
        {
            foreach (var b in yanit.Basliklar) ctx.Response.Headers[b.Key] = b.Value;
            if (yanit.Html) RespondHtml(ctx, yanit.Govde);
            else Respond(ctx, yanit.Durum, yanit.Govde);
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
        { _log.LogWarning("[kurulum] yanıt yazılamadı ({Hata}) — işlem yine de tamamlandı", e.GetType().Name); }

        if (yanit.YenidenBaslat)
            _ = Task.Run(async () => { await Task.Delay(800, CancellationToken.None); if (!_kurulumIsleyici.YenidenBaslat(out var tur)) _log.LogError("[kurulum] ajan yeniden başlatılamadı ({Tur}) — satış kapalı kalıyor, elle yeniden başlatın", tur); });
    }

    /// <summary>Bakım için uçuştaki satışların bitmesini bekleme tavanı (kart penceresi + bildirim).</summary>
    private static readonly TimeSpan BakimBeklemesi = TimeSpan.FromSeconds(120);

    /// <summary>Dinleyici portu (Host/Origin denetimi için). <c>http://+:7788/</c> gibi joker önekler de okunur.</summary>
    private int DinleyiciPortu()
    {
        var m = System.Text.RegularExpressions.Regex.Match(_opt.ListenPrefix, @":(\d+)/?");
        return m.Success ? int.Parse(m.Groups[1].Value) : 80;
    }

    /// <summary>Açılışta kayıtsız ya da eşleşmemişse kurulum sayfasını tarayıcıda açar — ilk kurulumda
    /// kullanıcı hiçbir şey aramaz. Masaüstü yoksa (servis) sessizce geçer.</summary>
    private void KurulumSayfasiniGerekirseAc()
    {
        if (_kurulum is null) return;
        try
        {
            var d = _kurulum.Durum();
            if (d.Tamam) return;
            var url = _opt.ListenPrefix.TrimEnd('/') + KurulumIstek.Yol;
            _log.LogWarning("kurulum eksik (kayıtlı={K}, eşli={E}, eklenti={P}) — sayfa açılıyor: {Url}",
                d.Kayitli, d.Esli, d.EklentiAdresi ?? "yok", url);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _log.LogInformation("kurulum sayfası kendiliğinden açılamadı ({Hata}) — elle açın: {Prefix}setup",
                e.GetType().Name, _opt.ListenPrefix);
        }
    }

    private const string NOTFOUND = "{\"error\":\"not found\"}";

    private static void RespondHtml(HttpListenerContext ctx, string html)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        GovdeYazVeKapat(ctx, Encoding.UTF8.GetBytes(html));
    }

    private static void Respond(HttpListenerContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        GovdeYazVeKapat(ctx, Encoding.UTF8.GetBytes(json));
    }

    /// <summary>HEAD'e gövde YAZILMAZ (HttpGovde): yalnız durum kodu ve boş gövde; diğer yöntemlerde gövde.</summary>
    private static void GovdeYazVeKapat(HttpListenerContext ctx, byte[] bytes)
    {
        if (HttpGovde.Yazilir(ctx.Request.HttpMethod))
        {
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        else ctx.Response.ContentLength64 = 0;
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
    /// <summary>
    /// RM-0389 (denetim 2, Codex 2): terminale, deftere ya da platforma dokunan ARKA PLAN işleri de
    /// bakım kapısından girer. Kurtarma bir komutu COMPLETED yapıp terminal kilidini bırakır ve
    /// bildirimi ondan SONRA kuyruğa koyar; kapı dışında kalsaydı kaldırma o arada kimliği sökebilir,
    /// sonuç hiç bildirilemezdi. Bakımdayken iş o tur YAPILMAZ (kayıtlar durur, sonraki turda/açılışta).
    /// </summary>
    private async Task KapidanAsync(Func<Task> is_)
    {
        using var giris = _bakim.Gir();
        if (giris is null) return;
        await is_();
    }

    private void Temizle()
    {
        using var giris = _bakim.Gir();
        if (giris is null) return;
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
            // W116 — 30 sn'den 10 sn'ye indirildi (kullanıcı isteği).
            // ⚠️ Turun KENDİSİ ucuz: boşta üç küçük yerel SQLite sorgusu. Sıklığı artırmak
            // ağ trafiğini artırmıyor çünkü hem outbox kayıtlarının hem yarım fişlerin KENDİ
            // `next_attempt_at`i var; tur yalnız sırası GELMİŞ işi görür. Kurtarma turu ayrıca
            // 60 sn'lik kendi kapısında (`_sonKurtarma`), temizlik günlük kapıda.
            // Kazanç: kâğıt takıldıktan sonra fişin kapanma gecikmesi 30 sn'den 10 sn'ye iner.
            try { await Task.Delay(AjanTuru.DrainAraligi, ct); }
            catch (OperationCanceledException) { break; }

            // Günde bir: eski kayıtları temizle. Sürekli açık kalan bir kasada açılış tek başına yetmez.
            if (DateTimeOffset.UtcNow - _sonTemizlik >= TimeSpan.FromDays(1)) Temizle();
            try { await KapidanAsync(() => _handler.DrainOutboxAsync(ct)); }
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
                try { await KapidanAsync(() => _handler.RecoverPendingAsync(ct)); }
                catch (Exception e) when (e is not OperationCanceledException)
                { _log.LogWarning(e, "periyodik kurtarma hata verdi"); }
            }
        }
    }
}
