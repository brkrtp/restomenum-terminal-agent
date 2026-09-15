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

    /// <summary>Ajanı yeniden başlatır (IP ya da eklenti değişikliğinden sonra).</summary>
    void YenidenBaslat();
}

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
                port = p.GetInt32();
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

    private static string? Alan(string govde, string ad, out string? neden)
    {
        neden = null;
        try
        {
            using var doc = JsonDocument.Parse(govde);
            if (!doc.RootElement.TryGetProperty(ad, out var v) || v.ValueKind != JsonValueKind.String)
            { neden = $"'{ad}' alanı yok"; return null; }
            var s = v.GetString()?.Trim();
            if (string.IsNullOrEmpty(s)) { neden = $"'{ad}' boş"; return null; }
            return s;
        }
        catch (JsonException) { neden = "gövde JSON değil"; return null; }
    }

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
    public static string Html(KurulumDurumu d)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder(8192);
        sb.Append("""
<!doctype html><html lang="tr"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Yazarkasa Kurulumu</title>
<style>
 body{font-family:system-ui,Segoe UI,sans-serif;max-width:640px;margin:32px auto;padding:0 16px;color:#222;background:#fafafa}
 h1{font-size:20px;margin:0 0 4px} .alt{color:#666;font-size:13px;margin-bottom:20px}
 .kart{background:#fff;border:1px solid #ddd;border-radius:8px;padding:16px;margin-bottom:14px}
 .kart h2{font-size:15px;margin:0 0 10px} .durum{font-weight:600}
 .ok{color:#1a7f37} .uyari{color:#b45309} .hata{color:#b91c1c}
 label{display:block;font-size:13px;color:#444;margin:8px 0 3px}
 input,textarea{width:100%;box-sizing:border-box;padding:8px;font-size:15px;border:1px solid #bbb;border-radius:6px;font-family:inherit}
 textarea{font-family:ui-monospace,Consolas,monospace;font-size:12px;min-height:72px}
 button{margin-top:10px;padding:9px 16px;font-size:14px;border:0;border-radius:6px;background:#1d4ed8;color:#fff;cursor:pointer}
 button:disabled{background:#999} .msg{margin-top:8px;font-size:13px;min-height:18px}
 .kucuk{font-size:12px;color:#777;margin-top:6px} code{background:#eee;padding:1px 4px;border-radius:3px}
 .sayac{display:inline-block;margin-right:14px}
</style></head><body>
<h1>Yazarkasa Kurulumu</h1>
<div class="alt">RestoMenum ajanı · yalnız bu bilgisayardan erişilir</div>
""");

        // ── DURUM ──────────────────────────────────────────────────────────────────
        sb.Append("<div class=\"kart\"><h2>Durum</h2>");
        sb.Append(d.Kayitli
            ? "<div class=\"durum ok\">✓ Platforma kayıtlı</div>"
            : "<div class=\"durum uyari\">⚠ Platforma kayıtlı değil — 1. adımı tamamlayın</div>");
        sb.Append(d.Esli
            ? $"<div class=\"durum ok\">✓ Terminal eşli{(d.CihazModeli is null ? "" : $": {E(d.CihazModeli)}")}{(d.CihazSeri is null ? "" : $" · seri {E(d.CihazSeri)}")}</div>"
            : "<div class=\"durum uyari\">⚠ Terminal eşleşmemiş — 2. ve 3. adımı tamamlayın</div>");
        sb.Append(d.EklentiAdresi is not null
            ? $"<div class=\"durum ok\">✓ Eklenti bağlı: {E(d.EklentiAdresi)}</div>"
            : "<div class=\"durum uyari\">⚠ Eklenti bağlantısı yok — 4. adımı tamamlayın</div>");
        if (d.ZNo is not null || d.FisNo is not null)
            sb.Append($"<div class=\"kucuk\"><span class=\"sayac\">Z raporu: {d.ZNo}</span><span class=\"sayac\">Sıradaki fiş: {d.FisNo}</span>" +
                      (d.KagitUyarisi == true ? "<span class=\"sayac hata\">KÂĞIT BİTTİ</span>" : "") + "</div>");
        if (d.Not is not null) sb.Append($"<div class=\"msg hata\">{E(d.Not)}</div>");
        sb.Append("</div>");

        // ── 1. KAYIT ───────────────────────────────────────────────────────────────
        sb.Append("<div class=\"kart\"><h2>1. Platform kaydı</h2>");
        if (d.Kayitli)
            sb.Append("<div class=\"kucuk\">Bu cihaz kayıtlı. Yeniden kaydetmek gerekmiyor.</div>");
        else
            sb.Append("""
<label for="kod">Eşleştirme kodu <span class="kucuk">(RestoMenum eklentisinden alın — 10 dakika geçerli)</span></label>
<input id="kod" autocomplete="off" placeholder="örn. 8F3K-2QZ7">
<button id="kaydol" onclick="kaydol()">Kaydol</button>
<div id="kodMsg" class="msg"></div>
""");
        sb.Append("</div>");

        // ── 2. TERMİNAL ────────────────────────────────────────────────────────────
        sb.Append("<div class=\"kart\"><h2>2. Terminal adresi</h2>");
        sb.Append($"<label for=\"ip\">Terminalin IP adresi</label><input id=\"ip\" value=\"{E(d.TerminalIp)}\" placeholder=\"192.168.1.189\">");
        sb.Append($"<label for=\"port\">Port</label><input id=\"port\" value=\"{d.TerminalPort ?? 7500}\">");
        sb.Append("<button onclick=\"terminalKaydet()\">Kaydet</button><div id=\"ipMsg\" class=\"msg\"></div>");
        sb.Append("<div class=\"kucuk\">IP'yi değiştirirseniz ajan kendini yeniden başlatır (birkaç saniye).</div></div>");

        // ── 3. EŞLEŞTİRME ──────────────────────────────────────────────────────────
        sb.Append("<div class=\"kart\"><h2>3. Terminalle eşleştir</h2>");
        sb.Append("<div class=\"kucuk\">Terminalde <b>Menü → GMP Eşleştirme</b> ekranını açın (şifre gerekebilir), ekran açıkken aşağıdaki düğmeye basın.</div>");
        sb.Append("<button id=\"esles\" onclick=\"esles()\">Eşleştir</button><div id=\"esMsg\" class=\"msg\"></div></div>");

        // ── 4. EKLENTİ ─────────────────────────────────────────────────────────────
        // Alan her durumda görünür: panelde "Kurulum sırrı üret" her basışta yeni sır üretir ve
        // eskisini geçersiz kılar — bağlıyken de yeni dize yapıştırmak gerekebilir.
        sb.Append("<div class=\"kart\"><h2>4. Eklenti bağlantısı</h2>");
        sb.Append("<div class=\"kucuk\">RestoMenum panelinde <b>Eklenti Ayarları → Cihaz kurulumu</b> sekmesinde bu cihaz için " +
                  "<b>Kurulum sırrı üret</b> deyin, çıkan dizeyi kopyalayıp buraya yapıştırın. " +
                  "Yeni sır üretmek eskisini geçersiz kılar; o zaman yeni dizeyi yine buraya yapıştırın.</div>");
        sb.Append("<label for=\"dize\">Kurulum dizesi</label><textarea id=\"dize\" autocomplete=\"off\" spellcheck=\"false\" placeholder=\"eyJ1cmwiOi…\"></textarea>");
        sb.Append("<button id=\"eklenti\" onclick=\"eklentiKaydet()\">Doğrula ve kaydet</button><div id=\"dizeMsg\" class=\"msg\"></div>");
        sb.Append("<div class=\"kucuk\">Dize önce eklentiyle canlı doğrulanır; geçerliyse kaydedilir ve ajan kendini yeniden başlatır.</div></div>");

        // ── JS ─────────────────────────────────────────────────────────────────────
        sb.Append("""
<script>
async function post(yol, govde){
  const r = await fetch(yol,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(govde)});
  let j; try{ j = await r.json(); }catch{ j = {ok:false,message:'sunucu yanıtı okunamadı ('+r.status+')'}; }
  return j;
}
function goster(id, j){ const el=document.getElementById(id); el.textContent=j.message||''; el.className='msg '+(j.ok?'ok':'hata'); }
async function kaydol(){
  const b=document.getElementById('kaydol'); b.disabled=true;
  const j=await post('/setup/enroll',{code:document.getElementById('kod').value});
  goster('kodMsg', j); b.disabled=false;
  if(j.ok){ document.getElementById('kod').value=''; setTimeout(()=>location.reload(),1500); }
}
async function terminalKaydet(){
  const j=await post('/setup/terminal',{ip:document.getElementById('ip').value,port:parseInt(document.getElementById('port').value||'7500',10)});
  goster('ipMsg', j);
  if(j.ok && j.restartRequired){
    document.getElementById('ipMsg').textContent=j.message+' Ajan yeniden başlıyor…';
    setTimeout(async function bekle(){ try{ await fetch('/setup/status'); location.reload(); }catch{ setTimeout(bekle,1000); } }, 3000);
  }
}
async function eklentiKaydet(){
  const b=document.getElementById('eklenti'); b.disabled=true;
  document.getElementById('dizeMsg').textContent='Eklentiyle doğrulanıyor…';
  const j=await post('/setup/plugin',{setup:document.getElementById('dize').value});
  goster('dizeMsg', j); b.disabled=false;
  if(j.ok && j.restartRequired){
    document.getElementById('dize').value='';
    document.getElementById('dizeMsg').textContent=j.message+' Ajan yeniden başlıyor…';
    setTimeout(async function bekle(){ try{ await fetch('/setup/status'); location.reload(); }catch{ setTimeout(bekle,1000); } }, 3000);
  }
}
async function esles(){
  const b=document.getElementById('esles'); b.disabled=true;
  document.getElementById('esMsg').textContent='Eşleştiriliyor… (terminal menüde olmalı)';
  const j=await post('/setup/pair',{});
  goster('esMsg', j); b.disabled=false;
  if(j.ok) setTimeout(()=>location.reload(),1500);
}
</script></body></html>
""");
        return sb.ToString();
    }
}
