using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Restomenum.Agent.Core;

/// <summary>
/// <b>W149 — KURULUM SAYFASI: son kullanıcı cihazını KENDİSİ eşleştirir.</b>
///
/// <para><b>Neden var:</b> bugüne kadar eşleştirme için <c>appsettings.json</c>'a elle kod
/// yazmak, <c>GMP.XML</c>'e elle IP yazmak, terminali menüye alıp YÖNETİCİ komut satırından
/// <c>--pair</c> çalıştırmak gerekiyordu — üstelik kayıt kodu 10 dakika ömürlüyken. Bir restoran
/// sahibi bunu yapamaz; bir teknisyen bile zor yapar.</para>
///
/// <para><b>Nasıl:</b> ajan zaten <c>127.0.0.1:7788</c>'de dinliyor. Aynı dinleyici <c>/setup</c>
/// adresinde tek sayfalık bir kurulum ekranı sunuyor: kayıt kodu, terminal IP'si, eşleştirme
/// düğmesi ve durum. Dosya yok, komut satırı yok. Sayfa yalnız <c>127.0.0.1</c>'den erişilir.</para>
///
/// <para><b>Katman ayrımı:</b> bu dosya SAF — cihaza, dosya sistemine, ağa dokunmaz. Cihaza dokunan
/// işlemler <see cref="IKurulumIslemleri"/> arkasında ve Host.Windows'ta; böylece sayfa ve
/// ayrıştırma sahte işlemlerle test edilebilir.</para>
///
/// <para>⚠️ <b>Kayıt kodu HİÇBİR YERDE loglanmaz.</b> 10 dakika boyunca cihaz bağlama yetkisi
/// taşır; log satırı ondan uzun yaşar.</para>
/// </summary>
public interface IKurulumIslemleri
{
    /// <summary>Anlık durum. Cihaza dokunabilir (sayaçlar için Echo); satış sürüyorsa dokunmaz.</summary>
    KurulumDurumu Durum();

    /// <summary>Tek kullanımlık kodla platforma kaydolur. Kod loglanmaz.</summary>
    Task<KurulumSonucu> KaydolAsync(string kod, CancellationToken ct);

    /// <summary>Terminal IP/port'unu yapılandırmaya yazar. DLL bunu yalnız açılışta okur → yeniden başlatma gerekir.</summary>
    KurulumSonucu TerminalAyarla(string ip, int port);

    /// <summary>Terminalle eşleşir (<c>StartPairingInit</c>). Terminal o an eşleştirme menüsünde olmalı.</summary>
    KurulumSonucu Esles();

    /// <summary>
    /// W150: eklenti kurulum dizesini CANLI doğrular (oturum + eşleme) ve ancak başarılıysa veri
    /// dizinine yazar. Sır loglanmaz. Kanal açılışta kurulduğundan yeniden başlatma gerekir.
    /// </summary>
    Task<KurulumSonucu> EklentiAyarlaAsync(DeviceConfigSetup ayar, CancellationToken ct);

    /// <summary>
    /// W152: yerel ağı tarar ve terminal portunda (7500) yanıt veren adresleri döndürür. Cihaza
    /// GMP çağrısı YAPMAZ — yalnız ham TCP bağlanma denemesi; bu yüzden satışa engel olmaz ve
    /// eşleşme gerektirmez. "Port açık" ile "doğrulanmış terminal" AYRI: yalnız adayları listeler.
    /// </summary>
    Task<IReadOnlyList<TerminalAday>> TerminalleriTaraAsync(CancellationToken ct);

    /// <summary>
    /// Kurulu cihazı KALDIRIR (<see cref="CihazKaldirici"/>): eklenti bağlantısı silinir, platform
    /// kaydı (connectorId) adı değiştirilerek saklanır; ajan kurulmamış duruma döner. Terminalde iş
    /// sürüyorsa ya da bekleyen/reddedilmiş/yarım iş varsa reddeder. Cihaz anahtarı ve GMP.XML KORUNUR.
    /// Çağıran, işlemden ÖNCE <see cref="BakimKapisi"/>'ni kapatmış olmalı; <paramref name="say"/>
    /// cihaz kilidi alındıktan SONRA çağrılır.
    /// </summary>
    KurulumSonucu Kaldir(Func<BekleyenIsler> say);

    /// <summary>
    /// Yeniden başlatma şu an mümkün değilse sebebi (<see cref="YenidenBaslatmaKarari"/>);
    /// <c>null</c> = mümkün. Yeniden başlatma gerektiren işlemler bundan ÖNCE sorar.
    /// </summary>
    string? YenidenBaslatmaEngeli();

    /// <summary>Ajanı yeniden başlatır (IP ya da eklenti değişikliğinden sonra).</summary>
    void YenidenBaslat();
}

/// <summary>W152 — taramada bulunan aday. <paramref name="Ip"/> terminal portuna TCP bağlanabildi;
/// GMP ile doğrulanmadı (o eşleşme gerektirir ve satışı bölerdi).</summary>
public sealed record TerminalAday(string Ip, int Port);

/// <param name="Kayitli">Platforma kayıtlı mı (connectorId var mı).</param>
/// <param name="Esli">Terminalle eşli mi (bu süreçte).</param>
/// <param name="EklentiAdresi">Config kanalının bağlı olduğu eklenti sunucusu (yalnız host); <c>null</c> = kanal yok.
/// Sır bu kayda HİÇ girmez — sayfa ve JSON yalnız adresi görür.</param>
/// <param name="Not">Serbest metin: son hata ya da uyarı; <c>null</c> = yok.</param>
public sealed record KurulumDurumu(
    bool Kayitli, bool Esli,
    string? TerminalIp = null, int? TerminalPort = null,
    string? CihazModeli = null, string? CihazSeri = null,
    uint? ZNo = null, uint? FisNo = null,
    bool? KagitUyarisi = null,
    string? Not = null,
    string? EklentiAdresi = null)
{
    /// <summary>Üç adım da tamam mı: kayıt, eşleşme, eklenti. Açılışta sayfa buna göre kendiliğinden açılır.</summary>
    public bool Tamam => Kayitli && Esli && EklentiAdresi is not null;
}

/// <param name="YenidenBaslatmaGerekli">Ayar yazıldı ama etkili olması için ajan yeniden başlamalı.</param>
public sealed record KurulumSonucu(bool Basarili, string Mesaj, bool YenidenBaslatmaGerekli = false)
{
    public static KurulumSonucu Tamam(string mesaj, bool yenidenBaslat = false) => new(true, mesaj, yenidenBaslat);
    public static KurulumSonucu Hata(string mesaj) => new(false, mesaj);
}

/// <summary>Kurulum uçlarının istek gövdelerini ayrıştırır. Hata sebebi AYRIŞIR (W129 dersi).</summary>
public static class KurulumIstek
{
    public const string Yol = "/setup";

    /// <summary><c>{"code":"…"}</c> → kod. Boşluk kırpılır. Sebep dolu dönerse geçersiz.</summary>
    public static string? Kod(string govde, out string? neden)
    {
        neden = null;
        var v = Alan(govde, "code", out neden);
        if (v is null) return null;
        if (v.Length > 256) { neden = "kod çok uzun"; return null; }
        return v;
    }

    /// <summary><c>{"ip":"…","port":7500}</c>. Port yoksa 7500.</summary>
    public static (string Ip, int Port)? Terminal(string govde, out string? neden)
    {
        neden = null;
        var ip = Alan(govde, "ip", out neden);
        if (ip is null) return null;
        if (!IPAddress.TryParse(ip, out var adres) || adres.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        { neden = $"'{ip}' geçerli bir IPv4 adresi değil"; return null; }

        var port = 7500;
        try
        {
            using var doc = JsonDocument.Parse(govde);
            if (doc.RootElement.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number)
            {
                // Denetim 2 (Codex 1): GetInt32 taşan sayıda FormatException atıyordu.
                if (!p.TryGetInt32(out port)) { neden = $"port {p.GetRawText()} aralık dışı"; return null; }
            }
        }
        catch (JsonException) { /* ip zaten okundu; port varsayılan */ }
        if (port is < 1 or > 65535) { neden = $"port {port} aralık dışı"; return null; }
        return (ip, port);
    }

    /// <summary><c>{"setup":"base64…"}</c> → çözülmüş ve kurallara vurulmuş eklenti ayarı. Sebep sırrı içermez.</summary>
    public static DeviceConfigSetup? Eklenti(string govde, out string? neden)
    {
        neden = null;
        var dize = Alan(govde, "setup", out neden);
        if (dize is null) return null;
        if (dize.Length > 4096) { neden = "kurulum dizesi çok uzun"; return null; }
        return DeviceConfigSetupParser.TryParse(dize, out neden);
    }

    /// <summary>
    /// <c>{"confirm":"KALDIR"}</c> — kaldırma geri alınamaz, açık onay sözcüğü şart. Büyük/küçük
    /// harf ve i/ı/İ farkı gözetilmez (kaldır, kaldir, KALDİR hepsi geçer); başka her şey reddedilir.
    /// </summary>
    public static bool KaldirmaOnayi(string govde, out string? neden)
    {
        var v = Alan(govde, "confirm", out neden);
        if (v is null) return false;
        if (v.ToUpper(new System.Globalization.CultureInfo("tr-TR")).Replace('İ', 'I') != "KALDIR")
        { neden = "onay için kutuya KALDIR yazın"; return false; }
        return true;
    }

    private static string? Alan(string govde, string ad, out string? neden)
    {
        neden = null;
        try
        {
            using var doc = JsonDocument.Parse(govde);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { neden = "gövde bir JSON nesnesi değil"; return null; }
            if (!doc.RootElement.TryGetProperty(ad, out var v) || v.ValueKind != JsonValueKind.String)
            { neden = $"'{ad}' alanı yok"; return null; }
            var s = v.GetString()?.Trim();
            if (string.IsNullOrEmpty(s)) { neden = $"'{ad}' boş"; return null; }
            return s;
        }
        catch (JsonException) { neden = "gövde JSON değil"; return null; }
    }

    /// <summary>Tarama sonucu → JSON. <paramref name="tarananAg"/> hangi /24 tarandı (kullanıcıya bilgi).</summary>
    public static string JsonTarama(IReadOnlyList<TerminalAday> adaylar, string? tarananAg) =>
        JsonSerializer.Serialize(new
        {
            subnet = tarananAg,
            count = adaylar.Count,
            found = adaylar.Select(a => new { ip = a.Ip, port = a.Port }).ToArray(),
        });

    public static string Json(KurulumSonucu s) =>
        JsonSerializer.Serialize(new { ok = s.Basarili, message = s.Mesaj, restartRequired = s.YenidenBaslatmaGerekli });

    public static string Json(KurulumDurumu d) => JsonSerializer.Serialize(new
    {
        enrolled = d.Kayitli, paired = d.Esli,
        terminalIp = d.TerminalIp, terminalPort = d.TerminalPort,
        deviceModel = d.CihazModeli, deviceSerial = d.CihazSeri,
        zNo = d.ZNo, fisNo = d.FisNo, paperWarning = d.KagitUyarisi, note = d.Not,
        pluginHost = d.EklentiAdresi,
    });
}

/// <summary>
/// Kurulum sayfasının HTML'i. Dış kaynak YOK (CDN, font, stil dosyası) — makine internetsiz de
/// olabilir ve sayfa her koşulda açılmalı. Durum sunucudan gömülü gelir; düğmeler <c>fetch</c> ile
/// <c>/setup/*</c> uçlarına gider.
/// </summary>
public static class KurulumSayfasi
{
    /// <param name="anahtar">Süreç başına üretilen istek anahtarı (<see cref="KurulumGuvenlik"/>); her POST'ta geri gelir.</param>
    public static string Html(KurulumDurumu d, string anahtar = "")
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder(12288);

        // ⚠️ DIŞ KAYNAK YOK: makine internetsiz olabilir; her şey gömülü. Yazı tipi sistem
        // font'u; ızgara/parıltı CSS gradyanıyla. Tema tek: koyu cyberpunk (2026), bilerek.
        sb.Append("""
<!doctype html><html lang="tr"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Yazarkasa Kurulumu</title>
<style>
 :root{
   --bg:#05060d; --bg2:#0a0d1a; --panel:rgba(13,18,33,.72); --line:rgba(0,240,255,.22);
   --cy:#00f0ff; --mg:#ff2bd6; --ok:#00ffa3; --warn:#ffc247; --err:#ff4d6d;
   --ink:#d7e6ff; --dim:#7d8bb0; --glow:0 0 12px;
 }
 *{box-sizing:border-box}
 html,body{margin:0}
 body{
   font-family:"Segoe UI",system-ui,sans-serif; color:var(--ink); min-height:100vh;
   background:
     radial-gradient(1200px 600px at 15% -10%, rgba(255,43,214,.10), transparent 60%),
     radial-gradient(1000px 500px at 110% 10%, rgba(0,240,255,.12), transparent 55%),
     linear-gradient(180deg,var(--bg),var(--bg2));
   background-attachment:fixed;
 }
 body::before{
   content:""; position:fixed; inset:0; z-index:0; pointer-events:none; opacity:.35;
   background-image:
     linear-gradient(rgba(0,240,255,.06) 1px,transparent 1px),
     linear-gradient(90deg,rgba(0,240,255,.06) 1px,transparent 1px);
   background-size:34px 34px; mask-image:linear-gradient(180deg,#000,transparent 85%);
 }
 .wrap{position:relative; z-index:1; max-width:680px; margin:0 auto; padding:34px 18px 60px}
 .hero{margin:0 0 26px}
 .hero .tag{font:600 11px/1 ui-monospace,Consolas,monospace; letter-spacing:.32em;
   color:var(--cy); text-transform:uppercase; opacity:.85}
 h1{font:800 30px/1.05 "Segoe UI",system-ui,sans-serif; margin:10px 0 6px; letter-spacing:.5px;
   background:linear-gradient(90deg,var(--cy),var(--mg)); -webkit-background-clip:text;
   background-clip:text; color:transparent; text-shadow:none}
 .hero .sub{color:var(--dim); font-size:13px}
 .hero .sub b{color:var(--cy); font-weight:600}

 .card{
   position:relative; background:var(--panel); border:1px solid var(--line);
   border-radius:14px; padding:18px 18px 20px; margin:0 0 16px;
   backdrop-filter:blur(6px); box-shadow:0 1px 0 rgba(255,255,255,.03) inset, 0 10px 30px rgba(0,0,0,.35);
   clip-path:polygon(0 0,calc(100% - 16px) 0,100% 16px,100% 100%,16px 100%,0 calc(100% - 16px));
 }
 .card::after{content:""; position:absolute; left:18px; right:18px; top:0; height:1px;
   background:linear-gradient(90deg,transparent,var(--cy),transparent); opacity:.5}
 .card h2{display:flex; align-items:center; gap:9px; font:700 13px/1 ui-monospace,Consolas,monospace;
   letter-spacing:.14em; text-transform:uppercase; color:var(--cy); margin:0 0 14px}
 .card h2 .n{display:inline-grid; place-items:center; width:22px; height:22px; border-radius:6px;
   font-size:11px; color:#04121a; background:linear-gradient(135deg,var(--cy),#7ef9ff);
   box-shadow:var(--glow) rgba(0,240,255,.6)}

 .stat{display:flex; align-items:center; gap:8px; font-weight:600; font-size:14px; margin:6px 0}
 .stat.ok{color:var(--ok); text-shadow:var(--glow) rgba(0,255,163,.35)}
 .stat.warn{color:var(--warn)}
 .stat.err{color:var(--err)}
 .counters{display:flex; flex-wrap:wrap; gap:8px; margin-top:12px}
 .chip{font:600 12px/1 ui-monospace,Consolas,monospace; color:var(--dim);
   border:1px solid var(--line); border-radius:999px; padding:6px 11px; background:rgba(0,240,255,.04)}
 .chip.alarm{color:#04121a; background:var(--err); border-color:var(--err); text-shadow:none}

 label{display:block; font:600 11px/1 ui-monospace,Consolas,monospace; letter-spacing:.12em;
   text-transform:uppercase; color:var(--dim); margin:14px 0 6px}
 label:first-of-type{margin-top:4px}
 .hint{font-size:12px; color:var(--dim); line-height:1.5; margin:2px 0 4px}
 .hint b{color:var(--cy); font-weight:600}
 input,select,textarea{
   width:100%; padding:11px 12px; font-size:15px; color:var(--ink); font-family:inherit;
   background:rgba(2,6,16,.72); border:1px solid var(--line); border-radius:9px; outline:none;
   transition:border-color .15s,box-shadow .15s;
 }
 select{appearance:none; cursor:pointer;
   background-image:linear-gradient(45deg,transparent 50%,var(--cy) 50%),linear-gradient(135deg,var(--cy) 50%,transparent 50%);
   background-position:calc(100% - 18px) 18px,calc(100% - 13px) 18px; background-size:5px 5px,5px 5px; background-repeat:no-repeat}
 textarea{font-family:ui-monospace,Consolas,monospace; font-size:12px; min-height:74px; resize:vertical}
 input:focus,select:focus,textarea:focus{border-color:var(--cy); box-shadow:var(--glow) rgba(0,240,255,.35)}
 .row{display:flex; gap:10px; align-items:end}
 .row .grow{flex:1}

 button{
   margin-top:14px; padding:11px 18px; font:700 13px/1 "Segoe UI",system-ui,sans-serif;
   letter-spacing:.04em; color:#04121a; cursor:pointer; border:0; border-radius:9px;
   background:linear-gradient(135deg,var(--cy),#59f6ff); box-shadow:var(--glow) rgba(0,240,255,.45);
   transition:transform .1s,box-shadow .15s,filter .15s;
 }
 button:hover{filter:brightness(1.08); box-shadow:0 0 20px rgba(0,240,255,.65)}
 button:active{transform:translateY(1px)}
 button:disabled{background:#1b2540; color:var(--dim); box-shadow:none; cursor:default}
 button.ghost{background:transparent; color:var(--cy); border:1px solid var(--cy); box-shadow:none}
 button.ghost:hover{background:rgba(0,240,255,.08); box-shadow:var(--glow) rgba(0,240,255,.35)}
 .card.danger{border-color:rgba(255,77,109,.45)}
 .card.danger h2{color:var(--err)}
 .card.danger h2 .n{background:var(--err); box-shadow:var(--glow) rgba(255,77,109,.6)}
 button.tehlike{background:var(--err); color:#fff; box-shadow:var(--glow) rgba(255,77,109,.45)}

 .msg{margin-top:10px; font-size:13px; min-height:18px; font-family:ui-monospace,Consolas,monospace}
 .msg.ok{color:var(--ok)} .msg.hata{color:var(--err)} .msg.info{color:var(--cy)}
 .foot{text-align:center; color:var(--dim); font:11px/1.6 ui-monospace,Consolas,monospace;
   letter-spacing:.1em; margin-top:26px; opacity:.7}
 [hidden]{display:none!important}
</style></head><body><div class="wrap">
<div class="hero">
 <div class="tag">// RESTOMENUM · AGENT SETUP</div>
 <h1>Yazarkasa Kurulumu</h1>
 <div class="sub">Bu ajan yalnız <b>bu bilgisayardan</b> erişilir · 127.0.0.1</div>
</div>
""");

        // ── DURUM ───────────────────────────────────────────────────────────────────
        sb.Append("<div class=\"card\"><h2><span class=\"n\">◈</span>Durum</h2>");
        sb.Append(d.Kayitli
            ? "<div class=\"stat ok\">✓ Platforma kayıtlı</div>"
            : "<div class=\"stat warn\">⚠ Platforma kayıtlı değil — 1. adımı tamamlayın</div>");
        sb.Append(d.Esli
            ? $"<div class=\"stat ok\">✓ Terminal eşli{(d.CihazModeli is null ? "" : $": {E(d.CihazModeli)}")}{(d.CihazSeri is null ? "" : $" · seri {E(d.CihazSeri)}")}</div>"
            : "<div class=\"stat warn\">⚠ Terminal eşleşmemiş — 2. ve 3. adımı tamamlayın</div>");
        sb.Append(d.EklentiAdresi is not null
            ? $"<div class=\"stat ok\">✓ Eklenti bağlı: {E(d.EklentiAdresi)}</div>"
            : "<div class=\"stat warn\">⚠ Eklenti bağlantısı yok — 4. adımı tamamlayın</div>");
        if (d.ZNo is not null || d.FisNo is not null || d.KagitUyarisi == true)
        {
            sb.Append("<div class=\"counters\">");
            if (d.ZNo is not null) sb.Append($"<span class=\"chip\">Z raporu: {d.ZNo}</span>");
            if (d.FisNo is not null) sb.Append($"<span class=\"chip\">Sıradaki fiş: {d.FisNo}</span>");
            if (d.KagitUyarisi == true) sb.Append("<span class=\"chip alarm\">KÂĞIT BİTTİ</span>");
            sb.Append("</div>");
        }
        if (d.Not is not null) sb.Append($"<div class=\"msg hata\">{E(d.Not)}</div>");
        sb.Append("</div>");

        // ── 1. KAYIT ────────────────────────────────────────────────────────────────
        sb.Append("<div class=\"card\"><h2><span class=\"n\">1</span>Platform kaydı</h2>");
        if (d.Kayitli)
            sb.Append("<div class=\"hint\">Bu cihaz kayıtlı. Yeniden kaydetmek gerekmiyor.</div>");
        else
            sb.Append("""
<div class="hint">Eşleştirme kodunu <b>RestoMenum eklentisinden</b> alın — 10 dakika geçerli.</div>
<label for="kod">Eşleştirme kodu</label>
<input id="kod" autocomplete="off" placeholder="örn. 8F3K-2QZ7">
<button id="kaydol" onclick="kaydol()">Kaydol</button>
<div id="kodMsg" class="msg"></div>
""");
        sb.Append("</div>");

        // ── 2. TERMİNAL (tarama + seçim + elle) ──────────────────────────────────────
        sb.Append("<div class=\"card\"><h2><span class=\"n\">2</span>Terminal adresi</h2>");
        sb.Append("<div class=\"hint\">Ağı tarayıp terminalinizi listeden seçin ya da IP'yi elle girin.</div>");
        sb.Append("<label for=\"ip\">Terminal (IP)</label><div class=\"row\"><div class=\"grow\">");
        sb.Append("<select id=\"ip\" onchange=\"ipSecim()\">");
        if (!string.IsNullOrEmpty(d.TerminalIp))
            sb.Append($"<option value=\"{E(d.TerminalIp)}\" selected>{E(d.TerminalIp)} (mevcut)</option>");
        else
            sb.Append("<option value=\"\" selected disabled>— tarayın ya da elle girin —</option>");
        sb.Append("<option value=\"__manuel__\">Elle gir…</option>");
        sb.Append("</select></div>");
        sb.Append("<button class=\"ghost\" id=\"tara\" onclick=\"tara()\">⟲ Tara</button></div>");
        sb.Append("<input id=\"ipManuel\" hidden autocomplete=\"off\" placeholder=\"192.168.1.189\" style=\"margin-top:10px\">");
        sb.Append($"<label for=\"port\">Port</label><input id=\"port\" value=\"{d.TerminalPort ?? 7500}\">");
        sb.Append("<button onclick=\"terminalKaydet()\">Kaydet</button><div id=\"ipMsg\" class=\"msg\"></div>");
        sb.Append("<div class=\"hint\">IP'yi değiştirirseniz ajan kendini yeniden başlatır (birkaç saniye).</div></div>");

        // ── 3. EŞLEŞTİRME ────────────────────────────────────────────────────────────
        sb.Append("<div class=\"card\"><h2><span class=\"n\">3</span>Terminalle eşleştir</h2>");
        sb.Append("<div class=\"hint\">Terminalde <b>Menü → GMP Eşleştirme</b> ekranını açın (şifre gerekebilir), ekran açıkken düğmeye basın.</div>");
        sb.Append("<button id=\"esles\" onclick=\"esles()\">Eşleştir</button><div id=\"esMsg\" class=\"msg\"></div></div>");

        // ── 4. EKLENTİ ───────────────────────────────────────────────────────────────
        sb.Append("<div class=\"card\"><h2><span class=\"n\">4</span>Eklenti bağlantısı</h2>");
        sb.Append("<div class=\"hint\">RestoMenum panelinde <b>Eklenti Ayarları → Cihaz kurulumu</b> sekmesinde " +
                  "<b>Kurulum sırrı üret</b> deyin, çıkan dizeyi buraya yapıştırın. Yeni sır üretmek eskisini geçersiz kılar; " +
                  "o zaman yeni dizeyi yine buraya yapıştırın.</div>");
        sb.Append("<label for=\"dize\">Kurulum dizesi</label><textarea id=\"dize\" autocomplete=\"off\" spellcheck=\"false\" placeholder=\"eyJ1cmwiOi…\"></textarea>");
        sb.Append("<button id=\"eklenti\" onclick=\"eklentiKaydet()\">Doğrula ve kaydet</button><div id=\"dizeMsg\" class=\"msg\"></div>");
        sb.Append("<div class=\"hint\">Dize önce eklentiyle canlı doğrulanır; geçerliyse kaydedilir ve ajan yeniden başlar.</div></div>");

        // ── 5. KALDIRMA (yalnız kurulu bir şey varken) ───────────────────────────────
        if (d.Kayitli || d.EklentiAdresi is not null)
        {
            sb.Append("<div class=\"card danger\"><h2><span class=\"n\">!</span>Cihazı kaldır</h2>");
            sb.Append("<div class=\"hint\">Platform kaydı ve eklenti bağlantısı bu bilgisayardan <b>silinir</b>; ajan " +
                      "kurulmamış duruma döner. Yeniden kurmak için eklentiden <b>yeni bir eşleştirme kodu</b> ve " +
                      "<b>kurulum dizesi</b> gerekir. Terminal adresi ve cihaz anahtarı korunur. Satış sürerken yapılamaz.</div>");
            sb.Append("<label for=\"kaldirOnay\">Onaylamak için KALDIR yazın</label>");
            sb.Append("<input id=\"kaldirOnay\" autocomplete=\"off\" placeholder=\"KALDIR\">");
            sb.Append("<button class=\"tehlike\" id=\"kaldir\" onclick=\"kaldir()\">Cihazı kaldır</button><div id=\"kaldirMsg\" class=\"msg\"></div></div>");
        }

        sb.Append("<div class=\"foot\">RESTOMENUM AGENT · LOCAL SETUP CONSOLE</div>");

        // ── JS ───────────────────────────────────────────────────────────────────────
        // Anahtar onaltılık (KurulumGuvenlik.YeniAnahtar); yine de kaçırılır — tırnak kıramasın.
        sb.Append($"</div><script>\nconst ANAHTAR='{System.Web.HttpUtility.JavaScriptStringEncode(anahtar)}';");
        sb.Append("""

async function post(yol, govde){
  const r = await fetch(yol,{method:'POST',headers:{'Content-Type':'application/json','X-Kurulum-Anahtari':ANAHTAR},body:JSON.stringify(govde)});
  let j; try{ j = await r.json(); }catch{ j = {ok:false,message:'sunucu yanıtı okunamadı ('+r.status+')'}; }
  return j;
}
function goster(id, j){ const el=document.getElementById(id); el.textContent=j.message||''; el.className='msg '+(j.ok?'ok':'hata'); }
function bilgi(id, t){ const el=document.getElementById(id); el.textContent=t; el.className='msg info'; }

async function kaydol(){
  const b=document.getElementById('kaydol'); b.disabled=true;
  const j=await post('/setup/enroll',{code:document.getElementById('kod').value});
  goster('kodMsg', j); b.disabled=false;
  if(j.ok){ document.getElementById('kod').value=''; setTimeout(()=>location.reload(),1500); }
}

function ipSecim(){
  const sel=document.getElementById('ip'), man=document.getElementById('ipManuel');
  const elle = sel.value==='__manuel__';
  man.hidden = !elle;
  if(elle){ man.focus(); }
}
function ipDegeri(){
  const sel=document.getElementById('ip');
  if(sel.value==='__manuel__') return document.getElementById('ipManuel').value.trim();
  return sel.value;
}
async function tara(){
  const b=document.getElementById('tara'); b.disabled=true; b.textContent='⟳ Taranıyor…';
  bilgi('ipMsg','Ağ taranıyor, birkaç saniye…');
  try{
    const r=await fetch('/setup/scan'); const j=await r.json();
    const sel=document.getElementById('ip'), onceki=ipDegeri();
    sel.innerHTML='';
    (j.found||[]).forEach(f=>{
      const o=document.createElement('option'); o.value=f.ip; o.textContent=f.ip+':'+f.port; sel.appendChild(o);
    });
    const m=document.createElement('option'); m.value='__manuel__'; m.textContent='Elle gir…'; sel.appendChild(m);
    if(onceki && onceki!=='__manuel__' && ![...sel.options].some(o=>o.value===onceki)){
      const o=document.createElement('option'); o.value=onceki; o.textContent=onceki+' (mevcut)'; sel.insertBefore(o,sel.firstChild);
    }
    if(onceki) sel.value=onceki;
    document.getElementById('ipManuel').hidden = sel.value!=='__manuel__';
    const n=j.count||0;
    bilgi('ipMsg', n? (n+' cihaz bulundu ('+(j.subnet||'')+') — birini seçin') : 'Cihaz bulunamadı ('+(j.subnet||'ağ')+'). IP'+"'"+'yi elle girebilirsiniz.');
  }catch(e){ const el=document.getElementById('ipMsg'); el.textContent='Tarama başarısız: '+e; el.className='msg hata'; }
  b.disabled=false; b.textContent='⟲ Tara';
}
async function terminalKaydet(){
  const ip=ipDegeri();
  if(!ip){ const el=document.getElementById('ipMsg'); el.textContent='Önce bir IP seçin ya da girin.'; el.className='msg hata'; return; }
  const j=await post('/setup/terminal',{ip:ip,port:parseInt(document.getElementById('port').value||'7500',10)});
  goster('ipMsg', j);
  if(j.ok && j.restartRequired){
    document.getElementById('ipMsg').textContent=j.message+' Ajan yeniden başlıyor…';
    document.getElementById('ipMsg').className='msg info';
    setTimeout(async function bekle(){ try{ await fetch('/setup/status'); location.reload(); }catch{ setTimeout(bekle,1000); } }, 3000);
  }
}
async function esles(){
  const b=document.getElementById('esles'); b.disabled=true;
  bilgi('esMsg','Eşleştiriliyor… (terminal menüde olmalı)');
  const j=await post('/setup/pair',{});
  goster('esMsg', j); b.disabled=false;
  if(j.ok) setTimeout(()=>location.reload(),1500);
}
async function eklentiKaydet(){
  const b=document.getElementById('eklenti'); b.disabled=true;
  bilgi('dizeMsg','Eklentiyle doğrulanıyor…');
  const j=await post('/setup/plugin',{setup:document.getElementById('dize').value});
  goster('dizeMsg', j); b.disabled=false;
  if(j.ok && j.restartRequired){
    document.getElementById('dize').value='';
    document.getElementById('dizeMsg').textContent=j.message+' Ajan yeniden başlıyor…';
    document.getElementById('dizeMsg').className='msg info';
    setTimeout(async function bekle(){ try{ await fetch('/setup/status'); location.reload(); }catch{ setTimeout(bekle,1000); } }, 3000);
  }
}
async function kaldir(){
  const b=document.getElementById('kaldir'); b.disabled=true;
  const j=await post('/setup/remove',{confirm:document.getElementById('kaldirOnay').value});
  goster('kaldirMsg', j); b.disabled=false;
  if(j.ok && j.restartRequired){
    document.getElementById('kaldirMsg').textContent=j.message+' Ajan yeniden başlıyor…';
    document.getElementById('kaldirMsg').className='msg info';
    setTimeout(async function bekle(){ try{ await fetch('/setup/status'); location.reload(); }catch{ setTimeout(bekle,1000); } }, 3000);
  }
}
</script></body></html>
""");
        return sb.ToString();
    }
}
