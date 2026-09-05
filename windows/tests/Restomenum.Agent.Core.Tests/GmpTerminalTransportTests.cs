using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Türkiye/Ingenico taşıma testleri — <b>paranın fiilen geçtiği katman.</b>
///
/// Sahte sarmalayıcı yalnız dönüş kodu üretmez, <b>çağrı sırasını da kaydeder</b>: bu katmanda
/// sıra davranışın kendisidir. `PrintBeforeMF` erken çağrılırsa fiş mali hafızaya girer ve bir daha
/// iptal edilemez; yarım açılmış fiş bırakılırsa bir sonraki satış onu sessizce yok eder.
/// </summary>
public class GmpTerminalTransportTests
{
    private sealed class FakeGmp : IGmpWrapper
    {
        public List<string> Calls { get; } = new();
        /// <summary>Son gönderilen fiş tipi — canlı terminalde yanlış değer fişi hiç açtırmıyordu.</summary>
        public int LastTicketType { get; private set; } = -1;
        public Dictionary<string, uint> Codes { get; } = new();
        public GmpTicket Ticket;
        public GmpTicket AfterPayment;
        public int PrintMfFailures;

        private GmpResult Kod(string ad)
        { Calls.Add(ad); return new GmpResult(Codes.TryGetValue(ad, out var c) ? c : GmpCodes.Ok); }

        public GmpResult Start(out ulong handle) { handle = 42; return Kod("Start"); }
        public GmpResult TicketHeader(ulong h, int t) { LastTicketType = t; return Kod("TicketHeader"); }
        public GmpResult OptionFlags(ulong h, GmpEchoFlags f) => Kod("OptionFlags");
        public GmpResult ItemSale(ulong h, GmpItem i, out GmpTicket tk) { tk = Ticket; return Kod("ItemSale"); }
        public GmpResult Payment(ulong h, GmpPaymentRequest r, out GmpTicket tk)
        { var res = Kod("Payment"); tk = AfterPayment; return res; }
        public GmpResult GetTicket(ulong h, out GmpTicket tk) { var r = Kod("GetTicket"); tk = Ticket; return r; }
        public GmpResult PrintTotalsAndPayments(ulong h) => Kod("PrintTotalsAndPayments");
        public GmpResult PrintBeforeMF(ulong h) => Kod("PrintBeforeMF");
        public GmpResult PrintUserMessage(ulong h) => Kod("PrintUserMessage");
        public GmpResult PrintMF(ulong h)
        { Calls.Add("PrintMF"); return new GmpResult(PrintMfFailures-- > 0 ? 1u : GmpCodes.Ok); }
        public GmpResult VoidAll(ulong h, out GmpTicket tk) { var r = Kod("VoidAll"); tk = Ticket; return r; }
        public GmpResult VoidPayment(ulong h, int i) => Kod("VoidPayment");
        public GmpResult Close(ulong h) => Kod("Close");
        public GmpResult Echo() => Kod("Echo");
        public GmpResult Pair() => Kod("Pair");
        public GmpResult CheckPairing(out bool paired) { paired = true; return Kod("CheckPairing"); }
    }

    private sealed class Departments : IDepartmentMap
    {
        public HashSet<string> Bilinmeyen { get; } = new();
        public int? Resolve(string productId) => Bilinmeyen.Contains(productId) ? null : 1;
    }

    private static SaleRequest Req(long amount = 3000, int paymentType = GmpPaymentTypes.Card,
        IReadOnlyList<FiscalLine>? lines = null) =>
        new("c1", "p1", "t1", amount, "TRY", 2, "prov",
            lines ?? new[] { new FiscalLine("prod1", "Kahve", 1, amount, 20m) }, paymentType);

    /// <summary>Bellek-içi görüntü deposu. Gerçekte <see cref="CommandStore"/> (disk) kullanılır —
    /// süreç kart penceresinde ölürse görüntünün hayatta kalması şart.</summary>
    private sealed class FakeSnapshots : ITicketSnapshotStore
    {
        private readonly Dictionary<string, (long, long, int)> _d = new();
        public void SaveSnapshot(string commandId, long total, long paid, int count, long? now = null)
            => _d[commandId] = (total, paid, count);
        public (long TotalMinor, long PaidMinor, int PaymentCount)? ReadSnapshot(string commandId)
            => _d.TryGetValue(commandId, out var v) ? v : null;
    }

    private static (GmpTerminalTransport, FakeGmp, Departments) Kur()
    {
        var g = new FakeGmp();
        var d = new Departments();
        return (new GmpTerminalTransport(g, d, new FakeSnapshots()), g, d);
    }

    // ── FAIL-CLOSED: terminale HİÇ dokunulmaz ────────────────────────────────

    [Fact]
    public async Task KalemYoksa_terminale_HIC_dokunulmaz()
    {
        var (t, g, _) = Kur();
        var r = await t.SaleAsync(Req(lines: Array.Empty<FiscalLine>()));

        Assert.Equal(TransportOutcome.Declined, r.Outcome);
        Assert.Equal("FISCAL_LINES_REQUIRED", r.ProviderResultCode);
        // ← ÇİVİ: kalem uydurmak yanlış departmana mali kayıt yazardı ve geri alınamazdı.
        Assert.Empty(g.Calls);
    }

    [Fact]
    public async Task EslenmemisUrun_kart_cekilmeden_reddedilir()
    {
        var (t, g, d) = Kur();
        d.Bilinmeyen.Add("prod1");
        var r = await t.SaleAsync(Req());

        Assert.Equal(TransportOutcome.Declined, r.Outcome);
        Assert.StartsWith("PRODUCT_UNMAPPED", r.ProviderResultCode);
        Assert.Empty(g.Calls);
    }

    // ── MUTLU YOL ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TamOdeme_fis_basilir_ve_KAPANIR()
    {
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 3000, 1, GmpPaymentTypes.Card, "RRN1", "4242");
        var r = await t.SaleAsync(Req());

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal("RRN1", r.Rrn);
        Assert.Equal(
            new[] { "Start", "TicketHeader", "OptionFlags", "ItemSale", "GetTicket", "Payment",
                    "PrintTotalsAndPayments", "PrintBeforeMF", "PrintUserMessage", "PrintMF", "Close" },
            g.Calls);
    }

    [Fact]
    public async Task Fis_tipi_SALE_olmali_TasnifDisi_DEGIL()
    {
        // ← ÇİVİ: burada 0 (`TasnifDisi`) gönderiliyordu ve canlı terminalde fiş HİÇ AÇILAMIYORDU
        // (0x0008 EKÜ_PROBLEM). Sahte sarmalayıcı EKÜ'yü modellemediği için test yeşil yanıyordu;
        // hata yalnız gerçek donanımda görünebilirdi. Değer artık teste bağlı.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 3000, 1, GmpPaymentTypes.Card);
        await t.SaleAsync(Req());

        Assert.Equal(GmpTicketTypes.Sale, g.LastTicketType);
        Assert.Equal(1, GmpTicketTypes.Sale);
    }

    [Fact]
    public async Task KismiOdeme_fis_ACIK_birakilir()
    {
        // Türkiye: ödeme parça parça eklenir. Fişi burada kapatmak, tutar tamamlanmadan
        // kapatmak olurdu — ki yarım ödenmiş fiş KAPANAMAZ.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Cash);
        var r = await t.SaleAsync(Req(amount: 1000, paymentType: GmpPaymentTypes.Cash));

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal(1000, r.ApprovedAmountMinor);
        Assert.DoesNotContain("PrintBeforeMF", g.Calls);   // ← ÇİVİ: mali hafızaya girmedi
        Assert.DoesNotContain("Close", g.Calls);
    }

    [Fact]
    public async Task Baski_basarisiz_olsa_bile_ODEME_ALINDI()
    {
        // Baskı hatasında `Declined` dönmek, ALINMIŞ bir parayı "reddedildi" diye raporlamak olurdu.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 3000, 1, GmpPaymentTypes.Card, "RRN2");
        g.Codes["PrintTotalsAndPayments"] = 1234;
        var r = await t.SaleAsync(Req());

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal("RRN2", r.Rrn);
    }

    [Fact]
    public async Task PrintMF_uc_kez_denenir()
    {
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 3000, 1, GmpPaymentTypes.Card);
        g.PrintMfFailures = 2;
        await t.SaleAsync(Req());

        Assert.Equal(3, g.Calls.Count(c => c == "PrintMF"));
        Assert.Contains("Close", g.Calls);
    }

    // ── BELİRSİZLİK ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(GmpCodes.Timeout)]
    [InlineData(GmpCodes.Timeout2)]
    [InlineData(GmpCodes.RecvBusy)]
    public async Task OdemeYanitsizsa_UNKNOWN_ve_TEKRAR_YOK(uint kod)
    {
        // `RecvBusy` de buraya düşer: sahada tam olarak kart ödemesi TAMAMLANDIKTAN sonra geldi.
        var (t, g, _) = Kur();
        g.Codes["Payment"] = kod;
        var r = await t.SaleAsync(Req());

        Assert.Equal(TransportOutcome.Unknown, r.Outcome);
        Assert.Equal(1, g.Calls.Count(c => c == "Payment"));   // ← ÇİVİ: yeniden gönderilmedi
    }

    [Fact]
    public async Task KalemHatasinda_YARIM_FIS_BIRAKILMAZ()
    {
        // Yarım açılmış fiş bırakılırsa bir sonraki `StartTicket` onu SESSİZCE VoidAll eder;
        // o sırada üzerinde ödeme varsa para kaybolur.
        var (t, g, _) = Kur();
        g.Codes["ItemSale"] = 1234;
        await t.SaleAsync(Req());

        Assert.Contains("VoidAll", g.Calls);
        Assert.Contains("Close", g.Calls);
    }

    // ── PROBE: "benim ödemem işlendi mi" ─────────────────────────────────────

    [Fact]
    public async Task Probe_odeme_sayaci_ARTMISSA_Landed()
    {
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(3000, 0, 0, 0);              // ödeme öncesi anlık görüntü
        g.Codes["Payment"] = GmpCodes.Timeout;
        await t.SaleAsync(Req());

        g.Ticket = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card, "RRN9");
        var p = await t.ProbeAsync(Req());

        Assert.Equal(ProbeVerdict.Landed, p.Verdict);
        Assert.Equal(1000, p.ApprovedAmountMinor);
        Assert.Equal(2000, p.RemainingMinor);   // kalan var ve bu ARIZA DEĞİL
    }

    [Fact]
    public async Task Probe_sayac_AYNIYSA_NotLanded()
    {
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Cash);
        g.Codes["Payment"] = GmpCodes.Timeout;
        await t.SaleAsync(Req());

        var p = await t.ProbeAsync(Req());   // fiş değişmedi

        Assert.Equal(ProbeVerdict.NotLanded, p.Verdict);
    }

    [Fact]
    public async Task Probe_TUTAR_degil_SAYAC_bakar()
    {
        // ← ÇİVİ: 20 ₺ üzerine 20 ₺ eklenince tutar farkı iki ödemeyi ayırt EDEMEZ.
        // Sayaç 1→2 gittiği için ödeme işlendiği anlaşılır.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(4000, 2000, 1, GmpPaymentTypes.Cash);
        g.Codes["Payment"] = GmpCodes.Timeout;
        await t.SaleAsync(Req(amount: 2000, paymentType: GmpPaymentTypes.Cash));

        g.Ticket = new GmpTicket(4000, 4000, 2, GmpPaymentTypes.Cash);
        var p = await t.ProbeAsync(Req(amount: 2000, paymentType: GmpPaymentTypes.Cash));

        Assert.Equal(ProbeVerdict.Landed, p.Verdict);
        Assert.Equal(2000, p.ApprovedAmountMinor);
    }

    [Fact]
    public async Task Probe_BOZUK_sayac_Indeterminate()
    {
        // Sarmalayıcı fiş dizisinin sınırını aşan bir sayaç gördüğünde -1 bildirir. Bunu normal
        // bir sayı sansaydık karşılaştırma "sayaç arttı" der ve GERÇEKLEŞMEMİŞ bir ödeme Landed
        // sayılırdı — para hareket etmemişken tahsilat yazmak.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(3000, 0, 0, 0);
        g.Codes["Payment"] = GmpCodes.Timeout;
        await t.SaleAsync(Req());

        g.Ticket = new GmpTicket(3000, 1000, -1, GmpPaymentTypes.Card);
        var p = await t.ProbeAsync(Req());

        Assert.Equal(ProbeVerdict.Indeterminate, p.Verdict);
    }

    [Fact]
    public async Task Probe_anlik_goruntu_YOKSA_Indeterminate()
    {
        // Agent yeniden başlamış: ödeme var ama BİZİM olduğunu söyleyemeyiz. Tahmin yerine
        // belirsiz denir — "işlendi" demek yanlış onay, "işlenmedi" demek çift tahsilat olurdu.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card);
        typeof(GmpTerminalTransport).GetField("_handle",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(t, 42UL);

        var p = await t.ProbeAsync(Req());

        Assert.Equal(ProbeVerdict.Indeterminate, p.Verdict);
    }

    // ── İPTAL: kart/nakit ayrımı ─────────────────────────────────────────────

    [Fact]
    public async Task Nakit_iptali_DOGRUDAN_VoidAll()
    {
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Cash);
        await t.SaleAsync(Req(amount: 1000, paymentType: GmpPaymentTypes.Cash));
        g.Calls.Clear();

        var r = await t.VoidAsync();

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal(new[] { "VoidAll", "Close" }, g.Calls);   // canlıda ölçüldü: ~1,5-2 sn
        Assert.DoesNotContain("VoidPayment", g.Calls);
    }

    [Fact]
    public async Task Kart_iptali_2069_sonrasi_VoidPayment_ister()
    {
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card);
        await t.SaleAsync(Req(amount: 1000));
        g.Calls.Clear();
        g.Ticket = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card, "RRN5");
        g.Codes["VoidAll"] = GmpCodes.PaymentFound;

        var r = await t.VoidAsync();

        Assert.Contains("VoidPayment", g.Calls);
        // İlk VoidAll 2069 verdi, ters işlemden sonra ikinci VoidAll da 2069 verecek şekilde
        // sahtelendiği için sonuç belirsiz — kritik olan ters işlemin DENENMESİ.
        Assert.Equal(TransportOutcome.Unknown, r.Outcome);
    }

    [Fact]
    public async Task VoidPayment_basarisizsa_REVERSAL_FAILED_ve_TEKRAR_YOK()
    {
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card);
        await t.SaleAsync(Req(amount: 1000));
        g.Calls.Clear();
        g.Ticket = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card, "RRN7");
        g.Codes["VoidAll"] = GmpCodes.PaymentFound;
        g.Codes["VoidPayment"] = 9999;

        var r = await t.VoidAsync();

        Assert.Equal(TransportOutcome.Unknown, r.Outcome);
        Assert.StartsWith("REVERSAL_FAILED", r.ProviderResultCode);
        // Para hareket etti, geri alınamadı → referans KORUNUR, elle iade için tek dayanak.
        Assert.Equal("RRN7", r.Rrn);
        Assert.Equal(1, g.Calls.Count(c => c == "VoidPayment"));   // ← ÇİVİ: tekrar denenmedi
    }

    [Fact]
    public async Task Mobil_QR_odemesi_NAKIT_gibi_iptal_olur()
    {
        // Canlı terminalde ölçüldü: `paymentType=16` ile kısmi ödemeli fişte `VoidAll` DOĞRUDAN
        // OK döndü (1959 ms, 2069 YOK). Ayrımı "kart mı" diye kursaydık mobil ödeme, sahada hiç
        // çalışmamış ve REVERSAL_FAILED riski taşıyan ters işlem yoluna girerdi.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Mobile);
        await t.SaleAsync(Req(amount: 1000, paymentType: GmpPaymentTypes.Mobile));
        g.Calls.Clear();

        var r = await t.VoidAsync();

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.DoesNotContain("VoidPayment", g.Calls);
    }

    [Fact]
    public void Banka_bacagi_YALNIZ_kartta_vardir()
    {
        Assert.True(GmpPaymentTypes.HasBankLeg(GmpPaymentTypes.Card));
        Assert.False(GmpPaymentTypes.HasBankLeg(GmpPaymentTypes.Cash));
        Assert.False(GmpPaymentTypes.HasBankLeg(GmpPaymentTypes.Mobile));
    }

    [Fact]
    public async Task Tanitici_yokken_ALREADY_DONE_acik_fis_demektir()
    {
        // Bu kodun değeri önce 2331 diye TAHMİN EDİLMİŞTİ ve yanlıştı; doğrusu 2080. Yanlış
        // kalsaydı bu dal sessizce çalışmaz, agent yeniden başladıktan sonra belirsizlik çözümü
        // tam da en gerekli anda çökerdi. Test o değeri çiviliyor.
        var (t, g, _) = Kur();
        g.Codes["Start"] = GmpCodes.AlreadyDone;

        var tk = await t.ReadTicketAsync();

        Assert.True(tk.HasOpenTicket);
        Assert.Equal(2080u, GmpCodes.AlreadyDone);
        // Yoklama fişi açmadığı için kapatma da yapılmaz.
        Assert.DoesNotContain("Close", g.Calls);
    }

    [Fact]
    public async Task Tanitici_yokken_acik_fis_YOKSA_yoklama_fisi_BIRAKILMAZ()
    {
        // `Start` başarılıysa yoklama için bir fiş AÇMIŞ oluruz. Bırakmak, bir sonraki satışın
        // `StartTicket`'ının onu sessizce iptal etmesi demek.
        var (t, g, _) = Kur();

        var tk = await t.ReadTicketAsync();

        Assert.False(tk.HasOpenTicket);
        Assert.Contains("Close", g.Calls);
    }

    [Fact]
    public async Task Mali_hafizadaki_fis_IPTAL_EDILEMEZ()
    {
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card);
        await t.SaleAsync(Req(amount: 1000));
        g.Calls.Clear();
        g.Codes["VoidAll"] = GmpCodes.CannotVoid;

        var r = await t.VoidAsync();

        Assert.Equal(TransportOutcome.Declined, r.Outcome);
        Assert.Equal("ALREADY_FISCALIZED", r.ProviderResultCode);
    }
}
