namespace Restomenum.Agent.Core;

/// <summary>
/// <b>W130 — TEKRAR ANAHTARLARI İÇİN TEK POLİTİKA.</b>
///
/// <para><b>Neden var:</b> anahtar sınırları ayrıştırıcıdan ayrıştırıcıya farklıydı ve bu
/// ölçüldü (2026-09-10): <c>ticketFinishId</c> 64'te kesiliyordu, <c>ticketCancelId</c> ise
/// <b>hiç sınırlanmıyordu</b>. Aynı kasadan gelen, aynı şekilde üretilmiş, aynı 66 karakterlik
/// iki anahtardan biri reddedildi, öteki geçti. Kimse bunu tasarlamamıştı — iki ayrı yerde
/// iki ayrı gün yazılmıştı.</para>
///
/// <para><b>Neden sınır GEREKİYOR:</b> bu anahtarlar depoda birincil anahtar oluyor
/// (<c>event_id</c>, komut kimliği). Sınırsız dış girdiyi anahtar yapmak, boyutu karşı tarafın
/// insafına bırakmaktır.</para>
///
/// <para><b>Neden 128:</b> sahada ölçülen en uzun gerçek anahtar 66 karakter
/// (<c>&lt;uuid&gt;-&lt;terminalId&gt;-ticketcancel</c>). 128 onun iki katına yakın — taşmayı
/// engeller, makul anahtarı kesmez. 64 tam da bunu yapıyordu ve <b>gerçek bir isteği
/// reddetti</b>.</para>
///
/// <para>⚠️ Bu sabit <b>iptal yoluna YENİ bir red ekliyor</b> — orada eskiden hiç sınır yoktu.
/// Bilinçli: sınır ölçülenin iki katı, ve red artık SEBEBİYLE loglanıyor. Para yolunda sessiz
/// bir red bırakmaktansa görünür ve cömert bir sınır tercih edildi.</para>
/// </summary>
public static class TekrarAnahtari
{
    /// <summary>Tekrar anahtarının en fazla uzunluğu — bkz. <see cref="TekrarAnahtari"/>.</summary>
    public const int EnCok = 128;

    /// <summary>
    /// Anahtarı doğrular. <c>null</c> = geçerli; dolu dönerse <b>red sebebi</b>.
    /// Sebep AYRIŞIR: "yok", "boş" ve "çok uzun" aynı şey değildir — 2026-09-10'da aynı mesajı
    /// paylaşmaları karşı tarafa kör arama yaptırdı.
    /// </summary>
    public static string? Dogrula(string? anahtar, string ad) => anahtar switch
    {
        null => $"{ad} YOK (SaleToPOIRequest.Restomenum içinde bekleniyor)",
        _ when string.IsNullOrWhiteSpace(anahtar) => $"{ad} BOŞ",
        _ when anahtar.Length > EnCok => $"{ad} ÇOK UZUN ({anahtar.Length} > {EnCok})",
        _ => null,
    };
}
