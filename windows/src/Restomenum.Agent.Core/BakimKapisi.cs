namespace Restomenum.Agent.Core;

/// <summary>
/// <b>RM-0389 — bakım kapısı.</b> Kurulum sayfasının ajanı yeniden başlatan ya da kimliğini söken
/// işlemleri (terminal adresi, eklenti dizesi, cihazı kaldır) terminale, deftere ya da platforma
/// dokunan bir işin ORTASINA denk gelmemeli.
///
/// <para><b>Kim girer:</b> satış/iptal/fiş tamamlama istekleri (yanıt + bildirim bitene kadar) ve
/// arka plan işleri — açılış/periyodik kurtarma, bildirim kuyruğunun boşaltılması, cihaz eşleme
/// yoklaması. Hepsi <see cref="Gir"/> ile girer; <c>null</c> dönerse iş o tur YAPILMAZ.</para>
///
/// <para><b>Bakım:</b> <see cref="BakimaAlAsync"/> kapıyı ATOMİK kapatır — o andan sonra hiçbir iş
/// giremez — ve içerideki son işin çıkmasını bekler. Süre dolar ya da iptal edilirse kapı geri açılır
/// (bitmeyen bir iş ajanı sonsuza kadar satışa kapalı bırakmamalı). Dönen sahiplik bırakılınca kapı
/// AÇILIR; yalnız <see cref="BakimSahipligi.KapaliTut"/> denmişse yeniden başlatmaya kadar kapalı
/// kalır. Böylece bakımı açan her yol — hata, istisna, erken dönüş — kapıyı kendiliğinden bırakır.</para>
/// </summary>
public sealed class BakimKapisi
{
    private readonly object _g = new();
    private int _icerde;
    private bool _bakim;
    private TaskCompletionSource? _bosaldi;

    public bool Bakimda { get { lock (_g) return _bakim; } }

    /// <summary>İş girişi. <c>null</c> = bakımda, iş YAPILMAMALI. Dönen nesne bırakılınca çıkış (bir kez).</summary>
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
    /// Kapıyı kapatır ve içerideki işlerin bitmesini bekler. <c>null</c> = süre doldu, iptal edildi ya
    /// da zaten bakımda (kapı eski hâlinde).
    /// </summary>
    public async Task<BakimSahipligi?> BakimaAlAsync(TimeSpan bekleme, CancellationToken ct = default)
    {
        Task bekle;
        lock (_g)
        {
            if (_bakim) return null;
            _bakim = true;
            if (_icerde == 0) return new BakimSahipligi(this);
            _bosaldi = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bekle = _bosaldi.Task;
        }

        var bitti = await Task.WhenAny(bekle, Task.Delay(bekleme, ct)) == bekle;
        if (bitti) return new BakimSahipligi(this);
        Ac();
        return null;
    }

    private void Ac()
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

    /// <summary>Bakımın sahibi. Bırakılınca kapı açılır — <see cref="KapaliTut"/> denmedikçe.</summary>
    public sealed class BakimSahipligi : IDisposable
    {
        private readonly BakimKapisi _k;
        private int _bitti;
        private volatile bool _kapaliTut;

        internal BakimSahipligi(BakimKapisi k) => _k = k;

        /// <summary>Değişiklik yapıldı ve yeniden başlatma bekleniyor: kapı süreç bitene kadar kapalı kalır.</summary>
        public void KapaliTut() => _kapaliTut = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _bitti, 1) != 0) return;
            if (!_kapaliTut) _k.Ac();
        }

        /// <summary>
        /// <see cref="KapaliTut"/> denmiş olsa bile kapıyı açar: beklenen yeniden başlatma
        /// GERÇEKLEŞMEDİ ve süreç çalışmaya devam ediyor — sonsuza kadar satışa kapalı kalmamalı.
        /// </summary>
        public void KapiyiAc() => _k.Ac();
    }
}
