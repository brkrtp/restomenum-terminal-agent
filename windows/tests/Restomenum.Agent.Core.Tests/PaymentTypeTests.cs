using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W54 — beş yeni ödeme tipi kabul ediliyor, dördü BİLİNÇLİ olarak edilmiyor.
///
/// <para>Sayılar DLL'in kendi sabitleri (<c>GmpInterop.cs:869-884</c>): platform op sözlüğü
/// bunları kullanıyor, uydurma değil.</para>
/// </summary>
public class PaymentTypeTests
{
    [Theory]
    [InlineData(1)]      // PAYMENT_CASH_TL
    [InlineData(4)]      // PAYMENT_BANK_CARD
    [InlineData(16)]     // PAYMENT_MOBILE
    [InlineData(8)]      // PAYMENT_YEMEKCEKI      ← W54
    [InlineData(32)]     // PAYMENT_HEDIYE_CEKI    ← W54
    [InlineData(512)]    // PAYMENT_PUAN           ← W54
    [InlineData(2048)]   // PAYMENT_BANKA_TRANSFERI ← W54
    [InlineData(4096)]   // PAYMENT_CEK            ← W54
    public void Kabul_edilen_tipler(int tip) => Assert.True(GmpPaymentTypes.IsKnown(tip));

    [Theory]
    [InlineData(2)]      // PAYMENT_CASH_CURRENCY — DÖVİZ
    [InlineData(64)]     // PAYMENT_IKRAM
    [InlineData(128)]    // PAYMENT_ODEMESIZ
    [InlineData(256)]    // PAYMENT_KAPORA
    [InlineData(8192)]   // PAYMENT_ACIK_HESAP
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    public void REDDEDILEN_tipler(int tip) => Assert.False(GmpPaymentTypes.IsKnown(tip));

    [Fact]
    public void DOVIZ_bilincli_olarak_DISARIDA()
    {
        // ← ÇİVİ: ajan her tutarı TL gönderiyor (`CurrencyTl = 949` sabit; kur profili
        // `IGmpWrapper`'a bağlı DEĞİL). Dövizi kabul etmek, dövizli bir ödemeyi TL tutarıyla
        // deftere yazmak olurdu — geri alınamaz yanlış mali kayıt. Kur desteği gelmeden
        // bu satır DEĞİŞMEMELİ.
        Assert.False(GmpPaymentTypes.IsKnown(2));
    }

    [Fact]
    public void Sabitler_DLL_degerleriyle_BIREBIR()
    {
        // ← ÇİVİ: bu sayılar cihazın `ST_PAYMENT.typeOfPayment` alanına AYNEN gidiyor. Biri
        // kayarsa ödeme yanlış tipte kaydedilir ve iptal yolu da yanlış seçilir (kart↔nakit
        // karışması bu projede bir kez yaşandı: "4, 2 DEĞİL").
        Assert.Equal(1, GmpPaymentTypes.Cash);
        Assert.Equal(4, GmpPaymentTypes.Card);
        Assert.Equal(16, GmpPaymentTypes.Mobile);
        Assert.Equal(8, GmpPaymentTypes.MealVoucher);
        Assert.Equal(32, GmpPaymentTypes.GiftVoucher);
        Assert.Equal(512, GmpPaymentTypes.Points);
        Assert.Equal(2048, GmpPaymentTypes.BankTransfer);
        Assert.Equal(4096, GmpPaymentTypes.Cheque);
    }
}
