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
