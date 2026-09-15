using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Restomenum.Agent.Core;
using Restomenum.Agent.Host;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>W149 — kurulum sayfasının cihaza/dosyaya/ağa dokunan tarafı.</b> Sayfanın kendisi ve
/// ayrıştırma Core'da (<see cref="KurulumSayfasi"/>, <see cref="KurulumIstek"/>); burası yalnız
/// gerçek işlemleri bağlar.
///
/// <para><b>Cihaz kilidi:</b> eşleştirme ve durum okuması cihaza gider; satış sürüyorsa
/// <see cref="IDeviceIdleGate.TryAcquire"/> <c>null</c> döner ve BEKLENMEZ — "satış sürüyor, sonra
/// deneyin" denir. Satış her zaman öncelikli.</para>
/// </summary>
public sealed class WindowsKurulumIslemleri : IKurulumIslemleri
{
    private readonly IServiceProvider _services;
    private readonly ILogger _log;
    private readonly IGmpWrapper _gmp;
    private readonly IDeviceKey _key;
    private readonly IDeviceIdleGate _kilit;
    private readonly DeviceIdentityHolder? _kimlik;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly AgentOptions _opt;

    public WindowsKurulumIslemleri(IServiceProvider services, ILoggerFactory logs, IGmpWrapper gmp,
        IDeviceKey key, IDeviceIdleGate kilit, IHostApplicationLifetime lifetime,
        IOptions<AgentOptions> opt, DeviceIdentityHolder? kimlik = null)
    {
        _services = services; _log = logs.CreateLogger("Kurulum"); _gmp = gmp; _key = key;
        _kilit = kilit; _kimlik = kimlik; _lifetime = lifetime; _opt = opt.Value;
    }

    public KurulumDurumu Durum()
    {
        var kayitli = !string.IsNullOrEmpty(_key.ConnectorId);
        var (ip, port) = GmpXmlAyar.Oku(GmpXmlYolu, _log);
        var (bilgi, _) = _kimlik?.Oku() ?? (null, null);

        bool esli = false; uint? zNo = null, fisNo = null; string? not = null;
        using (var k = _kilit.TryAcquire())
        {
            if (k is null)
            {
                not = "Satış sürüyor — cihaz durumu şu an okunamadı, sayfayı biraz sonra yenileyin.";
            }
            else
            {
                // Eşleşme bu SÜREÇTE tutulur (ölçüldü): sorusu cihaza gider ama fiş açmaz.
                if (_gmp.CheckPairing(out var p).Ok) esli = p;
                // Sayaçlar bilgi amaçlı; okunamazsa sayfa onları göstermez (uydurmaz).
                if (_gmp.Echo(out var d).Ok && (d.ZNo > 0 || d.FisNo > 0)) { zNo = d.ZNo; fisNo = d.FisNo; }
            }
        }

        return new KurulumDurumu(kayitli, esli, ip, port, bilgi is null ? null : $"{bilgi.Brand.Trim()} {bilgi.Model.Trim()}",
            bilgi?.Serial.Trim(), zNo, fisNo, KagitUyarisi: null, Not: not);
    }

    public Task<KurulumSonucu> KaydolAsync(string kod, CancellationToken ct)
        => WindowsEnrollment.KaydolAsync(_services, kod, ct);   // kod loglanMAZ

    public KurulumSonucu TerminalAyarla(string ip, int port)
    {
        var (eskiIp, eskiPort) = GmpXmlAyar.Oku(GmpXmlYolu, _log);
        if (eskiIp == ip && eskiPort == port)
            return KurulumSonucu.Tamam("Terminal adresi zaten bu. Değişiklik yok.");

        var hata = GmpXmlAyar.Yaz(GmpXmlYolu, ip, port, _log);
        if (hata is not null) return KurulumSonucu.Hata(hata);

        // ⚠️ DLL, GMP.XML'i yalnız AÇILIŞTA okur (ölçüldü: yapılandırma yankısı açılış izinde
        // bir kez). Yeni IP ancak yeniden başlatınca etkili. Sayfa bunu bilerek "yeniden
        // başlıyor" der ve bekler.
        _log.LogInformation("terminal adresi yazıldı: {Ip}:{Port} (eski {EskiIp}:{EskiPort}) — yeniden başlatma gerekli",
            ip, port, eskiIp, eskiPort);
        return KurulumSonucu.Tamam($"Terminal adresi {ip}:{port} olarak kaydedildi.", yenidenBaslat: true);
    }

    public KurulumSonucu Esles()
    {
        using var k = _kilit.TryAcquire();
        if (k is null) return KurulumSonucu.Hata("Şu an bir satış sürüyor. Satış bitince tekrar deneyin.");

        var ok = WindowsPairing.Run(_services);
        if (!ok)
            return KurulumSonucu.Hata(
                "Eşleşme başarısız. Terminalin açık ve ağda olduğundan (2. adımdaki IP), ve terminalde " +
                "GMP Eşleştirme ekranının AÇIK olduğundan emin olup tekrar deneyin.");

        var (bilgi, _) = _kimlik?.Oku() ?? (null, null);
        var cihaz = bilgi is null ? "" : $" {bilgi.Brand.Trim()} {bilgi.Model.Trim()} · seri {bilgi.Serial.Trim()}";
        return KurulumSonucu.Tamam($"Eşleşme başarılı.{cihaz}");
    }

    public void YenidenBaslat()
    {
        // Kendini aynı argümanlarla yeniden başlat, sonra dur. Yeni süreç dinleyici portunu eski
        // süreç bırakana kadar alamaz — AgentWorker bağlanmayı kısa aralıklarla yeniden dener.
        var exe = Environment.ProcessPath;
        if (exe is null) { _log.LogError("yeniden başlatma: süreç yolu bilinmiyor"); return; }
        var args = Environment.GetCommandLineArgs().Skip(1);
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _log.LogWarning("kurulum sayfası istedi: ajan yeniden başlatılıyor ({Args})", string.Join(' ', args));
        Process.Start(psi);
        _lifetime.StopApplication();
    }

    private static string GmpXmlYolu => Path.Combine(AppContext.BaseDirectory, "GMP.XML");
}

/// <summary>
/// <c>GMP.XML</c>'deki terminal adresi. <b>Bayt düzeyinde</b> tek etiket değiştirir — dosyayı
/// yeniden serileştirmez, çünkü DLL bu dosyayı kendi ayrıştırıp geri yazıyor (ölçüldü) ve
/// biçimini bozmak istemiyoruz. Yedek alır.
/// </summary>
public static class GmpXmlAyar
{
    private static readonly Regex IpRx = new(@"<IP>([^<]*)</IP>", RegexOptions.Compiled);
    private static readonly Regex PortRx = new(@"<Port>([^<]*)</Port>", RegexOptions.Compiled);

    public static (string? Ip, int? Port) Oku(string yol, ILogger? log = null)
    {
        try
        {
            var s = File.ReadAllText(yol, Encoding.UTF8);
            var ip = IpRx.Match(s) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
            int? port = PortRx.Match(s) is { Success: true } pm && int.TryParse(pm.Groups[1].Value, out var p) ? p : null;
            return (ip, port);
        }
        catch (Exception e)
        {
            log?.LogWarning("GMP.XML okunamadı: {Hata}", e.Message);
            return (null, null);
        }
    }

    /// <returns><c>null</c> = başarılı; dolu = hata mesajı.</returns>
    public static string? Yaz(string yol, string ip, int port, ILogger? log = null)
    {
        try
        {
            var b = File.ReadAllBytes(yol);
            var s = Encoding.UTF8.GetString(b);
            if (!IpRx.IsMatch(s)) return "GMP.XML içinde <IP> etiketi bulunamadı — dosya beklenen biçimde değil.";
            if (!PortRx.IsMatch(s)) return "GMP.XML içinde <Port> etiketi bulunamadı — dosya beklenen biçimde değil.";

            File.Copy(yol, yol + $".kurulum-oncesi-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
            s = IpRx.Replace(s, $"<IP>{ip}</IP>", 1);
            s = PortRx.Replace(s, $"<Port>{port}</Port>", 1);
            // BOM'u koru: dosya BOM'la başlıyorsa aynı şekilde yaz.
            var bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            File.WriteAllText(yol, s, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));
            return null;
        }
        catch (Exception e)
        {
            log?.LogError(e, "GMP.XML yazılamadı");
            return $"GMP.XML yazılamadı: {e.Message}";
        }
    }
}
