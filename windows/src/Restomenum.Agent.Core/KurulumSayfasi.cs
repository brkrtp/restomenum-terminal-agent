using System.Net;
using System.Text;

namespace Restomenum.Agent.Core;

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
            sb.Append("<div class=\"hint\">Bu cihaz kayıtlı. Yeniden kaydetmek gerekmiyor.</div><div id=\"kodMsg\" class=\"msg\"></div>");
        else
            sb.Append("""
<div class="hint">RestoMenum eklentisinde <b>Bu bilgisayarda kur</b> derseniz kod buraya kendiliğinden gelir; ya da
kodu eklentiden alıp yazın — 10 dakika geçerli.</div>
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

// Tek tık kurulum: eklenti /setup#kod=… açar. Kod fragment'ta — sunucuya, günlüğe, Referer'a gitmez.
// Yalnız alana YERLEŞİR; gönderen kullanıcının tıklamasıdır (127.0.0.1'i her site açtırabilir).
function kodHashten(h){
  const m=/^#(?:.*&)?kod=([^&]*)/.exec(h||'');
  if(!m) return null;
  let k; try{ k=decodeURIComponent(m[1]).trim(); }catch{ return null; }
  return /^[A-Za-z0-9-]{4,64}$/.test(k) ? k : null;
}
function hashtenKodYerlestir(){
  if(!location.hash) return;
  const k=kodHashten(location.hash);
  history.replaceState(null,'',location.pathname+location.search);
  const alan=document.getElementById('kod');
  if(!k){ if(alan) bilgi('kodMsg','Bağlantıdaki kod okunamadı; kodu eklentiden alıp elle yazın.'); return; }
  if(!alan){ bilgi('kodMsg','Bu cihaz zaten kayıtlı; eklentiden gelen kod kullanılmadı.'); return; }
  alan.value=k;
  bilgi('kodMsg',"Kod eklentiden geldi. Bu bilgisayarı kaydetmek için Kaydol'a basın.");
  document.getElementById('kaydol').focus();
}
hashtenKodYerlestir();

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
