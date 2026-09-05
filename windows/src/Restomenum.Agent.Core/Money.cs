namespace Restomenum.Agent.Core;

/// <summary>
/// Para birimi SINIR dönüşümü — <b>TEK yardımcı</b> (yerel sözleşme "Para birimi kuralı", K-21).
///
/// <para>Tel üzerinde tutarlar nexo uyumlu <b>ONDALIK</b> (240, 99.99). İçeride her hesap
/// <b>TAM SAYI minor unit</b> kalır. Dönüşüm YALNIZ burada ve <b>YUVARLAMA</b> ile yapılır.</para>
///
/// <para><b>Çarpan SABİT DEĞİL — <c>exponent</c>'ten türetilir</b> (<c>10^exponent</c>). Platform
/// <c>exponent</c> 0–3 kabul ediyor ve EU pazarını destekliyor; <c>100</c>'ü sabit yazmak
/// <c>exponent=0</c> bir para biriminde tutarı 100 kat yanlış hesaplardı. Değer GET yanıtının
/// <c>RestomenumExt.Exponent</c>'inden okunur — para biriminden TAHMİN edilmez.</para>
///
/// <para><b>Kesme (truncate) YASAK.</b> Tel değeri <c>decimal</c> okunur (JSON <c>GetDecimal()</c>,
/// <c>double</c> DEĞİL — <c>99.99</c> bir double'da <c>99.98999…</c>'dur) ve
/// <see cref="decimal.Round(decimal,int,MidpointRounding)"/> ile yuvarlanır.</para>
/// </summary>
public static class Money
{
    /// <summary>Tel ondalığını tam sayı minor unit'e çevirir (yuvarlayarak). Örn. exp=2: 99.99→9999; exp=0: 240→240.</summary>
    public static long ToMinor(decimal wireAmount, int exponent) =>
        (long)decimal.Round(wireAmount * Pow10(exponent), 0, MidpointRounding.AwayFromZero);

    /// <summary>Tam sayı minor unit'i tel ondalığına çevirir. Örn. exp=2: 9999→99.99; exp=0: 240→240.</summary>
    public static decimal ToWire(long minor, int exponent) => minor / Pow10(exponent);

    /// <summary><c>10^exponent</c> (decimal). Platform aralığı 0–3; güvenlik payıyla 0–4 kabul.</summary>
    private static decimal Pow10(int exponent)
    {
        if (exponent is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(exponent), exponent, "exponent 0–4 aralığında olmalı");
        decimal p = 1m;
        for (var i = 0; i < exponent; i++) p *= 10m;
        return p;
    }
}
