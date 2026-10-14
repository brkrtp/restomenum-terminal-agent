using System.Security.Cryptography;
using System.Text;

namespace Restomenum.Agent.Core;

/// <summary>
/// <b>RM-0389 — bakım kapısı.</b> Kurulum sayfasının ajanı yeniden başlatan ya da kimliğini söken
/// işlemleri (terminal adresi, eklenti dizesi, cihazı kaldır) bir satışın ORTASINA denk gelmemeli.
///
/// <para><b>Nasıl:</b> satış/iptal/fiş tamamlama istekleri <see cref="Gir"/> ile girer ve yanıt
/// dönene kadar (bildirim dahil — <c>HandleAsync</c> "ÖNCE bildir, SONRA dön") içeride sayılır.
/// <see cref="BakimaAlAsync"/> kapıyı ATOMİK kapatır — o andan sonra hiçbir yeni istek giremez — ve
/// içerideki son isteğin çıkmasını bekler. Bekleme süresi dolarsa kapı geri açılır: bitmeyen bir
/// satış ajanı sonsuza kadar satışa kapalı bırakmamalı.</para>
///
/// <para>Başarılı bir bakım YENİDEN BAŞLATMAYA kadar sürer (kapı bilerek açılmaz): kimliği sökülmüş
/// ya da yapılandırması değişmiş bir süreç yeni satış almamalı. Yeniden başlatma gerçekleşmezse
/// ajan satışa kapalı kalır — yanlış kimlikle satış yapmaktan iyidir.</para>
/// </summary>
public sealed class BakimKapisi
{
    private readonly object _g = new();
    private int _icerde;
    private bool _bakim;
    private TaskCompletionSource? _bosaldi;

    public bool Bakimda { get { lock (_g) return _bakim; } }

    /// <summary>İstek girişi. <c>null</c> = bakımda, istek KABUL EDİLMEMELİ. Dönen nesne bırakılınca çıkış.</summary>
    public IDisposable? Gir()
    {
        lock (_g)
        {
            if (_bakim) return null;
            _icerde++;
            return new Cikis(this);
        }
    }

    /// <summary>
    /// Kapıyı kapatır ve içeridekilerin bitmesini bekler. <c>false</c> = süre doldu (kapı geri açıldı)
    /// ya da zaten bakımda.
    /// </summary>
    public async Task<bool> BakimaAlAsync(TimeSpan bekleme, CancellationToken ct = default)
    {
        Task bekle;
        lock (_g)
        {
            if (_bakim) return false;
            _bakim = true;
            if (_icerde == 0) return true;
            _bosaldi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bekle = _bosaldi.Task;
        }

        var bitti = await Task.WhenAny(bekle, Task.Delay(bekleme, ct)) == bekle;
        if (bitti) return true;
        lock (_g) { _bakim = false; _bosaldi = null; }
        return false;
    }

    /// <summary>Bakım işlemi BAŞARISIZ olunca kapıyı açar. Başarılı işlemden sonra çağrılmaz.</summary>
    public void BakimdanCik()
    {
        lock (_g) { _bakim = false; _bosaldi = null; }
    }

    private void Cik()
    {
        lock (_g)
        {
            _icerde--;
            if (_icerde == 0) _bosaldi?.TrySetResult();
        }
    }

    private sealed class Cikis(BakimKapisi k) : IDisposable
    {
        private int _bir;
        public void Dispose() { if (Interlocked.Exchange(ref _bir, 1) == 0) k.Cik(); }
    }
}

/// <summary>
/// <b>RM-0389 — cihazı kaldırmanın dosya adımları.</b> Saf: yol ve sayıları alır, kilide ve
/// platforma dokunmaz (onlar çağıranın işi), bu yüzden her durumu testte ölçülebilir.
///
/// <para><b>Sıra ve kurtarma:</b> önce eklenti dizesi silinir, SONRA kimlik taşınır. Her adım
/// tekrarlanabilir (dosya yoksa atlanır), dolayısıyla yarım kalan bir kaldırma ayrı bir durum
/// kaydına gerek olmadan YENİDEN DENENEREK tamamlanır: eklenti silinip kimlik taşınamadıysa cihaz
/// hâlâ kayıtlı görünür, kart sayfada kalır, ikinci deneme yalnız kimliği taşır. Ters sıra (önce
/// kimlik) kayıtsız ama eklentili, satış alamayan ve sebebi görünmeyen bir ara durum üretirdi.</para>
/// </summary>
public static class CihazKaldirma
{
    public static KurulumSonucu Uygula(string connectorYolu, string? eklentiYolu,
        int bekleyenBildirim, int yarimKomut, DateTime simdi)
    {
        // Proje kuralı: uçuştaki komut asla kaybedilmez. Kimlik sökülürse bunların sonucu
        // platforma hiç bildirilemez (oturum boş kimlikle açılamaz).
        if (bekleyenBildirim > 0)
            return KurulumSonucu.Hata($"Platforma henüz bildirilmemiş {bekleyenBildirim} satış sonucu var. " +
                                      "Bildirilmeleri için internet bağlantısını kontrol edip birkaç dakika sonra tekrar deneyin.");
        if (yarimKomut > 0)
            return KurulumSonucu.Hata($"Sonucu henüz kesinleşmemiş {yarimKomut} yarım satış var. " +
                                      "Ajan bunları terminale soruyor; birkaç dakika sonra tekrar deneyin.");

        var eklentiVar = eklentiYolu is not null && File.Exists(eklentiYolu);
        var kimlikVar = File.Exists(connectorYolu);
        if (!eklentiVar && !kimlikVar)
            return KurulumSonucu.Tamam("Kaldırılacak bir kurulum yok — cihaz zaten kurulmamış durumda.");

        if (eklentiVar)
        {
            try { File.Delete(eklentiYolu!); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return KurulumSonucu.Hata("Eklenti bağlantı dosyası silinemedi; hiçbir şey değişmedi. " +
                                          "Dosya başka bir program tarafından açık olabilir ya da ajan yetkisiz çalışıyor " +
                                          "(servis olarak çalıştırın). Sonra tekrar deneyin.");
            }
        }

        if (kimlikVar)
        {
            try { File.Move(connectorYolu, connectorYolu + $".kaldirildi-{simdi:yyyyMMdd-HHmmss}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return KurulumSonucu.Hata("Eklenti bağlantısı silindi ama platform kaydı bu bilgisayardan kaldırılamadı. " +
                                          "Tekrar deneyin — işlem kaldığı yerden devam eder.");
            }
        }

        return KurulumSonucu.Tamam(
            "Cihaz kaldırıldı. Yeniden kurmak için eklentiden yeni eşleştirme kodu ve kurulum dizesi alın.",
            yenidenBaslat: true);
    }
}

/// <summary>
/// <b>RM-0389 — kurulum uçlarının kapısı.</b> Kurulum sayfası cihaz kaydını söker, terminal adresini
/// ve eklenti sırrını değiştirir. Yalnız onay sözcüğü yetmez: başka bir site tarayıcı üzerinden
/// <c>127.0.0.1</c>'e "basit istek" (text/plain gövdeli POST) gönderebilir (OWASP CSRF).
///
/// <list type="bullet">
/// <item>İstemci yerel olmalı (dinleyici başka adrese açılmış olsa bile).</item>
/// <item><c>Host</c> başlığı yerel adres olmalı — DNS yeniden bağlamayla gelen sayfalar düşer.</item>
/// <item>POST'ta gövde <c>application/json</c> olmalı: tarayıcı bunu ancak ön kontrolle (CORS)
///   gönderir ve dinleyici CORS izni vermediği için başka siteden hiç ulaşamaz.</item>
/// <item><c>Origin</c> gönderildiyse sayfanın kendi adresi olmalı.</item>
/// <item>Sayfaya gömülü, süreç başına üretilen tahmin edilemez anahtar <c>X-Kurulum-Anahtari</c>
///   başlığında gelmeli.</item>
/// </list>
/// </summary>
public static class KurulumGuvenlik
{
    public const string AnahtarBasligi = "X-Kurulum-Anahtari";

    public static string YeniAnahtar() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <returns><c>null</c> = geçer; dolu = ret sebebi (sır içermez).</returns>
    public static string? Denetle(string yontem, bool yerelIstemci, string? host, string? origin,
        string? icerikTuru, string? anahtar, string beklenenAnahtar, int port)
    {
        if (!yerelIstemci) return "kurulum yalnız bu bilgisayardan yapılabilir";

        var izinli = new[] { $"127.0.0.1:{port}", $"localhost:{port}" };
        if (host is null || !izinli.Contains(host, StringComparer.OrdinalIgnoreCase))
            return $"geçersiz Host başlığı ({host ?? "yok"})";

        if (!string.Equals(yontem, "POST", StringComparison.OrdinalIgnoreCase)) return null;

        if (icerikTuru is null || !icerikTuru.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            return "istek gövdesi JSON olmalı (application/json)";
        if (origin is not null && !izinli.Any(h => string.Equals(origin, "http://" + h, StringComparison.OrdinalIgnoreCase)))
            return $"istek başka bir kaynaktan geldi ({origin})";
        if (anahtar is null || beklenenAnahtar.Length == 0 ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(anahtar), Encoding.UTF8.GetBytes(beklenenAnahtar)))
            return "kurulum anahtarı geçersiz — sayfayı yenileyip tekrar deneyin";
        return null;
    }
}

/// <summary>
/// <b>RM-0389 — kurulum sayfası ajanı yeniden başlatabilir mi?</b> Servis olarak çalışırken yeniden
/// başlatma, sürecin çıkıp servis yöneticisinin (SCM) kurtarma eylemiyle yeniden açmasına dayanır.
/// Kurtarma eylemi tanımlı değilse servis KAPALI kalır ve sayfa sonsuza kadar "yeniden başlıyor"
/// der; bu durumda yeniden başlatma gerektiren işlem DEĞİŞİKLİK YAPMADAN reddedilir.
/// </summary>
public static class YenidenBaslatmaKarari
{
    /// <summary>Win32 <c>SC_ACTION_RESTART</c>.</summary>
    public const int ScActionRestart = 1;

    public const string KurulumKomutu =
        "sc.exe failure RestomenumAgent reset= 86400 actions= restart/5000/restart/5000/restart/30000";

    /// <param name="eylemTurleri">Servisin kurtarma eylemleri (<c>SC_ACTION.Type</c>); <c>null</c> = okunamadı.</param>
    /// <returns><c>null</c> = yeniden başlatılabilir; dolu = engel ve çözüm.</returns>
    public static string? Engel(bool servisModu, IReadOnlyList<int>? eylemTurleri)
    {
        if (!servisModu) return null;
        if (eylemTurleri is null)
            return "Servisin kurtarma ayarı okunamadı; ajan yeniden başlatılamayabilir. Yönetici olarak şunu çalıştırın: " + KurulumKomutu;
        if (eylemTurleri.Count == 0 || eylemTurleri[0] != ScActionRestart)
            return "Servisin 'hata olursa yeniden başlat' ayarı yok; kurulum sayfası ajanı yeniden başlatamaz. " +
                   "Yönetici olarak şunu çalıştırın: " + KurulumKomutu;
        return null;
    }
}

/// <summary>Komut satırı ve yapılandırmadaki göreli yollar, çalışma dizini değişmeden ÖNCE sabitlenir.</summary>
public static class Yollar
{
    public static string? Tam(string? yol, string taban) =>
        string.IsNullOrWhiteSpace(yol) ? null : Path.GetFullPath(yol, taban);
}
