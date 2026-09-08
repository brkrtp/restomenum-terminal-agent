using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W55 — beyan edilen yetenekler. <b>Beyan, kasiyere "bu düğme çalışır" sözü vermektir</b>; kasa ve
/// panel düğmeleri bu listeye bakarak çıkıyor. Çalışmayan bir düğme, olmayan düğmeden kötüdür:
/// müşteri karşısında denenir ve kasa manuel ödemeye düşer.
///
/// <para>Bu yüzden buradaki testler "kod doğru mu"dan çok <b>"her satırın ölçülmüş bir dayanağı var
/// mı"</b> sorusunu koruyor.</para>
/// </summary>
public class AgentCapabilitiesTests
{
    [Fact]
    public void OLCULEN_dort_tip_beyanda()
    {
        // ← ÇİVİ: 2026-09-09 01:23'te `--capability-probe` bu dördünü tek fiş üzerinde denedi ve
        // DÖRDÜ DE rc=0 döndü; fişin tahsilat sayacı 1→2→3→4 yükseldi (yani cihaz kabul etmekle
        // kalmadı, gerçek ödeme satırı yazdı). Ölçüm var, beyan da olmalı.
        Assert.Contains("sale.giftVoucher", AgentCapabilities.Declared);
        Assert.Contains("sale.points", AgentCapabilities.Declared);
        Assert.Contains("sale.bankTransfer", AgentCapabilities.Declared);
        Assert.Contains("sale.cheque", AgentCapabilities.Declared);
    }

    [Fact]
    public void UYGULAMA_GEREKTIREN_tipler_beyanda_DEGIL()
    {
        // ← ÇİVİ: DLL'in kendi tablosunda YEMEKCEKI (8) ve MOBILE (16) yanında "(Uygulama varsa)"
        // yazıyor ve bu terminalde öyle bir uygulama YOK — `--payment-apps` dört kayıt döndürdü,
        // DÖRDÜ DE banka kartı (Garanti, Ziraat, İş Bankası, Akbank). Bunları beyan etmek, kasiyere
        // basınca çalışmayacak bir düğme göstermek olur.
        //
        // Cihaza böyle bir uygulama kurulursa yol şudur: ÖNCE `--capability-probe` ile ölç, SONRA
        // buraya ekle. Bu testi "artık gerekmiyor" diye silmek, tam da kaçındığımız sırayı tersine
        // çevirmek olur.
        Assert.DoesNotContain("sale.mealVoucher", AgentCapabilities.Declared);
        Assert.DoesNotContain("sale.mobileWallet", AgentCapabilities.Declared);
    }

    [Fact]
    public void DOVIZ_beyanda_DEGIL()
    {
        // ← ÇİVİ: ajan her tutarı TL gönderiyor (`GmpWrapper.CurrencyTl = 949` sabit; kur profili
        // `IGmpWrapper`'a bağlı DEĞİL). Dövizi beyan etmek, dövizli bir ödemeyi TL tutarıyla deftere
        // yazdırmak olurdu — geri alınamaz yanlış mali kayıt.
        Assert.DoesNotContain("sale.foreignCurrency", AgentCapabilities.Declared);
    }

    [Fact]
    public void Beyan_edilen_her_SATIS_opunun_odeme_tipi_KABUL_ediliyor()
    {
        // ← ÇİVİ: beyan ile satış yolu ayrışırsa beyan yalan olur — panel düğmeyi gösterir, kasiyer
        // basar, ajan `IsKnown` kapısında reddeder. Bu test ikisini tek doğruluk kaynağına bağlar.
        var opTipi = new Dictionary<string, int>
        {
            ["sale.cash"] = GmpPaymentTypes.Cash,
            ["sale.card"] = GmpPaymentTypes.Card,
            ["sale.giftVoucher"] = GmpPaymentTypes.GiftVoucher,
            ["sale.points"] = GmpPaymentTypes.Points,
            ["sale.bankTransfer"] = GmpPaymentTypes.BankTransfer,
            ["sale.cheque"] = GmpPaymentTypes.Cheque,
        };

        foreach (var op in AgentCapabilities.Declared.Where(o => o.StartsWith("sale.", StringComparison.Ordinal)))
        {
            Assert.True(opTipi.ContainsKey(op), $"'{op}' beyan ediliyor ama karşılığı olan ödeme tipi bilinmiyor");
            Assert.True(GmpPaymentTypes.IsKnown(opTipi[op]), $"'{op}' beyan ediliyor ama satış yolu tipi REDDEDİYOR");
        }
    }

    [Fact]
    public void Yoklamanin_KABUL_ettigi_her_tip_beyanda()
    {
        // ← ÇİVİ: ters yön. `--capability-probe` bir tipi ölçüp KABUL dediyse ve beyanda yoksa, o
        // düğme panelde hiç çıkmaz — ölçtüğümüz bir yeteneği sahada kullanmıyor oluruz. Yoklamanın
        // aday listesi büyüdüğünde bu test "beyanı da güncelle" diye hatırlatır.
        foreach (var (_, op, ad) in CapabilityProbe.Candidates)
            Assert.True(AgentCapabilities.Declared.Contains(op),
                $"{ad} yoklamada ölçülüyor ama beyanda yok: '{op}'");
    }

    [Fact]
    public void Beyan_TEKRARSIZ()
    {
        // Aynı op iki kez giderse platform tarafında sayım/karşılaştırma bozulur; ucuz bir kapı.
        Assert.Equal(AgentCapabilities.Declared.Length, AgentCapabilities.Declared.Distinct().Count());
    }
}
