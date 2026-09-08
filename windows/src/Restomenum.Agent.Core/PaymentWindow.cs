namespace Restomenum.Agent.Core;

/// <summary>
/// Cihazın ödeme dizisinde <b>gerçekten dolu</b> olan kayıtların aralığı.
///
/// <para><b>Neden ayrı ve saf:</b> bu aritmetik 2026-09-08'de bir kez ısırdı ve sarmalayıcının
/// içinde olduğu için test edilemiyordu. DLL aynı fişi iki farklı biçimde döndürebiliyor ve
/// ikisi de gerçek — ölçüldü:</para>
/// <list type="bullet">
///   <item><b>07.09 18:00:</b> dizi <c>count</c> uzunluğunda (3), dolu kayıt <c>inThis</c> (1),
///   dolu olan SONDAKİ (index 2); ilk ikisi tamamen sıfır.</item>
///   <item><b>08.09 13:02:</b> dizi <c>inThis</c> uzunluğunda (1), <c>count</c> ise 2; dolu kayıt
///   BAŞTAKİ (index 0).</item>
/// </list>
///
/// <para>Konumu varsaymak yerine <b>elimizdeki dizinin son <c>inThis</c> kaydı</b> alınıyor; bu
/// tek ifade iki biçimi de karşılıyor. Varsayımın bedeli ölçüldü: eski kural ikinci biçimde
/// diziyi "bozuk okuma" sayıp fişteki banka bacağını düşürüyordu, yani başarısız bir kart
/// denemesinde hangi bankaya gidildiği bildirilemiyordu.</para>
/// </summary>
public static class PaymentWindow
{
    /// <summary>Dizinin fiziksel kapasitesi (<c>ST_TICKET.stPayment</c>). Sayaç bunu aşarsa okuma BOZUKTUR.</summary>
    public const int Capacity = 24;

    /// <summary>
    /// Dolu kayıtların yarı-açık aralığı <c>[Start, End)</c>. Boş aralık = iddia edilecek kayıt yok.
    /// </summary>
    /// <param name="totalCount">Fişteki TOPLAM ödeme sayısı (<c>totalNumberOfPayments</c>).</param>
    /// <param name="inThisCount">Bu yanıtta DOLU gelen kayıt sayısı (<c>numberOfPaymentsInThis</c>).</param>
    /// <param name="arrayLength">Elimize ulaşan dizinin gerçek uzunluğu.</param>
    public static (int Start, int End) Range(int totalCount, int inThisCount, int arrayLength)
    {
        if (totalCount <= 0 || arrayLength <= 0) return (0, 0);

        // Tutarsız sayaç → hiçbir şey iddia etme. `inThis > total` olamaz; olduysa okuma güvenilmez.
        if (inThisCount < 0 || inThisCount > totalCount) return (0, 0);

        var son = System.Math.Min(arrayLength, totalCount);
        var basla = System.Math.Max(0, son - inThisCount);
        return (basla, son);
    }
}
