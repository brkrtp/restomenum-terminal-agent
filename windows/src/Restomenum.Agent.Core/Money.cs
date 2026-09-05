namespace Restomenum.Agent.Core;

/// <summary>
/// Para birimi SINIR dönüşümü — <b>TEK yardımcı</b> (yerel sözleşme "Para birimi kuralı", K-21).
///
/// <para>Tel üzerinde tutarlar nexo uyumlu <b>ONDALIK</b> (240, 99.99). İçeride her hesap
/// <b>TAM SAYI KURUŞ</b> (minor unit) kalır: toplama, tavan karşılaştırması, defter yazımı —
/// hiçbirinde ondalık aritmetik yapılmaz. Dönüşüm YALNIZ burada ve <b>YUVARLAMA</b> ile yapılır.</para>
///
/// <para><b>Kesme (truncate) YASAK.</b> <c>99.99</c> bir <c>double</c>'da <c>99.98999…</c> olarak durur;
/// kesmek 1 kuruş eksik yazar. Bu yüzden (1) tel değerini <c>decimal</c> olarak alırız — JSON sayısı
/// <c>GetDecimal()</c> ile okunmalı, <c>double</c> DEĞİL — ve (2) <see cref="decimal.Round(decimal,int,MidpointRounding)"/>
/// ile yuvarlarız. İkinci bir kopya çıkarsa zamanla ıraksar; o yüzden dönüşüm tek yerde toplanır.</para>
/// </summary>
public static class Money
{
    /// <summary>Tel ondalığını tam sayı kuruşa çevirir (yuvarlayarak). Örn. <c>99.99 → 9999</c>.</summary>
    public static long ToMinor(decimal wireAmount) =>
        (long)decimal.Round(wireAmount * 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>Tam sayı kuruşu tel ondalığına çevirir (yanıttaki AuthorizedAmount). <c>9999 → 99.99</c>.</summary>
    public static decimal ToWire(long minor) => minor / 100m;
}
