using Microsoft.Extensions.Logging;

namespace Restomenum.Agent.Host;

/// <summary>
/// Ajanın kendi kayıtlarını <b>diske</b> yazan sağlayıcı.
///
/// <para><b>Neden var:</b> ajan bugüne kadar yalnız konsola yazıyordu ve üretimde bir konsol
/// penceresinde çalışıyor. Bu, saha teşhisini iki kez fiilen engelledi (2026-09-07): bir ödeme
/// reddinin sebebini gösteren satır — hangi <c>saleSessionId</c>'nin geldiği — yalnız o pencerede
/// vardı, pencere kapanınca gitti. Cihazın kendi izi (<c>GMPDLL_*.TXT</c>) ve <c>agent.db</c> ne
/// KARAR verdiğimizi değil, ne YAPTIĞIMIZI gösteriyor; aradaki "neden" kayboluyordu.</para>
///
/// <para><b>Sessiz kalmayı seçtiği tek yer kendi hatalarıdır:</b> disk dolu/kilitli olabilir ve bir
/// log hatası ödeme akışını ASLA bozmamalı. Bu yüzden her yazma try/catch içinde ve istisna
/// yutuluyor — burada "sağlam olmak" = "görünmez olmak".</para>
///
/// <para>Kart verisi yazılmaz: kaynaktaki kayıtlar zaten maskeli/referans alanları taşıyor
/// (§12.3); bu sınıf yalnız verilen metni yazar, zenginleştirme yapmaz.</para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dizin;
    private readonly object _kilit = new();
    private readonly long _azamiBoyut;
    private readonly int _saklamaGun;
    private DateOnly _gun;
    private StreamWriter? _yazici;

    /// <param name="dizin">Kayıt dizini — <c>agent.db</c> ile aynı yer (%ProgramData%).</param>
    /// <param name="azamiBoyutBayt">Bir dosya bu boyutu aşınca sıradaki parçaya geçilir.</param>
    /// <param name="saklamaGun">Bu günden eski dosyalar silinir.</param>
    public FileLoggerProvider(string dizin, long azamiBoyutBayt = 20L * 1024 * 1024, int saklamaGun = 14)
    {
        _dizin = dizin;
        _azamiBoyut = azamiBoyutBayt;
        _saklamaGun = saklamaGun;
        try { Directory.CreateDirectory(_dizin); } catch { /* log hatası akışı bozmaz */ }
        Temizle();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Yaz(string satir)
    {
        try
        {
            lock (_kilit)
            {
                var bugun = DateOnly.FromDateTime(DateTime.Now);
                if (_yazici is null || bugun != _gun || Tasti())
                {
                    _yazici?.Dispose();
                    _gun = bugun;
                    _yazici = new StreamWriter(Yol(bugun), append: true) { AutoFlush = true };
                }
                _yazici.WriteLine(satir);
            }
        }
        catch { /* disk dolu/kilitli: ödeme akışı log yüzünden durmaz */ }
    }

    private bool Tasti()
    {
        try { return _yazici?.BaseStream.Length >= _azamiBoyut; }
        catch { return false; }
    }

    /// <summary>Aynı gün içinde boyut aşılırsa sıradaki boş parça adı.</summary>
    private string Yol(DateOnly gun)
    {
        var taban = Path.Combine(_dizin, $"agent-{gun:yyyyMMdd}");
        for (var i = 0; i < 1000; i++)
        {
            var y = i == 0 ? taban + ".log" : $"{taban}.{i}.log";
            try { if (!File.Exists(y) || new FileInfo(y).Length < _azamiBoyut) return y; }
            catch { return y; }
        }
        return taban + ".log";
    }

    private void Temizle()
    {
        try
        {
            var sinir = DateTime.Now.AddDays(-_saklamaGun);
            foreach (var f in Directory.EnumerateFiles(_dizin, "agent-*.log"))
                try { if (File.GetLastWriteTime(f) < sinir) File.Delete(f); } catch { /* kilitli */ }
        }
        catch { /* dizin yok/erişilemez */ }
    }

    public void Dispose()
    {
        lock (_kilit) { _yazici?.Dispose(); _yazici = null; }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _ad;

        public FileLogger(FileLoggerProvider p, string ad) { _p = p; _ad = ad; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Seviye süzgeci `ILoggerFactory` katmanında (appsettings `Logging:LogLevel`); burada
        // ikinci bir eşik koymak, yapılandırmayı iki yerden yönetmek olurdu.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var metin = formatter(state, exception);
            var satir = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Kisa(logLevel)} [{_ad}] {metin}";
            if (exception is not null) satir += Environment.NewLine + exception;
            _p.Yaz(satir);
        }

        private static string Kisa(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
            LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
