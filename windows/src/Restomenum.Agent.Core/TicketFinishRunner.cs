namespace Restomenum.Agent.Core;

/// <summary>
/// <see cref="TicketFinish.Planla"/>'nın kararını cihazda UYGULAR.
///
/// <para><b>Neden Core'da, Host'ta değil:</b> burada sıra ve durdurma kuralları var (bir adım
/// patlarsa sonrakine geçilmez, <c>Close</c> yine de denenmez). Bunlar Host'ta yazılsaydı
/// testlenemezdi — <c>Restomenum.Agent.Host.Windows</c> yalnız Windows'ta derlenir ve gerçek
/// terminal ister. Core'daki sahte sarmalayıcıyla mutasyon testi yazılabiliyor.</para>
///
/// <para><b>Sözü:</b> bu tür <c>FP3_VoidAll</c> ya da <c>FP3_VoidPayment</c> ÇAĞIRMAZ — ne bu
/// dosyada ne de çağırdığı yerde geçer. İşlediği fişlerde tanım gereği TAHSİLAT vardır; orada
/// ters işlem müşterinin parasını geri vermek olurdu.</para>
/// </summary>
public static class TicketFinishRunner
{
    /// <param name="Kapandi">Fiş cihazda GERÇEKTEN kapandı mı (<c>FP3_Close</c> rc=0).</param>
    /// <param name="Plan">Uygulanan karar — <c>Dokunma</c> ise cihaza HİÇ gidilmedi.</param>
    /// <param name="Bayraklar">Karara temel olan HAM <c>TransactionFlags</c>.</param>
    /// <param name="BasarisizAdim">Zincir yarıda kaldıysa çağrının adı; yoksa <c>null</c>.</param>
    /// <param name="Kod">O adımın ham dönüş kodu; yoksa <c>null</c>.</param>
    /// <param name="ToplamMinor">Kapatmadan HEMEN ÖNCE okunan fiş toplamı; okunamadıysa <c>null</c>.</param>
    /// <param name="TahsilMinor">Kapatmadan HEMEN ÖNCE okunan tahsilat; okunamadıysa <c>null</c>.</param>
    public readonly record struct Sonuc(
        bool Kapandi, FisBitirmePlani Plan, uint Bayraklar,
        string? BasarisizAdim = null, string? Kod = null,
        long? ToplamMinor = null, long? TahsilMinor = null);

    /// <summary>
    /// Fişi OKUR, planı çıkarır ve — <paramref name="uygula"/> ise — uygular.
    ///
    /// <para><paramref name="uygula"/> <c>false</c>: <b>KURU PROVA</b>. Fiş okunur (bu da bir cihaz
    /// çağrısıdır ama fişi DEĞİŞTİRMEZ), plan hesaplanır, hiçbir baskı/kapatma yapılmaz. Ölçmek
    /// için değiştirmek zorunda kalmamak şart — para taşıyan bir cihazda "ne yapacağını görmek"
    /// tek başına bir yetenektir.</para>
    /// </summary>
    public static Sonuc Calistir(IGmpWrapper gmp, ulong handle, bool uygula,
        Action<string, object?>? log = null)
    {
        bool Yaz(string m, object? v = null) { log?.Invoke(m, v); return true; }

        // `OptionFlags(Reload)` ŞART: tek başına `GetTicket` ödeme detayını eksik döndürebiliyor
        // (ölçüldü) ve karar tam o alanlara dayanıyor. Üreticinin akışında da adım 2 budur.
        var of = gmp.OptionFlags(handle, GmpEchoFlags.Reload);
        var gt = gmp.GetTicket(handle, out var fis);
        if (!of.Ok || !gt.Ok)
        {
            Yaz("[bitir] fiş OKUNAMADI — dokunulmadı", new { of = of.ToString(), gt = gt.ToString() });
            return new Sonuc(false,
                new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "TICKET_READ_FAILED", 0), 0,
                "GetTicket", $"{of}/{gt}");
        }

        var plan = TicketFinish.Planla(fis);
        Yaz("[bitir] fiş okundu", new
        {
            toplam = fis.TotalAmountMinor, tahsil = fis.PaidAmountMinor,
            odemeSayisi = fis.PaymentCount, kalan = fis.RemainingMinor,
            bayraklar = TicketFlags.Yazdirilabilir(fis.TransactionFlags),
            plan = plan.Adim.ToString(), neden = plan.Neden, ureticiAdimi = plan.UreticiAdimi,
        });
        // CİHAZIN KÂĞIDA BASTIĞI SATIRLAR (W73 yankısı). Bayrak "basılmadı" derken kâğıtta
        // toplam satırı VARSA, bayrak ile fiziksel baskı ayrışmış demektir ve tam zinciri
        // koşmak toplamları İKİNCİ kez basar. Bu ayrımı ancak bu satırlar gösterir.
        _ = fis.PrinterLines is { } yz
            ? Yaz("[bitir] cihazın bastığı satırlar", new
              { adet = yz.Count, cihazBildirdi = fis.PrinterLineCount,
                satirlar = PrinterCopy.Yazdirilabilir(yz) })
            : Yaz("[bitir] yazıcı yankısı GELMEDİ (null) — kâğıtta ne olduğu bilinmiyor", null);

        if (plan.Adim == FisBitirmeAdimi.Dokunma || !uygula)
            return new Sonuc(false, plan, fis.TransactionFlags,
                ToplamMinor: fis.TotalAmountMinor, TahsilMinor: fis.PaidAmountMinor);

        // Plana göre çağrı listesi. `VoidAll`/`VoidPayment` hiçbirinde yok.
        var adimlar = new List<(string Ad, Func<GmpResult> Cagri)>();
        if (plan.Adim == FisBitirmeAdimi.TumBaskiVeKapat)
        {
            adimlar.Add(("PrintTotalsAndPayments", () => gmp.PrintTotalsAndPayments(handle)));
            adimlar.Add(("PrintBeforeMF", () => gmp.PrintBeforeMF(handle)));
        }
        if (plan.Adim is FisBitirmeAdimi.MfVeKapat or FisBitirmeAdimi.TumBaskiVeKapat)
            adimlar.Add(("PrintMF", () => gmp.PrintMF(handle)));

        foreach (var (ad, cagri) in adimlar)
        {
            var r = cagri();
            if (r.Ok) { Yaz("[bitir] adım tamam", new { ad }); continue; }
            // ⚠️ SONRAKİ ADIMA GEÇİLMEZ ve `Close` DENENMEZ. Basılamamış bir fişi kapatmak,
            // eksik bir mali belgeyi kesinleştirmek olurdu; fiş açık kalırsa tekrar denenebilir.
            Yaz("[bitir] adım BAŞARISIZ — zincir durdu, fiş AÇIK bırakıldı",
                new { ad, code = r.ToString() });
            return new Sonuc(false, plan, fis.TransactionFlags, ad, r.ToString(),
                fis.TotalAmountMinor, fis.PaidAmountMinor);
        }

        var kapanis = gmp.Close(handle);
        if (!kapanis.Ok)
        {
            Yaz("[bitir] Close BAŞARISIZ — fiş AÇIK kaldı", new { code = kapanis.ToString() });
            return new Sonuc(false, plan, fis.TransactionFlags, "Close", kapanis.ToString(),
                fis.TotalAmountMinor, fis.PaidAmountMinor);
        }

        Yaz("[bitir] ✓ FİŞ KAPANDI", new { ureticiAdimi = plan.UreticiAdimi });
        return new Sonuc(true, plan, fis.TransactionFlags,
            ToplamMinor: fis.TotalAmountMinor, TahsilMinor: fis.PaidAmountMinor);
    }
}
