namespace Restomenum.Agent.Core;

/// <summary>
/// Yeniden bağlanma gecikmesi (§5.2) — **jitter ZORUNLUDUR**.
///
/// Bir gateway instance'ı kapandığında ona bağlı tüm agent'lar aynı anda kopar. Sabit gecikmeyle
/// hepsi aynı saniyede geri dönerse **yeniden bağlanma fırtınası** olur: 10.000 cihazda ~33
/// bağlantı/sn, 50.000'de ~166. Jitter bu dalgayı yayar.
///
/// Jitter **çift yönlüdür** (±%20). Yalnız pozitif jitter dalgayı yaymak yerine öteler.
/// </summary>
public sealed class Backoff
{
    private static readonly long[] StepsMs = { 500, 1_000, 2_000, 5_000, 10_000, 30_000 };
    private readonly Func<double, double> _random;
    private int _attempt;

    /// <param name="random">[0,x) aralığında değer üretir. Testte sabitlenebilir.</param>
    public Backoff(Func<double, double>? random = null) =>
        _random = random ?? (x => Random.Shared.NextDouble() * x);

    /// <summary>Başarılı bağlantıdan sonra çağrılır — sonraki kopma en kısa gecikmeyle başlasın.</summary>
    public void Reset() => _attempt = 0;

    public long Next()
    {
        var baseMs = StepsMs[Math.Min(_attempt, StepsMs.Length - 1)];
        _attempt++;
        var spread = baseMs * 0.2;
        var delta = _random(2 * spread) - spread;
        return Math.Max(1, (long)(baseMs + delta));
    }

    public int AttemptCount => _attempt;
}
