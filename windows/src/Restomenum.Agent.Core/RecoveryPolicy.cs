namespace Restomenum.Agent.Core;

/// <summary>
/// Terminal RECV_BUSY döndüğünde — "sana şu an cevap veremiyorum".
///
/// <b>Bu "hiçbir şey olmadı" DEMEK DEĞİLDİR.</b> Sahada ölçülen iki vakada RECV_BUSY tam olarak
/// kart ödemesi terminalde <b>BAŞARIYLA TAMAMLANDIKTAN</b> sonra geldi; terminal parayı almış,
/// işlemi bitirmekle meşguldü. RECV_BUSY'yi "güvenli, tekrar gönder" sayan bir agent tam da bu
/// anda ikinci kez tahsilat yapar.
/// </summary>
public sealed class TerminalBusyException : Exception
{
    public TerminalBusyException(string message = "RECV_BUSY — terminal meşgul, cevap veremiyor")
        : base(message) { }
}

/// <summary>
/// Belirsizlik sonrası terminali ne zaman sorgulayacağımız — <b>saha ölçümü</b>, tahmin değil.
///
/// <para>İki gerçek timeout vakasından (GMPDLL_2026_04_17_103039.TXT) çıkan desen aynı:</para>
/// <code>
///   Payment timeout                     : 90.17 s / 90.16 s  (GMP.XML CommTimeOut=90000)
///   HEMEN sorgu → RECV_BUSY             :  6.74 s /  6.36 s  ← ilk sorgu HER ZAMAN başarısız
///   Başarılı sorguya kadar ek bekleme   : 26.3 s / 25.3 s
///   ReloadTransaction (OptionFlags+Get) :  1.10 s /  1.38 s
/// </code>
///
/// <para><b>Kural:</b> timeout'tan hemen sonra sorgulama. İlk sorgu iki vakada da RECV_BUSY aldı ve
/// 6.5 saniyeyi boşa harcadı; terminal kart işlemini bitirmek için ~25–30 sn daha meşgul kalıyor.
/// Bu yüzden ilk sorgu <see cref="InitialDelay"/> kadar geciktirilir.</para>
///
/// <para><b>Neden sorgulamayı hiç bırakmıyoruz:</b> alternatif "ödemeyi tekrar gönder" olurdu ve
/// sahada kanıtlandığı üzere para çoktan hareket etmiş olabilir. Beklemek yavaştır; tekrar
/// göndermek müşterinin parasına mal olur.</para>
/// </summary>
public sealed class RecoveryPolicy
{
    /// <summary>İlk sorgudan önceki bekleme. Ölçüm: ~25–30 sn meşguliyet.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>RECV_BUSY sonrası tekrarlar arası bekleme (üstel, <see cref="MaxDelay"/> ile sınırlı).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Toplam sorgu denemesi. Ölçülen kurtarma ~56 sn sürdü; 6 deneme (30 + 5 + 10 + 20 + 20 + 20 ≈ 105 sn)
    /// buna rahat yer bırakır. Tükendiğinde <b>tahmin edilmez</b> — belirsiz kabul edilip insana gider.
    /// </summary>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>Testte gerçek zaman beklememek için değiştirilebilir.</summary>
    public Func<TimeSpan, CancellationToken, Task> Sleep { get; init; } = Task.Delay;

    /// <summary>N'inci denemeden (0 tabanlı) önceki bekleme.</summary>
    public TimeSpan DelayFor(int attempt)
    {
        if (attempt <= 0) return InitialDelay;
        var ms = RetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelay.TotalMilliseconds));
    }

    /// <summary>Testte anında çalışan politika — bekleme yok, davranış aynı.</summary>
    public static RecoveryPolicy Immediate => new()
    {
        InitialDelay = TimeSpan.Zero,
        RetryDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero,
        Sleep = (_, _) => Task.CompletedTask,
    };
}
