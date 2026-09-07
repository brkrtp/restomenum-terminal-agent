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
        /// <summary>`Start`'ın döndüreceği tanıtıcı — 2080'de bile dolar (gerçek sarmalayıcı gibi).</summary>
        public ulong NextStartHandle = 42;
        /// <summary>Cihaz tarafında GEÇERSİZLEŞMİŞ tanıtıcılar → 2317 (sahada ölçülen bayatlama).</summary>
        public HashSet<ulong> StaleHandles { get; } = new();
        /// <summary>`Start`'ın SIRAYLA döndüreceği kodlar (boşsa <see cref="Codes"/> geçerli).</summary>
        public Queue<uint> StartSequence { get; } = new();

        private GmpResult Kod(string ad)
        { Calls.Add(ad); return new GmpResult(Codes.TryGetValue(ad, out var c) ? c : GmpCodes.Ok); }

        public GmpResult Start(out ulong handle)
        {
            handle = NextStartHandle;
            if (StartSequence.Count > 0) { Calls.Add("Start"); return new GmpResult(StartSequence.Dequeue()); }
            return Kod("Start");
        }
        public GmpResult TicketHeader(ulong h, int t) { LastTicketType = t; return Kod("TicketHeader"); }
        public GmpResult OptionFlags(ulong h, GmpEchoFlags f)
        { Calls.Add("OptionFlags"); return new GmpResult(StaleHandles.Contains(h) ? GmpCodes.InvalidHandle
            : Codes.TryGetValue("OptionFlags", out var c) ? c : GmpCodes.Ok); }
        public GmpResult ItemSale(ulong h, GmpItem i, out GmpTicket tk) { tk = Ticket; return Kod("ItemSale"); }
        public GmpResult Payment(ulong h, GmpPaymentRequest r, out GmpTicket tk)
        { var res = Kod("Payment"); tk = AfterPayment; return res; }
        public GmpResult GetTicket(ulong h, out GmpTicket tk)
        {
            Calls.Add("GetTicket"); tk = Ticket;
            return new GmpResult(StaleHandles.Contains(h) ? GmpCodes.InvalidHandle
                : Codes.TryGetValue("GetTicket", out var c) ? c : GmpCodes.Ok);
        }
        public GmpResult PrintTotalsAndPayments(ulong h) => Kod("PrintTotalsAndPayments");
        public GmpResult PrintBeforeMF(ulong h) => Kod("PrintBeforeMF");
        public GmpResult PrintUserMessage(ulong h) => Kod("PrintUserMessage");
        public GmpResult PrintMF(ulong h)
        { Calls.Add("PrintMF"); return new GmpResult(PrintMfFailures-- > 0 ? 1u : GmpCodes.Ok); }
        public GmpResult VoidAll(ulong h, out GmpTicket tk) { var r = Kod("VoidAll"); tk = Ticket; return r; }
        public GmpResult VoidPayment(ulong h, int i) => Kod("VoidPayment");
        public GmpResult Close(ulong h) => Kod("Close");
        public GmpResult Echo() => Kod("Echo");
        public GmpResult Pair(GmpPairingConfig c, out GmpDeviceInfo i)
        { i = new GmpDeviceInfo("INGENICO", "TEST", "SN1", "1.0"); return Kod("Pair"); }
        public GmpResult CheckPairing(out bool paired) { paired = true; return Kod("CheckPairing"); }
        public GmpResult Report(GmpReportType t) => Kod("Report:" + t);
        public GmpResult SetIpAddress(string ip, int port) => Kod("SetIpAddress");
        public GmpResult SetInvoice(ulong h, GmpInvoice inv) => Kod("SetInvoice");
        public GmpResult SetDepartments(string json, string pwd) => Kod("SetDepartments");
        public GmpResult GetDepartments(out string json) { json = "[]"; return Kod("GetDepartments"); }
        public GmpResult GetTaxRates(out string json) { json = "[]"; return Kod("GetTaxRates"); }
    }

    private static SaleRequest Req(long amount = 3000, int paymentType = GmpPaymentTypes.Card,
        IReadOnlyList<FiscalLine>? lines = null, string? oturum = null, long? satisToplam = null,
        string komut = "c1") =>
        new(komut, "p1", "t1", amount, "TRY", 2, "prov",
            lines ?? new[] { new FiscalLine("prod1", "Kahve", 1, amount, 20m, DepartmentNo: 1) },
            paymentType, oturum, satisToplam);

    /// <summary>Bellek-içi görüntü deposu. Gerçekte <see cref="CommandStore"/> (disk) kullanılır —
    /// süreç kart penceresinde ölürse görüntünün hayatta kalması şart.</summary>
    private sealed class FakeSnapshots : ITicketSnapshotStore
    {
        private readonly Dictionary<string, (long, long, int)> _d = new();
        public void SaveSnapshot(string commandId, long total, long paid, int count, long? now = null)
            => _d[commandId] = (total, paid, count);
        public (long TotalMinor, long PaidMinor, int PaymentCount)? ReadSnapshot(string commandId)
            => _d.TryGetValue(commandId, out var v) ? v : null;

        /// <summary>Açık fiş ↔ satış oturumu bağı (gerçekte diskte, testte bellekte).</summary>
        private readonly Dictionary<string, string> _bag = new();
        public void BindOpenTicket(string terminalId, string saleSessionId, long? now = null)
            => _bag[terminalId] = saleSessionId;
        public string? ReadOpenTicketBinding(string terminalId)
            => _bag.TryGetValue(terminalId, out var v) ? v : null;
        public void ClearOpenTicketBinding(string terminalId) => _bag.Remove(terminalId);
    }

    private static (GmpTerminalTransport, FakeGmp, FakeSnapshots) Kur()
    {
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        return (new GmpTerminalTransport(g, snap), g, snap);
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
        var (t, g, _) = Kur();
        // Departmanı EKLENTİ çözer; negatif değer "eşlenmemiş" demek ve terminale GİTMEDEN
        // reddedilmeli — tahmin edilmiş bir departman yanlış mali kayıt yazar.
        var r = await t.SaleAsync(Req(lines: new[] { new FiscalLine("prod1", "Kahve", 1, 3000, 20m, DepartmentNo: -1) }));

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
    public async Task Kart_2086_banka_kodlu_ret_UNKNOWN_declined_DEGIL()
    {
        // 2086 = banka kodlu başarısızlık. "İŞLEM ONAYLANMADI" (gerçek ret, para YOK) ile
        // "NO RESPONSE" (yanıt kayıp, para çekilmiş OLABİLİR) aynı kod altında — AYIRT EDİLEMEZ.
        // ← ÇİVİ: bu yüzden Declined DEĞİL Unknown. Declined deseydik kasiyer "başka kart" ister,
        // ilk çekim geçmişse müşteri İKİ KEZ öderdi. Kart para-güvenliğinin ta kendisi.
        var (t, g, _) = Kur();
        g.Codes["Payment"] = GmpCodes.PaymentFailedWithBankCode;   // 2086
        var r = await t.SaleAsync(Req());

        Assert.Equal(TransportOutcome.Unknown, r.Outcome);
        Assert.NotEqual(TransportOutcome.Declined, r.Outcome);
        // ← ÇİVİ: kasaya/deftere giden koşul da "reddedildi" DEĞİL "host'a ulaşılamadı" olmalı;
        // sınıf doğru olup koşul `Refusal` kalsaydı platform yine kesin-ret sayardı.
        Assert.Equal("UnreachableHost", r.ErrorCondition);
        // ← ÇİVİ: belirsiz kart ret'inde fiş KÖRlemesine iptal/ters işlem EDİLMEZ — para
        // çekilmiş olabilir; akıbet yalnız ProbeAsync ile terminale sorularak çözülür. Otomatik
        // VoidAll, gerçekleşmiş bir ödemeyi geri almaya çalışmak olurdu.
        Assert.DoesNotContain("VoidAll", g.Calls);
    }

    [Fact]
    public async Task Kart_2085_BELIRSIZ_kesin_ret_DEGIL()
    {
        // ⚠️ Bu test daha önce `Declined` (kesin ret) bekliyordu ve **YANLIŞTI**. Varsayım "2085 =
        // kart hiç okutulmadı, para hareketi yok, kesin" idi; tek dayanağı bir canlı gözlemdi.
        // Üretici belgesi (GMP3_ErrorHandling_EN_v2) 2085 için yalnız "payment was not a success and
        // there's no specific error message from Banking application" diyor — "cihazda ödeme
        // oluşmadı" DEMİYOR. Aynı belge kapsanmayan/iletişim kaynaklı hatalar için "FP3_Payment
        // cihazda başarılı olmuş olabilir, FP3_GetTicket ile bak" diye uyarıyor.
        //
        // 2026-09-06 21:33 UTC'de bunun bedeli ölçüldü: banka hattı olmayan terminalde kart denemesi
        // 2085 döndü, deneme kasaya `Refusal` = "kart reddedildi" diye kapandı. Kartta sorun yoktu.
        // ← ÇİVİ: 2085 artık belirsizdir; akıbeti ProbeAsync terminale sorarak çözer.
        var (t, g, _) = Kur();
        g.Codes["Payment"] = GmpCodes.PaymentFailed;   // 2085
        var r = await t.SaleAsync(Req());

        Assert.Equal(TransportOutcome.Unknown, r.Outcome);
        Assert.NotEqual("Refusal", r.ErrorCondition);
        Assert.Contains("Payment", r.ProviderResultCode);
        // Belirsiz sonuçta ödeme TEKRAR GÖNDERİLMEZ.
        Assert.Equal(1, g.Calls.Count(c => c == "Payment"));
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
        // Sıra: önce OKU (denetim izi), sonra iptal et. Fişin ne olduğu, SİLİNMEDEN ÖNCE loglanır;
        // sonra loglansaydı iptal sırasındaki bir çökmede ne sildiğimizi kimse bilemezdi.
        Assert.Equal(new[] { "OptionFlags", "GetTicket", "VoidAll", "Close" }, g.Calls);
        Assert.DoesNotContain("VoidPayment", g.Calls);
        Assert.Equal(RestomenumReasons.TicketCancelled, r.Info);
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

    // ── W4: BAYAT TANITICI KURTARMASI (canlıda 2 kez ölçüldü) ───────────────────

    [Fact]
    public async Task BayatTanitici_2317_yenilenir_ve_fis_GERCEKTEN_okunur()
    {
        // ÖLÇÜLEN ARIZA: başarısız ödemeden sonra fişin tanıtıcısı cihaz tarafında geçersizleşiyor.
        // 2026-09-06 01:50 ve 2026-09-07 14:30'daki denemelerde belirsizlik yoklaması 6 turun
        // 6'sında OptionFlags/GetTicket → 2317 aldı; akıbet ÇÖZÜLEMEDİ, kasiyer "operatöre danışın"
        // gördü ve deneme defterde asılı kaldı — tam da kurtarmanın en gerekli olduğu anda.
        // ← ÇİVİ: 2317'de tanıtıcı `FP3_Start` yoklamasıyla YENİLENİR (2080'de bile hTrx dolar) ve
        // fiş GERÇEKTEN okunur. Yenileme kaldırılırsa bu test kırmızıya döner.
        var (t, g, _) = Kur();
        g.Codes["Payment"] = GmpCodes.PaymentFailed;          // 2085 → fiş açık kalır, tanıtıcı bizde
        await t.SaleAsync(Req());

        g.StaleHandles.Add(42);                                // cihaz tarafında geçersizleşti
        g.Codes["Start"] = GmpCodes.AlreadyDone;               // açık fiş var
        g.NextStartHandle = 99;                                // 2080 hTrx'i AÇIK FİŞİN tanıtıcısı
        g.Ticket = new GmpTicket(3000, 0, 0, GmpPaymentTypes.Card);

        var fis = await t.ReadTicketAsync();

        Assert.True(fis.HasOpenTicket);
        Assert.Equal(3000, fis.TotalAmountMinor);   // ← içerik OKUNDU (eskiden 0 dönüyordu)
        Assert.Equal(0, fis.PaymentCount);
    }

    [Fact]
    public async Task BayatTanitici_yenilendikten_sonra_da_okunamiyorsa_TAHMIN_YOK()
    {
        // Yenileme bir kurtarma denemesidir, garanti değil. Başarısızsa uydurulmaz.
        var (t, g, _) = Kur();
        g.Codes["Payment"] = GmpCodes.PaymentFailed;
        await t.SaleAsync(Req());

        g.StaleHandles.Add(42);
        g.StaleHandles.Add(99);                    // yenilenen tanıtıcı da geçersiz
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.NextStartHandle = 99;

        await Assert.ThrowsAsync<TerminalBusyException>(() => t.ReadTicketAsync());
    }

    [Fact]
    public async Task AcikFis_YOKSA_yoklama_fisi_ACIK_BIRAKILMAZ()
    {
        // Yoklama için açılan fiş bırakılırsa bir sonraki satış 2080 alır — yani teşhis aracı
        // arızanın kendisini üretir.
        var (t, g, _) = Kur();
        g.Codes["Payment"] = GmpCodes.PaymentFailed;
        await t.SaleAsync(Req());

        g.StaleHandles.Add(42);
        g.NextStartHandle = 77;                    // Start OK → yeni fiş açıldı
        var oncekiKapanis = g.Calls.Count(c => c == "Close");

        var fis = await t.ReadTicketAsync();

        Assert.False(fis.HasOpenTicket);
        Assert.Equal(oncekiKapanis + 1, g.Calls.Count(c => c == "Close"));
    }

    // ── W4(c): BAYAT FİŞ — KANITLA OTOMATİK TEMİZLİK ────────────────────────────

    [Fact]
    public async Task BayatFis_ODEME_YOKSA_temizlenir_ve_AYNI_satisa_devam_edilir()
    {
        // 2026-09-07: 990 kuruşluk ödemesiz bir fiş 14 saat boyunca HER satışı bloke etti; kasiyer
        // art arda "operatöre danışın" gördü ve terminal elle temizlenene kadar satış yapılamadı.
        // ← ÇİVİ: fişte ödeme YOKSA (cihazın kendi defteriyle kanıtlı) temizlenir ve kasiyer hiçbir
        // şey yapmadan satış sürer. Denetim izi `Restomenum.info` ile taşınır.
        var (t, g, _) = Kur();
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);   // önce: açık fiş var
        g.StartSequence.Enqueue(GmpCodes.Ok);            // temizlik sonrası: fiş açılır
        g.Ticket = new GmpTicket(990, 0, 0, 0);          // ödeme YOK
        g.AfterPayment = new GmpTicket(3000, 3000, 1, GmpPaymentTypes.Card);

        var r = await t.SaleAsync(Req());

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal(RestomenumReasons.StaleTicketCleared, r.Info);
        Assert.Contains("VoidAll", g.Calls);
        Assert.Equal(2, g.Calls.Count(c => c == "Start"));   // temizlik sonrası fiş YENİDEN açıldı
        Assert.Equal(1, g.Calls.Count(c => c == "Payment"));
    }

    [Fact]
    public async Task BayatFis_ODEME_VARSA_DOKUNULMAZ()
    {
        // ← ÇİVİ: üzerinde tahsilat olan fişi silmek geri alınamaz bir para kaybıdır. Kanıt
        // kontrolü kaldırılırsa bu test kırmızıya döner.
        var (t, g, _) = Kur();
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card);   // ödeme VAR

        var r = await t.SaleAsync(Req());

        Assert.DoesNotContain("VoidAll", g.Calls);        // ← ÇİVİ
        Assert.Empty(g.Calls.Where(c => c == "Payment")); // satış da başlamadı
        Assert.Equal(TransportOutcome.Declined, r.Outcome);
        Assert.Equal("PaymentRestriction", r.ErrorCondition);
        Assert.Equal(RestomenumReasons.TicketAlreadyOpen, r.Reason);
        Assert.False(r.PaymentInvoked);
    }

    [Fact]
    public async Task BayatFis_OKUNAMIYORSA_DOKUNULMAZ()
    {
        // Okunamayan fişi silmek, "ödeme yok" varsaymaktır — tam da yasakladığımız şey.
        var (t, g, _) = Kur();
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Codes["GetTicket"] = GmpCodes.RecvBusy;

        var r = await t.SaleAsync(Req());

        Assert.DoesNotContain("VoidAll", g.Calls);
        Assert.Equal(TransportOutcome.Declined, r.Outcome);
        Assert.Equal(RestomenumReasons.TicketAlreadyOpen, r.Reason);
    }

    [Fact]
    public async Task BayatFis_BOZUK_SAYAC_okumasi_ODEME_VAR_tarafina_dusier()
    {
        // Sarmalayıcı fiş dizisinin sınırını aşan sayaçta `PaymentCount = -1` bildirir. Bunu
        // "0 değil ama sayı" diye ele almak, okunamayan bir fişi silmek olurdu.
        var (t, g, _) = Kur();
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(3000, 0, -1, 0);

        var r = await t.SaleAsync(Req());

        Assert.DoesNotContain("VoidAll", g.Calls);
        Assert.Equal(RestomenumReasons.TicketAlreadyOpen, r.Reason);
    }

    // ── W10: KISMI TAHSILAT — AYNI SATISIN ACIK FISINE DEVAM ────────────────────

    [Fact]
    public async Task Ayni_satisin_acik_fisine_KALEM_EKLENMEDEN_odeme_eklenir()
    {
        // Turkiye'de kismi odeme NORMAL: 990'lik adisyona once 500, sonra 490.
        // ← CIVI: ikinci odemede `ItemSale` YENIDEN cagrilirsa fis toplami 990'dan 1980'e cikar
        // ve bu geri alinamaz bir mali kayittir. Devam yolunda yalniz `Payment` cagrilir.
        var (t, g, snap) = Kur();
        snap.BindOpenTicket("t1", "oturum-A");                    // ilk kismi odemeden kalan bag
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);            // cihazda acik fis var
        g.Ticket = new GmpTicket(990, 500, 1, GmpPaymentTypes.Cash);
        g.AfterPayment = new GmpTicket(990, 990, 2, GmpPaymentTypes.Cash);

        var r = await t.SaleAsync(Req(amount: 490, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.DoesNotContain("ItemSale", g.Calls);               // ← CIVI
        Assert.DoesNotContain("TicketHeader", g.Calls);
        Assert.DoesNotContain("VoidAll", g.Calls);                // bayat-fis yolu HIC calismadi
        Assert.Equal(1, g.Calls.Count(c => c == "Payment"));
    }

    [Fact]
    public async Task Devam_yolunda_TAM_odemede_fis_kapanir_ve_bag_silinir()
    {
        // Bag birakilsaydi bir sonraki satis "ayni satisin fisi" sanip KAPANMIS bir fise odeme
        // eklemeye calisirdi.
        var (t, g, snap) = Kur();
        snap.BindOpenTicket("t1", "oturum-A");
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(990, 500, 1, GmpPaymentTypes.Cash);
        g.AfterPayment = new GmpTicket(990, 990, 2, GmpPaymentTypes.Cash);   // tam odendi

        await t.SaleAsync(Req(amount: 490, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Contains("PrintMF", g.Calls);
        Assert.Contains("Close", g.Calls);
        Assert.Null(snap.ReadOpenTicketBinding("t1"));            // ← CIVI
    }

    [Fact]
    public async Task Kismi_odemede_bag_YAZILIR()
    {
        // Fis acik kaldi: sahibini diske yaz ki ajan yeniden baslasa bile kalan tahsilat AYNI
        // fise eklenebilsin.
        var (t, g, snap) = Kur();
        g.AfterPayment = new GmpTicket(990, 500, 1, GmpPaymentTypes.Cash);   // kismi

        await t.SaleAsync(Req(amount: 500, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("oturum-A", snap.ReadOpenTicketBinding("t1"));
        Assert.DoesNotContain("Close", g.Calls);                  // kismi odemede fis KAPANMAZ
    }

    [Fact]
    public async Task Fis_toplami_satis_toplamiyla_uyusmuyorsa_DEVAM_EDILMEZ()
    {
        // Kasiyer kismi odemeden sonra kalem eklemis/silmis. Devam etmek fise yanlis tutarda
        // odeme yazmaktir. ← CIVI: odeme HIC baslamaz.
        var (t, g, snap) = Kur();
        snap.BindOpenTicket("t1", "oturum-A");
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(1490, 500, 1, GmpPaymentTypes.Cash);   // fis 1490, satis 990

        var r = await t.SaleAsync(Req(amount: 490, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(TransportOutcome.Declined, r.Outcome);
        Assert.Equal(RestomenumReasons.TicketSaleMismatch, r.Reason);
        Assert.Equal("PaymentRestriction", r.ErrorCondition);
        Assert.False(r.PaymentInvoked);
        Assert.DoesNotContain("Payment", g.Calls);                // ← CIVI
        Assert.DoesNotContain("VoidAll", g.Calls);                // yanlis fis SILINMEZ de
    }

    [Fact]
    public async Task Istenen_tutar_kalani_asiyorsa_terminale_GIDILMEZ()
    {
        var (t, g, snap) = Kur();
        snap.BindOpenTicket("t1", "oturum-A");
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(990, 500, 1, GmpPaymentTypes.Cash);   // kalan 490

        var r = await t.SaleAsync(Req(amount: 700, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(RestomenumReasons.AmountExceedsRemaining, r.Reason);
        Assert.DoesNotContain("Payment", g.Calls);
    }

    [Fact]
    public async Task FARKLI_satisin_odemeli_fisine_DOKUNULMAZ()
    {
        // Dunden kalmis, uzerinde para olan bir fis baska bir adisyon icin SILINMEZ.
        var (t, g, snap) = Kur();
        snap.BindOpenTicket("t1", "oturum-A");
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(990, 500, 1, GmpPaymentTypes.Cash);

        var r = await t.SaleAsync(Req(amount: 500, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-B", satisToplam: 990));       // BASKA oturum

        Assert.Equal(RestomenumReasons.TicketAlreadyOpen, r.Reason);
        Assert.DoesNotContain("VoidAll", g.Calls);        // ← CIVI: para ustundeki fis silinmez
        Assert.DoesNotContain("Payment", g.Calls);
    }

    [Fact]
    public async Task Oturum_kimligi_YOKSA_devam_yolu_KAPALI()
    {
        // Eski platform alani gondermiyor. Emin olmadan fise odeme eklemek yerine bayat-fis
        // mantigina duseriz (fail-safe).
        var (t, g, snap) = Kur();
        snap.BindOpenTicket("t1", "oturum-A");
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(990, 500, 1, GmpPaymentTypes.Cash);

        var r = await t.SaleAsync(Req(amount: 490, paymentType: GmpPaymentTypes.Cash,
            oturum: null, satisToplam: 990));

        Assert.Equal(RestomenumReasons.TicketAlreadyOpen, r.Reason);
        Assert.DoesNotContain("Payment", g.Calls);
    }
}
