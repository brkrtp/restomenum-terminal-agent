namespace Restomenum.Agent.Gmp;

public sealed partial class GmpWrapper
{
    /// <summary>Cihaz metin alanı (rrn, onay kodu): boş/boşluk → null, değilse kırpılmış — tek biçim (RM-0396).</summary>
    private static string? Deger(string? s) => Bos(s) ? null : s!.Trim();
}
