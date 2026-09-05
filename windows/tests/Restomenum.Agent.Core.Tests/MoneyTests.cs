using System.Globalization;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Para birimi sınır dönüşümü — kesme YASAK, yuvarlama ZORUNLU, çarpan <c>exponent</c>'ten (K-21).
/// InlineData STRING olarak geçilir ve <c>decimal.Parse</c> ile ayrıştırılır: değeri double geçmek
/// testin kendisini 99.98999… tuzağına düşürürdü.
/// </summary>
public class MoneyTests
{
    [Theory]
    // exp=2 — peer'in "kesmede 1 kuruş kaybettiren" ölçtüğü değerler dahil.
    [InlineData("99.99", 2, 9999)]
    [InlineData("240", 2, 24000)]
    [InlineData("8.29", 2, 829)]
    [InlineData("0.29", 2, 29)]
    [InlineData("16.08", 2, 1608)]
    // exp=0 — kuruşsuz para birimi: çarpan 1, 100 DEĞİL (sabit-100 hatası burada 100× yanlış olurdu).
    [InlineData("240", 0, 240)]
    [InlineData("1500", 0, 1500)]
    // exp=3 — üç basamaklı minor.
    [InlineData("1.234", 3, 1234)]
    public void ToMinor_exponente_gore_yuvarlar(string wire, int exponent, long beklenen)
    {
        var deger = decimal.Parse(wire, CultureInfo.InvariantCulture);
        Assert.Equal(beklenen, Money.ToMinor(deger, exponent));
    }

    [Theory]
    [InlineData(9999, 2, "99.99")]
    [InlineData(24000, 2, "240")]
    [InlineData(240, 0, "240")]
    [InlineData(1234, 3, "1.234")]
    public void ToWire_exponente_gore(long minor, int exponent, string beklenen)
        => Assert.Equal(decimal.Parse(beklenen, CultureInfo.InvariantCulture), Money.ToWire(minor, exponent));

    [Fact]
    public void RoundTrip_kayipsiz()
    {
        foreach (var (m, e) in new[] { (9999, 2), (24000, 2), (829, 2), (240, 0), (1234, 3) })
            Assert.Equal(m, Money.ToMinor(Money.ToWire(m, e), e));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void Gecersiz_exponent_reddedilir(int exponent)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Money.ToMinor(1m, exponent));
}
