using System.Globalization;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Para birimi sınır dönüşümü — kesme YASAK, yuvarlama ZORUNLU (yerel sözleşme, K-21).
/// InlineData'yı STRING olarak geçiyoruz ve <c>decimal.Parse</c> ile ayrıştırıyoruz: değeri
/// double olarak geçmek testin kendisini 99.98999… tuzağına düşürürdü.
/// </summary>
public class MoneyTests
{
    [Theory]
    // Peer'in ölçtüğü "kesmede 1 kuruş kaybettiren" değerler dahil.
    [InlineData("99.99", 9999)]
    [InlineData("240", 24000)]
    [InlineData("8.29", 829)]
    [InlineData("4.35", 435)]
    [InlineData("1.15", 115)]
    [InlineData("19.99", 1999)]
    [InlineData("0.29", 29)]
    [InlineData("0.57", 57)]
    [InlineData("0.58", 58)]
    [InlineData("16.08", 1608)]
    public void ToMinor_yuvarlar_asla_kesmez(string wire, long beklenenKurus)
    {
        var deger = decimal.Parse(wire, CultureInfo.InvariantCulture);
        Assert.Equal(beklenenKurus, Money.ToMinor(deger));
    }

    [Theory]
    [InlineData(9999, "99.99")]
    [InlineData(24000, "240")]
    [InlineData(1, "0.01")]
    public void ToWire_kurustan_ondaliga(long minor, string beklenen)
        => Assert.Equal(decimal.Parse(beklenen, CultureInfo.InvariantCulture), Money.ToWire(minor));

    [Fact]
    public void RoundTrip_kayipsiz()
    {
        foreach (var m in new long[] { 1, 57, 829, 1608, 1999, 9999, 24000 })
            Assert.Equal(m, Money.ToMinor(Money.ToWire(m)));
    }
}
