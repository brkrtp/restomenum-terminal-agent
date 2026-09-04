namespace Restomenum.Agent.Core;

/// <summary>
/// Sunucu saat offseti (§5.3) — <b>expiresAt kontrolü cihaz saatine bırakılamaz.</b>
///
/// Restoran kasalarında duvar saati yanlış olabilir, elle değiştirilebilir veya NTP hiç
/// yapılandırılmamış olabilir. Cihaz saatine güvenip süresi geçmiş bir komutu çalıştırmak, kasiyerin
/// çoktan vazgeçtiği bir tahsilatı terminale düşürmek demektir; tersi de mümkündür — geçerli bir
/// komut "süresi geçti" diye reddedilir.
///
/// Offset <b>monotonic</b> saat üzerinden tutulur: duvar saati değişse bile (kullanıcı saati elle
/// değiştirdi, NTP sıçradı) geçen süre ölçümü bozulmaz.
///
/// Offset <b>bilinmiyorsa tahmin YOKTUR</b> — <see cref="IsExpired"/> <c>null</c> döner ve çağıran
/// <c>CLOCK_UNSYNCED</c> ile reddeder.
/// </summary>
public sealed class ClockOffset
{
    private const long ValidityMs = 10 * 60_000;

    private readonly Func<long> _wallClock;
    private readonly Func<long> _monotonic;
    private long? _offsetMs;
    private long _syncedAtMonotonic;

    public ClockOffset(Func<long>? wallClock = null, Func<long>? monotonic = null)
    {
        _wallClock = wallClock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _monotonic = monotonic ?? (() => Environment.TickCount64);
    }

    /// <param name="serverTimeMs">Sunucunun bildirdiği an (hello.ok / heartbeat).</param>
    /// <param name="requestStartMonotonic">İsteği gönderirken alınan monotonic okuma.</param>
    public void Sync(long serverTimeMs, long? requestStartMonotonic = null)
    {
        var start = requestStartMonotonic ?? _monotonic();
        // Gidiş-dönüşün yarısı kadar düzeltme: sunucu zamanı yolda geçen sürenin ortasında geçerliydi.
        var roundTrip = _monotonic() - start;
        var estimatedServerNow = serverTimeMs + roundTrip / 2;
        _offsetMs = estimatedServerNow - _wallClock();
        _syncedAtMonotonic = _monotonic();
    }

    public bool IsSynced =>
        _offsetMs.HasValue && (_monotonic() - _syncedAtMonotonic) <= ValidityMs;

    /// <exception cref="InvalidOperationException">Offset bilinmiyorsa — TAHMİN YOK (§5.3).</exception>
    public long ServerNow()
    {
        if (!IsSynced) throw new InvalidOperationException("CLOCK_UNSYNCED");
        return _wallClock() + _offsetMs!.Value;
    }

    /// <returns><c>null</c> offset bilinmiyorsa — karar VERİLEMEZ.</returns>
    public bool? IsExpired(long expiresAtMs) => IsSynced ? ServerNow() >= expiresAtMs : null;

    public long? Offset => IsSynced ? _offsetMs : null;
}
