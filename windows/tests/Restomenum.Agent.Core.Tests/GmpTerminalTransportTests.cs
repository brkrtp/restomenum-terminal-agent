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
    /// <summary>Tek sahte sarmalayici: W56 tazeleyicisi de bunu kullanir. Iki ayri sahte, iki ayri
    /// "cihaz nasil davranir" tanimi demek olurdu.</summary>
    internal sealed class FakeGmp : IGmpWrapper
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
        /// <summary>Cihaza GIDEN kalemler — indirimin gercekten gonderilip gonderilmedigi burada gorulur.</summary>
        public List<GmpItem> ItemSales { get; } = new();
        public GmpResult ItemSale(ulong h, GmpItem i, out GmpTicket tk)
        { ItemSales.Add(i); tk = Ticket; return Kod("ItemSale"); }
        /// <summary>Cihaza GİDEN ödeme isteği — banka seçiminin taşınıp taşınmadığı burada görülür.</summary>
        public GmpPaymentRequest? LastPaymentRequest;
        /// <summary>TUM odeme istekleri sirasiyla (W57 yoklamasi dort tipi arka arkaya deniyor).</summary>
        public List<GmpPaymentRequest> PaymentRequests { get; } = new();
        /// <summary>`Payment`in SIRAYLA donecegi kodlar (bossa <see cref="Codes"/> gecerli).</summary>
        public Queue<uint> PaymentSequence { get; } = new();
        public GmpResult Payment(ulong h, GmpPaymentRequest r, out GmpTicket tk)
        {
            LastPaymentRequest = r; PaymentRequests.Add(r); tk = AfterPayment;
            if (PaymentSequence.Count > 0) { Calls.Add("Payment"); return new GmpResult(PaymentSequence.Dequeue()); }
            return Kod("Payment");
        }
        public GmpResult GetTicket(ulong h, out GmpTicket tk)
        {
            Calls.Add("GetTicket"); tk = Ticket;
            return new GmpResult(StaleHandles.Contains(h) ? GmpCodes.InvalidHandle
                : Codes.TryGetValue("GetTicket", out var c) ? c : GmpCodes.Ok);
        }
        /// <summary>W109: her baskı/kapatma çağrısına GEÇİLEN zaman aşımı — bütçenin çivisi bunu okur.</summary>
        public List<(string Ad, int? Sure)> Sureler { get; } = new();
        /// <summary>Baskı ÇAĞRISI SIRASINDA çalışır — "süreç tam burada ölseydi ne kalırdı"
        /// sorusunu ölçmenin tek yolu. Çağrıdan önce/sonraki hâl bunu gösteremez.</summary>
        public Action? BaskiSirasinda;
        public GmpResult PrintTotalsAndPayments(ulong h, int? t = null)
        { Sureler.Add(("PrintTotalsAndPayments", t)); BaskiSirasinda?.Invoke(); return Kod("PrintTotalsAndPayments"); }
        public GmpResult PrintBeforeMF(ulong h, int? t = null)
        { Sureler.Add(("PrintBeforeMF", t)); return Kod("PrintBeforeMF"); }
        public GmpResult PrintUserMessage(ulong h) => Kod("PrintUserMessage");
        /// <summary>
        /// ⚠️ `Codes["PrintMF"]` ÖNCE bakılır, sonra <see cref="PrintMfFailures"/>. Eskiden yalnız
        /// sayaç okunuyordu ve `Codes` SESSİZCE yok sayılıyordu — bir testi kurarken belirli bir
        /// kodu (0x0020) verdim, sahte onu yuttu ve test yanlış sebeple kırmızıya döndü.
        /// Sahtenin sessizce yok saydığı ayar, yanlış teşhis üretir.
        /// </summary>
        public GmpResult PrintMF(ulong h, int? t = null)
        {
            Sureler.Add(("PrintMF", t)); Calls.Add("PrintMF");
            if (Codes.TryGetValue("PrintMF", out var c)) return new GmpResult(c);
            return new GmpResult(PrintMfFailures-- > 0 ? 1u : GmpCodes.Ok);
        }
        /// <summary>W60 yoklamasinin denedigi indirim cagrilari — istekler SIRAYLA kaydedilir.</summary>
        public List<(long Amount, string Text, int Index)> MinusCalls { get; } = new();
        public List<(int Rate, string Text, int Index)> DecCalls { get; } = new();
        /// <summary>`Dec`in dondurecegi hesaplanmis tutar (cihaz kurusa kendi yuvarlar).</summary>
        public long DecChanged = 0;
        public GmpResult Minus(ulong h, long amountMinor, string text, int indexOfItem, out GmpTicket tk)
        { MinusCalls.Add((amountMinor, text, indexOfItem)); tk = Ticket; return Kod("Minus"); }
        public GmpResult Dec(ulong h, int ratePercent, string text, int indexOfItem, out long changedMinor, out GmpTicket tk)
        { DecCalls.Add((ratePercent, text, indexOfItem)); changedMinor = DecChanged; tk = Ticket; return Kod("Dec"); }
        public GmpResult VoidAll(ulong h, out GmpTicket tk) { var r = Kod("VoidAll"); tk = Ticket; return r; }
        public GmpResult VoidPayment(ulong h, int i) => Kod("VoidPayment");
        public GmpResult Close(ulong h, int? t = null)
        { Sureler.Add(("Close", t)); return Kod("Close"); }
        /// <summary>Cihazin HAM durum alani (W112). Saglikli olcum: 0x0000001D.</summary>
        public uint EchoStatus = 0x0000001D;
        /// <summary>W142: sahte, cihazın GERÇEKTEN gönderdiği alanları taşır — sayaçlar dahil.
        /// Sahtenin gerçekten ayrıldığı her yer, yeşil bir testin altında saklanan kusurdur.</summary>
        public EchoDurum EchoDurumu = new(0x1D, ZNo: 91, FisNo: 2, EkuNo: 1, Handle: 0);
        /// <summary>Doluysa `Echo` bunu FIRLATIR — ayrıştırıcı istisnasının karşılığı.</summary>
        public Exception? EchoPatlat;
        public GmpResult Echo(out EchoDurum durum)
        {
            if (EchoPatlat is not null) { Calls.Add("Echo"); throw EchoPatlat; }
            durum = EchoDurumu; return Kod("Echo");
        }
        public GmpResult GetPaymentApplicationsRaw(out string json, out int total, out int received, byte requested = 20)
        { json = "[]"; total = 0; received = 0; return Kod("GetPaymentApplications"); }
        public GmpResult Pair(GmpPairingConfig c, out GmpDeviceInfo i)
        { i = new GmpDeviceInfo("INGENICO", "TEST", "SN1", "1.0"); return Kod("Pair"); }
        public GmpResult CheckPairing(out bool paired) { paired = true; return Kod("CheckPairing"); }
        public GmpResult Report(GmpReportType t) => Kod("Report:" + t);
        public GmpResult SetIpAddress(string ip, int port) => Kod("SetIpAddress");
        public GmpResult SetInvoice(ulong h, GmpInvoice inv) => Kod("SetInvoice");
        public GmpResult SetDepartments(string json, string pwd) => Kod("SetDepartments");
        /// <summary>Cihazin bolum/oran tablolari (W56). Varsayilan bos — eski testler etkilenmez.</summary>
        public string DepartmentsJson = "[]";
        public string TaxRatesJson = "[]";
        public GmpResult GetDepartments(out string json) { json = DepartmentsJson; return Kod("GetDepartments"); }
        public GmpResult GetTaxRates(out string json) { json = TaxRatesJson; return Kod("GetTaxRates"); }
    }

    // ── P27: FİŞ KİMLİĞİ + KAPANIŞ LİSTESİ ──────────────────────────────────────

    [Fact]
    public async Task Ayni_fisin_iki_kismi_odemesi_AYNI_ticketId_alir()
    {
        // ← ÇİVİ: kimlik fiş AÇILIŞINDA üretilir. Her ödemede yeniden üretilseydi aynı fişin
        // ödemeleri iki ayrı fişe bölünür ve kapanış listesi eksik giderdi — yani gerçekleşmiş
        // bir tahsilat deftere HİÇ yazılmazdı.
        var (t, g, snap) = Kur();
        g.Ticket = new GmpTicket(990, 200, 1, GmpPaymentTypes.Cash);
        g.AfterPayment = new GmpTicket(990, 200, 1, GmpPaymentTypes.Cash);   // kısmi

        var r1 = await t.SaleAsync(Req(amount: 200, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990, komut: "c1", odeme: "pay-1"));

        // İkinci ödeme: cihazda AÇIK fiş var (2080) ve bağ aynı oturuma işaret ediyor → devam.
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        var r2 = await t.SaleAsync(Req(amount: 300, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990, komut: "c2", odeme: "pay-2"));

        Assert.NotNull(r1.TicketId);
        Assert.Equal(r1.TicketId, r2.TicketId);       // ← ÇİVİ
        Assert.Equal(1, snap.UretilenKimlik);         // devam yolunda YENİ kimlik ÜRETİLMEDİ
    }

    [Fact]
    public async Task Fis_kapaninca_kapanis_listesi_BIZIM_odemelerimizi_tasir()
    {
        // ← ÇİVİ: liste cihazın tablosundan değil, kendi defterimizden gelir. Cihaz bizim
        // `paymentId`'mizi bilmez; sıraya bakıp eşleştirmek de olmaz çünkü cihaz BAŞARISIZ
        // denemeleri de fişte kayıt olarak tutuyor (ölçüldü 2026-09-07).
        var (t, g, snap) = Kur();
        g.Ticket = new GmpTicket(990, 200, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 200, 1, GmpPaymentTypes.Cash);
        await t.SaleAsync(Req(amount: 200, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990, komut: "c1", odeme: "pay-1"));

        // İkinci ödeme fişi TAMAMLIYOR.
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.AfterPayment = new GmpTicket(990, 990, 2, GmpPaymentTypes.Card);
        var r = await t.SaleAsync(Req(amount: 790, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990, komut: "c2", odeme: "pay-2"));

        Assert.Equal("CLOSED", r.TicketState);
        var liste = Assert.IsAssignableFrom<IReadOnlyList<TicketPaymentRow>>(r.ClosedTicketPayments);
        Assert.Equal(new[] { "pay-1", "pay-2" }, liste.Select(x => x.PaymentId));
        Assert.Equal(new long[] { 200, 790 }, liste.Select(x => x.AmountMinor));
        Assert.Equal(GmpPaymentTypes.Card, liste[1].MethodType);
        // Cihazın tahsil toplamı AYRI gider: listemizin toplamıyla karşılaştırılabilsin diye.
        Assert.Equal(200, r.DevicePaidMinor);         // pre-close okuması g.Ticket'tan
        Assert.Null(snap.ReadOpenTicketId("t1"));     // fiş kapandı → bağ da kimlik de silindi
    }

    [Fact]
    public async Task Close_BASARISIZSA_kapanis_listesi_URETILMEZ()
    {
        // ← ÇİVİ: fiş hâlâ açıkken "kapandı, satırları yaz" demek, kapanmamış bir fişin
        // ödemelerini deftere geçirmek olurdu.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.Codes["Close"] = 1234;

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.NotEqual("CLOSED", r.TicketState);
        Assert.Null(r.ClosedTicketPayments);
    }

    [Fact]
    public async Task Baski_yarim_kalirsa_fis_ACIK_bildirilir_ve_BAG_KORUNUR()
    {
        // ← ÇİVİ: baskı adımı düşerse `FP3_Close` HİÇ çağrılmaz — fiş cihazda AÇIK kalır.
        // "Kapandı" demek iki ayrı hasar üretirdi: (1) platform o fişin ödemelerini deftere yazar
        // oysa fiş hâlâ iptal edilebilir; (2) bağ silinirse aynı satışın sonraki ödemesi kendi
        // fişini "bayat fiş" sanıp TICKET_ALREADY_OPEN yer ve kasa manuel ödemeye düşer.
        var (t, g, snap) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.Codes["PrintBeforeMF"] = 1234;

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990, odeme: "pay-1"));

        Assert.Equal(TransportOutcome.Approved, r.Outcome);   // ödeme ALINDI, ret DEĞİL
        Assert.Equal("OPEN", r.TicketState);                  // ← ÇİVİ
        Assert.Null(r.ClosedTicketPayments);
        Assert.DoesNotContain("Close", g.Calls);
        Assert.Equal("oturum-A", snap.ReadOpenTicketBinding("t1"));   // ← ÇİVİ: bağ DURUYOR
    }

    [Fact]
    public async Task Kapanis_kaydi_BAG_SILINMEDEN_once_yazilir()
    {
        // ← ÇİVİ: sıra. Bağ silinmek ZORUNDA (yoksa sonraki satış kapanmış fişin kimliğini
        // devralır), ama kapanış gövdesi üst katmanda kuruluyor. Silme ile outbox'a yazma
        // arasında süreç ölürse replay edecek kimlik kalmaz ve o fişin ödemeleri deftere HİÇ
        // yazılmaz — üstelik bunu keşfedecek başka bir yol da yok.
        var (t, g, snap) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990, odeme: "pay-1"));

        var mark = snap.KapanisSirasi.IndexOf("mark:" + r.TicketId);
        var clear = snap.KapanisSirasi.IndexOf("clear:t1");
        Assert.True(mark >= 0 && clear >= 0, "iki adım da çalışmalı");
        Assert.True(mark < clear, "kalıcı kapanış kaydı bağ SİLİNMEDEN ÖNCE yazılmalı");
        var bekleyen = Assert.Single(snap.PendingClosedTickets());
        Assert.Equal(990, bekleyen.PaidMinor);
        Assert.Equal("oturum-A", bekleyen.SaleSessionId);
    }

    [Fact]
    public async Task Kapanan_fisten_SONRA_ayni_oturumda_YENI_fis_YENI_kimlik_alir()
    {
        // ← ÇİVİ: aynı masa oturumunda arka arkaya İKİ fiş kapanabilir (kısmi öde → kapan →
        // aynı masaya yeni sipariş). Kimlik `sale.uuid`'e dayansaydı ikinci kapanış "tekrar"
        // sayılır ve o fişin ödemeleri deftere HİÇ yazılmazdı.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);

        var r1 = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990, komut: "c1", odeme: "pay-1"));
        var r2 = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990, komut: "c2", odeme: "pay-2"));

        Assert.Equal("CLOSED", r1.TicketState);
        Assert.Equal("CLOSED", r2.TicketState);
        Assert.NotEqual(r1.TicketId, r2.TicketId);                     // ← ÇİVİ
        Assert.Equal(new[] { "pay-2" }, r2.ClosedTicketPayments!.Select(x => x.PaymentId));
    }

    // ── W25: BAŞARISIZ DENEME SATIRI — "fişe yazılmadı" ile "bankadan geçmedi" AYRI ──

    /// <summary>Yoklama için kur: anlık görüntü + cihazın ŞU ANKİ fişi.</summary>
    private static (GmpTerminalTransport, FakeGmp, FakeSnapshots) YoklamaKur(
        GmpTicket simdikiFis, int gorunenSayi = 1, long gorunenTahsil = 200)
    {
        var (t, g, snap) = Kur();
        g.Ticket = simdikiFis;
        // Yoklama tanıtıcıyı kendi tazeliyor: `Start` 2080 dönerse cihazda AÇIK fiş var demektir.
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        snap.SaveSnapshot("c1", 990, gorunenTahsil, gorunenSayi, "oturum-A");
        snap.BindOpenTicket("t1", "oturum-A");
        return (t, g, snap);
    }

    [Fact]
    public async Task Bankadan_CEVAP_gelmediyse_NotLanded_ama_TEKRAR_GUVENLI_DEGIL()
    {
        // Ölçülen vaka (2026-09-07, Garanti 62): sayaç 1→2, tahsil 200'de sabit, yeni satır
        // payAmount 0, AppErrorCode "0000", ErrorMsg "NO RESPONSE".
        // ← ÇİVİ: fişe yazılmadığı KESİN ama bankada yetim provizyon olabilir → UnreachableHost.
        var (t, g, _) = YoklamaKur(new GmpTicket(990, 200, 2, GmpPaymentTypes.Card,
            Payments: new[]
            {
                new GmpPaymentLine(GmpPaymentTypes.Cash, 200),
                new GmpPaymentLine(GmpPaymentTypes.Card, 0, 62, "GARANTİ BBVA", "NO RESPONSE", "0000"),
            },
            PaymentsAreComplete: true));

        var y = await t.ProbeAsync(Req(oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(ProbeVerdict.NotLanded, y.Verdict);
        Assert.True(y.CounterRead);
        Assert.Equal("UnreachableHost", y.ErrorCondition);          // ← ÇİVİ
        // ← ÇİVİ: `reason` CİHAZIN sebebi; `NOT_LANDED` yalnız `info`'da. Ters olsaydı platform
        // bunu "kart hiç çekilmedi, tekrar dene" diye okur ve çift tahsilat üretirdi.
        Assert.Equal(RestomenumReasons.NoResponse, y.Reason);
        Assert.Equal(RestomenumReasons.NotLandedOnTicket, y.Info);
        Assert.NotEqual(RestomenumReasons.NotLanded, y.Reason);
        Assert.Contains("GÜVENLİ DEĞİL", y.Note);                   // ← ÇİVİ: çift tahsilat kapısı
    }

    [Fact]
    public async Task Banka_ACIKCA_reddettiyse_Refusal_ve_tekrar_guvenli()
    {
        // Ölçülen vaka (Akbank 46): AppErrorCode "2202", AppErrorMsg "İŞLEM ONAYLANMADI".
        var (t, g, _) = YoklamaKur(new GmpTicket(990, 200, 2, GmpPaymentTypes.Card,
            Payments: new[]
            {
                new GmpPaymentLine(GmpPaymentTypes.Cash, 200),
                // W79 §7: GERÇEK banka reddinde yetkilendirme DENENMİŞTİR — onay kodu/RRN dolu.
                // Boş olsalardı bu "banka reddetti" değil "bankaya hiç gidilmedi" olurdu.
                new GmpPaymentLine(GmpPaymentTypes.Card, 0, 46, "AKBANK", "TERMINAL KAPALI ", "2202",
                    AuthorizeCode: "123456", Rrn: "600112233445"),
            },
            PaymentsAreComplete: true));

        var y = await t.ProbeAsync(Req(oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(ProbeVerdict.NotLanded, y.Verdict);
        Assert.Equal("Refusal", y.ErrorCondition);                  // ← ÇİVİ
        Assert.Equal(RestomenumReasons.BankDeclined, y.Reason);
        Assert.Equal(RestomenumReasons.NotLandedOnTicket, y.Info);
        Assert.Contains("tekrar güvenli", y.Note);
    }

    [Fact]
    public async Task Satirlar_EKSIKSE_hicbir_sey_iddia_edilmez()
    {
        // ← ÇİVİ: "okunamadı" ≠ "boş". Liste tam değilse hangi satırın yeni olduğu söylenemez;
        // sıfır tutarlı bir satır uydurup "yazılmadı" demek kanıtsız kesinlik olurdu.
        var (t, g, _) = YoklamaKur(new GmpTicket(990, 200, 2, GmpPaymentTypes.Card,
            Payments: new[] { new GmpPaymentLine(GmpPaymentTypes.Card, 0, 62, "GARANTİ BBVA", "NO RESPONSE", "0000") },
            PaymentsAreComplete: false));

        var y = await t.ProbeAsync(Req(oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(ProbeVerdict.Indeterminate, y.Verdict);
        Assert.Null(y.ErrorCondition);
    }

    [Fact]
    public async Task Yeni_satirda_TUTAR_varken_fis_toplami_artmadiysa_BELIRSIZ()
    {
        // İki okuma birbirini tutmuyor. Çözmüş gibi yapmak kanıtsız kesinlik üretirdi.
        var (t, g, _) = YoklamaKur(new GmpTicket(990, 200, 2, GmpPaymentTypes.Card,
            Payments: new[]
            {
                new GmpPaymentLine(GmpPaymentTypes.Cash, 200),
                new GmpPaymentLine(GmpPaymentTypes.Card, 300, 62, "GARANTİ BBVA"),
            },
            PaymentsAreComplete: true));

        var y = await t.ProbeAsync(Req(oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(ProbeVerdict.Indeterminate, y.Verdict);
    }

    [Fact]
    public async Task Basarisiz_bacak_SAYACTAN_DUSULMEZ_bayat_fis_korumasi_DURUR()
    {
        // ← ÇİVİ: sayaç değişmiyor (W25 kararı). Yalnız başarısız bacağı olan bir fiş bile
        // "üzerinde ödeme var" sayılır ve otomatik silinmez — silmek mali kaydı yok etmek olurdu.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 0, 1, GmpPaymentTypes.Card,
            Payments: new[] { new GmpPaymentLine(GmpPaymentTypes.Card, 0, 62, "GARANTİ BBVA", "NO RESPONSE", "0000") },
            PaymentsAreComplete: true);
        g.Codes["Start"] = GmpCodes.AlreadyDone;

        var r = await t.SaleAsync(Req(oturum: "baska-oturum", satisToplam: 990));

        Assert.Equal(TransportOutcome.Declined, r.Outcome);
        Assert.Equal(RestomenumReasons.TicketAlreadyOpen, r.Reason);
        Assert.Contains("ODEME_VAR:1", r.ProviderResultCode);       // ← ÇİVİ: sayaç 1 kaldı
        Assert.DoesNotContain("VoidAll", g.Calls);                  // fişe DOKUNULMADI
    }

    // ── W38: açık fişte cihazın toplam/kalan rakamı ─────────────────────────────

    [Fact]
    public async Task Acik_fiste_cihazin_TOPLAM_ve_KALAN_rakami_bildirilir()
    {
        // ← ÇİVİ: kısmi ödemede kasa kalanı KENDİ tabanından hesaplamak zorundaydı; cihazla
        // panel ayrışırsa kimse fark etmiyordu. Artık otorite cihazın rakamı.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(2380, 990, 1, GmpPaymentTypes.Cash);   // kısmi

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 2380));

        Assert.Equal("OPEN", r.TicketState);
        Assert.Equal(2380, r.DeviceTicketTotalMinor);
        Assert.Equal(1390, r.DeviceRemainingMinor);      // 2380 − 990
    }

    [Theory]
    [InlineData(0, 0)]        // toplam sıfır
    [InlineData(-100, 50)]    // toplam negatif
    [InlineData(990, -1)]     // ödenen negatif
    [InlineData(990, 1500)]   // ödenen toplamı aşıyor → kalan negatif çıkardı
    public async Task Cihaz_rakamlari_TUTARSIZSA_ikisi_de_KONMAZ(long toplam, long odenen)
    {
        // ← ÇİVİ: kasa bu sayıyı OTORİTE kabul edecek; yanlış bir sayı, kasanın kendi
        // hesabından daha kötüdür. Şüphede ikisini birden bırakıyoruz.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(toplam, odenen, 1, GmpPaymentTypes.Cash);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 2380));

        Assert.Null(r.DeviceTicketTotalMinor);
        Assert.Null(r.DeviceRemainingMinor);
    }

    [Fact]
    public async Task Kapali_fiste_davranis_DEGISMEDI()
    {
        // W79 §6b: kapanışta kalan artık AÇIKÇA 0 gönderiliyor — eskiden hiç konmuyordu.
        // Sebep ölçüldü (platform `bodyRead.js:99-106`): iki alan ÇİFT kabul ediliyor, kalan
        // yoksa TOPLAM da düşüyor; yani gönderdiğimiz 6793/941 platforma HİÇ ulaşmamıştı.
        // Sıfır burada UYDURULMUŞ değil ÖLÇÜLMÜŞTÜR: yalnız kapanış okuması gerçekten
        // başarılıysa konur (aşağıdaki ikinci test okuma başarısızken konmadığını çiviliyor).
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("CLOSED", r.TicketState);
        Assert.Equal(990, r.DeviceTicketTotalMinor);     // kapanış okumasından
        Assert.Equal(0, r.DeviceRemainingMinor);         // ← ÇİVİ: kapalıda kalan SIFIR (ölçülmüş)
    }

    // ── W79: TAHSİL ≠ İSTENEN, İPTAL SAYILARI, BANKAYA GİTMEDİ ──────────────────

    [Fact]
    public async Task KAYNAKLAR_CELISIRSE_hicbir_tutar_iddia_edilmez()
    {
        // ← ÇİVİ (§1): iki kaynak var — bu denemenin ödeme satırı ve fişin tahsil ARTIŞI.
        // Tutmuyorlarsa yanlış tutar bildirmektense "bilmiyorum" denir. Bu çivi olmasaydı tek
        // kaynağa güvenip sessizce yanlış bir tahsilat yazabilirdik.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 0, 0, GmpPaymentTypes.Card);
        g.AfterPayment = new GmpTicket(941, 700, 1, GmpPaymentTypes.Card,   // delta 700 ≠ satır 941
            Payments: new[] { new GmpPaymentLine(GmpPaymentTypes.Card, 941, 62, "GARANTİ BBVA") });

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Null(r.CollectedAmountMinor);           // ← ÇİVİ
        Assert.Null(r.MerchantDiscountMinor);          // farkı da iddia etmeyiz
        Assert.Equal(990, r.ApprovedAmountMinor);      // istenen yine gider
    }

    [Fact]
    public async Task TAHSIL_ISTENENE_ESITSE_indirim_alani_KONMAZ()
    {
        // Gürültü yok: her satışa "indirim 0" yazmak gerçek indirimi görünmez kılardı.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 0, 0, GmpPaymentTypes.Card);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card,
            Payments: new[] { new GmpPaymentLine(GmpPaymentTypes.Card, 990, 62, "GARANTİ BBVA") },
            PaymentsAreComplete: true);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(990, r.CollectedAmountMinor);
        Assert.Null(r.MerchantDiscountMinor);          // ← ÇİVİ
    }

    [Fact]
    public async Task DEFTERE_TAHSIL_EDILEN_yazilir()
    {
        // ← ÇİVİ (§13): değişmez `sum(collected) == paid_minor`. Bugün bu değişmez YOKTU ve dört
        // kez kırıldı (88/2: 7150↔6793 · 88/4: 990↔941 · FR30: 3380↔2311 · 88/11: 3380↔2331).
        var (t, g, snap) = Kur();
        g.Ticket = new GmpTicket(990, 0, 0, GmpPaymentTypes.Card);
        g.AfterPayment = new GmpTicket(941, 941, 1, GmpPaymentTypes.Card,
            Payments: new[] { new GmpPaymentLine(GmpPaymentTypes.Card, 941, 62, "GARANTİ BBVA") },
            TotalDiscountMinor: 49, PaymentsAreComplete: true);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990, odeme: "pay-1"));

        Assert.Equal(941, snap.Tahsil["pay-1"]);       // ← ÇİVİ: deftere TAHSİL edilen gitti
        Assert.Equal(r.CollectedAmountMinor, snap.Tahsil["pay-1"]);  // ← ÇİVİ: gövde ile defter AYNI
        Assert.NotEqual(990, snap.Tahsil["pay-1"]);    // ← ÇİVİ: İSTENEN yazılmadı (eski hata)
    }

    [Fact]
    public async Task KAPANIS_OKUNAMAZSA_kalan_KONMAZ()
    {
        // ← ÇİVİ (§6b): sıfır yalnız ÖLÇÜLMÜŞ sıfırdır. Kapanış okuması başarısızsa toplam da
        // yoktur; uydurulmuş sıfır göndermek "bilinmiyor"u "sıfır" diye yazdırmak olurdu.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.Codes["GetTicket"] = GmpCodes.InvalidHandle;      // kapanış okuması BAŞARISIZ

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Null(r.DeviceTicketTotalMinor);
        Assert.Null(r.DeviceRemainingMinor);               // ← ÇİVİ
    }

    [Fact]
    public async Task BANKAYA_GITMEDIYSE_sebep_BANK_DECLINED_DEGIL()
    {
        // ← ÇİVİ (§7): `E003` kullanılabilir bir koddur; eski ikili ayrım onu "banka reddetti"
        // sayıyordu. Ama onay kodu ve RRN BOŞ, tutar 0 — yetkilendirme HİÇ olmamış. "Banka
        // reddetti" kasiyeri BAŞKA kart denemeye iter; doğrusu aynı kartla tekrar denemektir.
        // Para güvenliği aynı kalır: yetkilendirme yoksa yetim provizyon da yok.
        var (t, g, _) = YoklamaKur(new GmpTicket(990, 200, 2, GmpPaymentTypes.Card,
            Payments: new[]
            {
                new GmpPaymentLine(GmpPaymentTypes.Cash, 200),
                new GmpPaymentLine(GmpPaymentTypes.Card, 0, 62, "GARANTİ BBVA", "KULLANICI VAZGEÇTİ", "E003"),
            },
            PaymentsAreComplete: true));

        var y = await t.ProbeAsync(Req(oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("Refusal", y.ErrorCondition);                   // koşul AYNI (tekrar güvenli)
        Assert.Equal(RestomenumReasons.NotSentToBank, y.Reason);     // ← ÇİVİ: sebep FARKLI
    }

    [Fact]
    public async Task IPTALDE_DENEME_ile_GERI_ALINAN_AYRI_sayilir()
    {
        // ← ÇİVİ (§10/W83): üç satırın ikisi başarısız (tutar 0). "Geri alınan ödeme" yalnız
        // parası olandır; "deneme" hepsidir. Tek sayıya indirgenirse kasa "3 ödeme geri alındı,
        // sıfır lira" gibi kendi içinde çelişkili bir şey görür — sahada tam bu oldu (fiş 88/10).
        var (t, g, snap) = Kur();
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Ticket = new GmpTicket(990, 400, 3, GmpPaymentTypes.Card,
            Payments: new[]
            {
                new GmpPaymentLine(GmpPaymentTypes.Card, 0, 46, "AKBANK", "KULLANICI VAZGECTI", "E003"),
                new GmpPaymentLine(GmpPaymentTypes.Cash, 400),
                new GmpPaymentLine(GmpPaymentTypes.Card, 0, 62, "GARANTİ BBVA", "NO RESPONSE", "0000"),
            },
            PaymentsAreComplete: true);
        snap.BindOpenTicket("t1", "oturum-B");

        var r = await t.VoidTicketAsync("t1");

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal(1, r.VoidedPaymentCount);      // ← ÇİVİ: yalnız PARASI olan
        Assert.Equal(3, r.VoidedAttemptCount);      // ← ÇİVİ: deneme sayısı KAYBOLMADI
        Assert.Equal(400, r.VoidedAmountMinor);
    }

    [Fact]
    public async Task TERS_ISLEM_YARIM_KALIRSA_tutar_NIYET_olarak_gider()
    {
        // ← ÇİVİ (§11/W88): 2026-09-09 18:11'de gövde aynı anda "VOID_INCOMPLETE" ve "941 geri
        // alındı" dedi; platform 400 `invalidResult` ile reddetti. Geri ALINMADI — geri almaya
        // ÇALIŞILDI. İki ayrı soru, iki ayrı alan.
        var (t, g, snap) = Kur();
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Codes["VoidAll"] = GmpCodes.PaymentFound;          // 2069 → ters işlem gerekiyor
        g.Codes["VoidPayment"] = GmpCodes.Timeout;           // ve o ters işlem YARIM kaldı
        g.Ticket = new GmpTicket(2331, 941, 1, GmpPaymentTypes.Card,
            Payments: new[] { new GmpPaymentLine(GmpPaymentTypes.Card, 941, 62, "GARANTİ BBVA") },
            PaymentsAreComplete: true);
        snap.BindOpenTicket("t1", "oturum-B");

        var r = await t.VoidTicketAsync("t1");

        Assert.Equal(TransportOutcome.Unknown, r.Outcome);
        Assert.Equal(0, r.VoidedAmountMinor);            // ← ÇİVİ: hiçbir şey geri alınmadı
        Assert.Equal(0, r.VoidedPaymentCount);           // ← ÇİVİ
        Assert.Equal(941, r.AttemptedVoidAmountMinor);   // ← ÇİVİ: niyet AYRI alanda
        Assert.Equal(1, r.AttemptedVoidCount);
        Assert.Equal(RestomenumReasons.VoidIncomplete, r.Reason);
    }

    // ── W37: iptal edilen FİŞİN kimliği ─────────────────────────────────────────

    [Fact]
    public async Task Fis_iptalinde_ticketId_BAG_SILINMEDEN_ONCE_okunur()
    {
        // ← ÇİVİ: kimlik `ClearOpenTicketBinding`'den SONRA okunsaydı her zaman null gelirdi —
        // alan var ama hep boş, yani sessizce işe yaramaz. Platform da hangi fişin iptal
        // edildiğini bilemeyip oturum+terminal ile arardı; aynı oturumda önce KAPANMIŞ bir
        // fişin tahsilatı ters kayda gidebilirdi.
        var (t, g, snap) = Kur();
        g.Codes["Start"] = GmpCodes.AlreadyDone;                 // cihazda AÇIK fiş var
        g.Ticket = new GmpTicket(990, 490, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);
        var fisId = snap.BindOpenTicket("t1", "oturum-B");

        var r = await t.VoidTicketAsync("t1");

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal(fisId, r.CancelledTicketId);                // ← ÇİVİ
        Assert.Equal("oturum-B", r.CancelledSaleSessionId);
        Assert.Null(snap.ReadOpenTicketId("t1"));                // bağ GERÇEKTEN silindi
    }

    [Fact]
    public async Task Acik_fis_yoksa_iptalde_ticketId_NULL()
    {
        // İptal edilecek fiş yoksa "iptal edilen fişin kimliği" diye bir şey de yok.
        var (t, g, _) = Kur();                                   // Start OK → açık fiş YOK
        var r = await t.VoidTicketAsync("t1");

        Assert.False(r.TicketWasOpen);
        Assert.Null(r.CancelledTicketId);
    }

    // ── W17(3): BANKA SEÇİMİ ────────────────────────────────────────────────────

    [Fact]
    public async Task Zarftan_gelen_banka_cihaza_GECER()
    {
        // Bir yöntem birden çok bankaya açık olabiliyor (K-27b) ve seçimi KASİYER yapıyor.
        // Ölçüldü (2026-09-07): geçirmediğimizde cihaz kendi seçiyor ve aynı fişte art arda iki
        // denemede FARKLI banka seçti (62 → 46) — yani bugün hangi bankadan çekildiği öngörülemez.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card);

        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990, banka: 62));

        Assert.Equal(62, g.LastPaymentRequest!.Value.BankBkmId);
    }

    [Fact]
    public async Task Zarfta_banka_YOKSA_cihaz_secer()
    {
        // ← ÇİVİ: seçim yapmıyoruz. Listeden birini kendimiz seçmek, işletme iki banka açtığında
        // sessizce hep birine gitmek olurdu ve kimse fark etmezdi.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card);

        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Null(g.LastPaymentRequest!.Value.BankBkmId);
    }

    [Fact]
    public async Task Banka_bacagi_OLUSMADIYSA_kullanilan_banka_BILDIRILMEZ()
    {
        // Bugünkü 2086 hâli: hat yok, bacak oluşmuyor. "Banka bilinmiyor" ile "banka Garanti"
        // ayrı beyanlar — 0 ya da tahmini bir değer paneli yanlış bankaya inandırırdı.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card);   // Payments YOK

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990, banka: 62));

        Assert.Null(r.UsedBankBkmId);          // ← ÇİVİ: İSTEDİĞİMİZ bankayı geri yansıtmıyoruz
    }

    [Fact]
    public async Task Kullanilan_banka_cihazin_YANKISINDAN_bildirilir()
    {
        // İstenen 62 ama cihaz 46 kullandıysa 46 bildirilir — panel gerçeği görsün.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card,
            Payments: new[] { new GmpPaymentLine(GmpPaymentTypes.Card, 990, 46, "AKBANK") });

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990, banka: 62));

        Assert.Equal(46, r.UsedBankBkmId);      // ← ÇİVİ
    }

    [Fact]
    public async Task TUTAR_TUTMASA_DA_banka_YAZILIR()
    {
        // ← ÇİVİ (W79 §4): bu testin ESKİ hâli tersini çiviliyordu — "tutar tutmuyorsa banka
        // yazma". O kural sahada ZARAR VERDİ: banka indirimi tutarı düşürdüğü için (990 istendi,
        // 941 tahsil) koşul tutmadı ve cihaz `bkm 62`yi AÇIKÇA söylediği hâlde deftere NULL
        // yazıldı (ölçüm 2026-09-09, fiş 88/2 ve 88/4). Kod tutarsızlığı GÖRÜYORDU ama onu yalnız
        // "bankayı söyleme" kararında kullandı; tutar uyuşmazlığını kimseye BİLDİRMEDİ.
        // Doğru ayrım: tutar uyuşmazlığı AYRI alanlarda bildirilir, banka YİNE DE yazılır —
        // cihaz bankayı tutardan bağımsız söylüyor.
        var (t, g, snap) = Kur();
        g.Ticket = new GmpTicket(990, 0, 0, GmpPaymentTypes.Card);
        // Banka indirimi fişin TOPLAMINI da düşürür (sahada ölçüldü: 2380 -> 2331), yani fiş
        // 941/941 ile TAM ödenmiş sayılır ve kapanır.
        g.AfterPayment = new GmpTicket(941, 941, 1, GmpPaymentTypes.Card,
            Payments: new[] { new GmpPaymentLine(GmpPaymentTypes.Card, 941, 62, "GARANTİ BBVA") },
            TotalDiscountMinor: 49, PaymentsAreComplete: true);
        g.Ticket = new GmpTicket(990, 0, 0, GmpPaymentTypes.Card);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990, odeme: "pay-1"));

        Assert.Equal(62, r.ClosedTicketPayments![0].BankBkmId);   // ← ÇİVİ: banka DÜŞMEDİ
        Assert.Equal(990, r.RequestedAmountMinor);
        Assert.Equal(941, r.CollectedAmountMinor);                // ← ÇİVİ: tahsil edilen ayrı
        Assert.Equal(49, r.MerchantDiscountMinor);                // ← ÇİVİ: fark bildiriliyor
        Assert.Equal(990, r.ApprovedAmountMinor);                 // ← ÇİVİ: İSTENEN kalır (platform kuralı)
    }

    [Fact]
    public async Task Banka_kimligi_tutar_TUTUYORSA_tasinir()
    {
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card, PaymentsAreComplete: true);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Card,
            Payments: new[] { new GmpPaymentLine(GmpPaymentTypes.Card, 990, 62, "GARANTİ BBVA") });

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Card,
            oturum: "oturum-A", satisToplam: 990, odeme: "pay-1"));

        Assert.Equal(62, r.ClosedTicketPayments![0].BankBkmId);
    }

    // ── W73: CİHAZIN FİŞE BASTIĞI SATIRLAR ──────────────────────────────────────

    /// <summary>Sahadan okunan GERÇEK yazıcı kopyası (F70-R, 2026-09-09 15:27:01).</summary>
    private static readonly string[] SahaKopyasi =
    {
        "Espresso               %10      *9,90 ",
        "İNDİRİM                         *0,99-",
    };

    [Fact]
    public async Task Fise_basilan_satirlar_GUNLUGE_yazilir()
    {
        // ← ÇİVİ: 2026-09-09'da "indirim fişte çıktı mı" sorusunu saatlerce cevaplayamadık; elimizde
        // yalnız toplamlar vardı ve kâğıt kullanıcıdaydı. Oysa cihaz bastığı satırları ZATEN
        // gönderiyordu (`OptionFlags(Reload)` → `ECHO_PRINTER`) ve `Map` onları atıyordu.
        // Bu test o satırların kaybolmamasını çiviliyor.
        var (t, gmp, _) = Kur();
        var loglar = new List<(string, object?)>();
        var tt = new GmpTerminalTransport(gmp, log: (m, o) => loglar.Add((m, o)));
        // Kapanış okuması `GetTicket`ten geliyor → sahtede `Ticket` alanı.
        gmp.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash,
            PaymentsAreComplete: true, PrinterLines: SahaKopyasi, PrinterLineCount: 2);
        gmp.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);

        await tt.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash));

        var veri = loglar.LastOrDefault(l => l.Item1.Contains("fişe basılan satırlar")).Item2;
        Assert.NotNull(veri);

        // ← ÇİVİ (dikiş): host TAM OLARAK bunu basıyor — `LogInformation("{Msg} {Arg}", m, a)`,
        // yani nesnenin `ToString()`'i. Bu test daha önce `satirlar` ALANINI okuyordu ve yeşildi;
        // sahada ise `satirlar = System.Collections.Generic.List`1[System.String]` basıldı
        // (2026-09-09 16:07:22.693). Alanı okumak, hostun ne yazdığını ÖLÇMEZ.
        var metin = veri!.ToString()!;
        Assert.DoesNotContain("System.Collections", metin);
        foreach (var satir in SahaKopyasi)
            Assert.Contains(satir, metin);              // ← ÇİVİ: BİREBİR, kırpılmadan, sondaki boşlukla
        Assert.False((bool)veri.GetType().GetProperty("eksik")!.GetValue(veri)!);
    }

    [Fact]
    public async Task EKSIK_kopya_EKSIK_diye_isaretlenir()
    {
        // ← ÇİVİ: "üç satır bastı" ile "üç satırını gördük" AYRI şeyler. Eksik bir kopyayı tam
        // sanmak yanlış teşhis üretir — aynı ayrım ödeme satırlarında bir kez ısırdı.
        var (t, gmp, _) = Kur();
        var loglar = new List<(string, object?)>();
        var tt = new GmpTerminalTransport(gmp, log: (m, o) => loglar.Add((m, o)));
        gmp.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash,
            PaymentsAreComplete: true, PrinterLines: new[] { SahaKopyasi[0] }, PrinterLineCount: 5);
        gmp.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);

        await tt.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash));

        var veri = loglar.LastOrDefault(l => l.Item1.Contains("fişe basılan satırlar")).Item2;
        Assert.True((bool)veri!.GetType().GetProperty("eksik")!.GetValue(veri)!);
        Assert.Equal(5, veri.GetType().GetProperty("cihazinBildirdigi")!.GetValue(veri));
    }

    [Fact]
    public async Task Satir_YOKSA_gunluge_de_YAZILMAZ()
    {
        // Gürültü yok: `null` = "cihaz göndermedi". Boş bir satır dizisi loglamak, her satışta
        // anlamsız bir kayıt biriktirirdi.
        var (t, gmp, _) = Kur();
        var loglar = new List<(string, object?)>();
        var tt = new GmpTerminalTransport(gmp, log: (m, o) => loglar.Add((m, o)));
        gmp.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);
        gmp.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash, PaymentsAreComplete: true);

        await tt.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash));

        Assert.DoesNotContain(loglar, l => l.Item1.Contains("fişe basılan satırlar"));
    }

    [Theory]
    [InlineData("Espresso  *9,90 ", "Espresso  *9,90 ")]                 // tutar ayraçlı → dokunulmaz
    [InlineData("KART: 1234", "KART: 1234")]                             // 4 hane → dokunulmaz
    [InlineData("PAN 4508034508034508", "PAN ***")]                      // 16 hane → maskelenir
    [InlineData("SN 123456", "SN ***")]                                  // 6 hane → sınır
    [InlineData("SN 12345", "SN 12345")]                                 // 5 hane → sınırın altı
    public void Yazici_satirinda_ARDISIK_RAKAM_maskelenir(string girdi, string beklenen)
    {
        // ← ÇİVİ: bu satırlar artık GÜNLÜĞE gidiyor. Fiş satırlarında kart verisi beklenmiyor —
        // ama "beklenmiyor" bir güvence değil: banka uygulamasının fişe ne yazacağını biz
        // belirlemiyoruz. Kural sarmalayıcıda (P/Invoke, testsiz) duruyordu; mutasyon orada
        // satırları düşürünce HİÇBİR test kırılmadı. Karar Core'a taşındı.
        Assert.Equal(beklenen, PrinterCopy.Maskele(girdi));
    }

    [Fact]
    public void Yazdirilabilir_satirlari_SONDAKI_BOSLUKLA_gosterir()
    {
        // ← ÇİVİ: fiş satırları sütun hizalı ve sondaki boşluk anlamlı ("…*9,90 "). Çıplak
        // birleştirmede o boşluk kaybolur; kâğıtla karşılaştıran kişi hizayı yanlış okur.
        var m = PrinterCopy.Yazdirilabilir(SahaKopyasi);

        Assert.Contains("1│Espresso               %10      *9,90 │", m);
        Assert.Contains("2│İNDİRİM                         *0,99-│", m);
    }

    [Fact]
    public void MASKE_render_edilmis_metinde_de_DURUYOR()
    {
        // ← ÇİVİ: maske `Read`'de uygulanıyor, render'da DEĞİL — kural tek yerde durmalı.
        // Ama "tek yerde duruyor" ile "günlüğe maskeli çıkıyor" ayrı iddialar; ölçülen ikincisi.
        // Kartlı fişte yazıcı kopyasında maskeli PAN olabilir ve bu metin artık DİSKE yazılıyor.
        var okunan = PrinterCopy.Read(new[] { "PAN 4508034508034508", "TUTAR *9,90 " }, 2);

        var metin = PrinterCopy.Yazdirilabilir(okunan);

        Assert.DoesNotContain("4508034508034508", metin);
        Assert.Contains("PAN ***", metin);
        Assert.Contains("TUTAR *9,90 ", metin);      // tutar ayraçlı → dokunulmadı
    }

    [Fact]
    public void Yazdirilabilir_GELMEDI_ile_BOS_GELDI_ayrimini_KORUR()
    {
        // ← ÇİVİ: "cihaz göndermedi" ile "gönderdi ama satır yok" ayrı olgular. Aynı metne
        // indirgemek, teşhiste yanlış yere baktırır — bu ayrımı bu kod tabanında üç kez kaybettik.
        Assert.NotEqual(PrinterCopy.Yazdirilabilir(null), PrinterCopy.Yazdirilabilir(Array.Empty<string>()));
        Assert.DoesNotContain("│", PrinterCopy.Yazdirilabilir(Array.Empty<string>()));
    }

    [Fact]
    public void Yazici_kopyasi_YALNIZ_dolu_pencereyi_okur()
    {
        // ← ÇİVİ: dizi kapasite kadar uzun olabilir; okunacak olan cihazın DOLDURDUĞU kadarıdır.
        // Fazlasını okumak boş satırları "fişte var" saymak olurdu.
        var dizi = new string?[] { "A", "B", null, null };
        Assert.Equal(new[] { "A", "B" }, PrinterCopy.Read(dizi, 2));
        Assert.Equal(new[] { "A" }, PrinterCopy.Read(dizi, 1));
        Assert.Empty(PrinterCopy.Read(dizi, 0)!);
        Assert.Equal(new[] { "A", "B" }, PrinterCopy.Read(dizi, 99));   // taşma kırpılır
    }

    [Fact]
    public void Yazici_kopyasi_GELMEDIYSE_null_BOSTAN_ayri()
    {
        // ← ÇİVİ: "gelmedi" ile "geldi ama satır yok" ayrı olgular; ikisini eşitlemek, cihazın
        // hiç göndermediği bir kopyayı "fiş boştu" diye okumak olurdu.
        Assert.Null(PrinterCopy.Read(null, 3));
        Assert.NotNull(PrinterCopy.Read(Array.Empty<string?>(), 0));
    }

    // ── W74: BAYRAK ADLARI DLL BİTLERİYLE HİZALI ────────────────────────────────

    [Fact]
    public void Echo_bayraklari_DLL_bitleriyle_AYNI()
    {
        // ← ÇİVİ: adlar bir zamanlar TERS eşlenmişti — `ItemDetails` bit 1'e, `PaymentDetails`
        // bit 2'ye bakıyordu; DLL'de tam tersi (`GmpInterop.cs`: ECHO_PAYMENT_DETAILS = 1<<1,
        // ECHO_ITEM_DETAILS = 1<<2). Her yerde üçü birden gönderildiği için zararsızdı, ama biri
        // tek başına kullansaydı istediğinin TERSİNİ alır ve bunu hiçbir şey söylemezdi.
        Assert.Equal(1UL, (ulong)GmpEchoFlags.Printer);          // ECHO_PRINTER        = 1<<0
        Assert.Equal(2UL, (ulong)GmpEchoFlags.PaymentDetails);   // ECHO_PAYMENT_DETAILS= 1<<1
        Assert.Equal(4UL, (ulong)GmpEchoFlags.ItemDetails);      // ECHO_ITEM_DETAILS   = 1<<2
    }

    [Fact]
    public void Reload_UCU_BIRDEN_gonderir_davranis_DEGISMEDI()
    {
        // ← ÇİVİ: W74 yalnız ADLARI düzeltti. `Reload` hâlâ 0b111 — sahadaki davranış birebir aynı.
        Assert.Equal(7UL, (ulong)GmpEchoFlags.Reload);
    }

    // ── W70: İNDİRİM CİHAZA SATIR TOPLAMI OLARAK GİDER ──────────────────────────

    [Theory]
    [InlineData(1, 100, 100)]     // adet 1 → satır = birim
    [InlineData(2, 100, 200)]     // adet 2 → 2 × 100
    [InlineData(3,  50, 150)]
    public async Task Indirim_SATIR_TOPLAMI_olarak_gonderilir(int adet, long birimIndirim, long beklenenSatir)
    {
        // ← ÇİVİ: cihazın `promotion.amount` alanı SATIR toplamıdır — yoklamada ölçüldü
        // (2026-09-09 14:57, adım 6): `count 2, amount 990, promotion 100` → fiş toplamı
        // 2×990−100 arttı, indirim toplamı 100 oldu. Birim bazlı olsaydı 200 olurdu.
        // `FiscalLine.LineDiscountMinor` ise BİRİM başına; çarpmayı unutmak indirimi adet katı
        // kadar EKSİK uygulamak, yani müşteriden fazla tahsilat demek.
        var (t, gmp, _) = Kur();
        gmp.Ticket = new GmpTicket(990 * adet, 0, 0, 0);

        await t.SaleAsync(Req(amount: 100, lines: new[]
        {
            new FiscalLine("prod1", "Kahve", adet, 990, 20m, DepartmentNo: 1, LineDiscountMinor: birimIndirim),
        }));

        Assert.Equal(beklenenSatir, gmp.ItemSales[0].DiscountMinor);
        Assert.Equal(990, gmp.ItemSales[0].UnitPriceMinor);   // birim fiyat DEĞİŞMEZ
        Assert.Equal(adet, gmp.ItemSales[0].Quantity);
    }

    [Fact]
    public async Task BOLUNMUS_satirlarda_her_alt_satir_KENDI_toplamini_tasir()
    {
        // ← ÇİVİ: kuruş dağıtımı satırı ikiye bölebiliyor ve alt satırların adetleri FARKLI.
        // Her birine kendi `birim × adet`i gitmeli; toplamları platformun gönderdiği satır
        // indirimini TAM vermeli, yoksa fişteki indirim birkaç kuruş sapar.
        var (t, gmp, _) = Kur();
        gmp.Ticket = new GmpTicket(1000, 0, 0, 0);

        await t.SaleAsync(Req(amount: 100, lines: new[]
        {
            new FiscalLine("p", "Ürün", 2, 333, 20m, DepartmentNo: 1, LineDiscountMinor: 33),
            new FiscalLine("p", "Ürün", 1, 334, 20m, DepartmentNo: 1, LineDiscountMinor: 34),
        }));

        Assert.Equal(66, gmp.ItemSales[0].DiscountMinor);    // 2 × 33
        Assert.Equal(34, gmp.ItemSales[1].DiscountMinor);    // 1 × 34
        Assert.Equal(100, gmp.ItemSales.Sum(i => i.DiscountMinor));   // ← ÇİVİ: toplam TAM
    }

    [Fact]
    public async Task Indirim_YOKKEN_cihaza_SIFIR_gider_promotion_kurulmaz()
    {
        // ← ÇİVİ: `GmpWrapper` `promotion`ı yalnız `DiscountMinor > 0` iken kuruyor. Sıfır
        // gitmezse alan `null` kalır — bugünkü davranışın ta kendisi (izde `Type:Null`).
        // Boş bir promotion nesnesi göndermek cihaza "0 TL indirim var" demek olurdu.
        var (t, gmp, _) = Kur();

        await t.SaleAsync(Req());

        Assert.Equal(0, gmp.ItemSales[0].DiscountMinor);
        Assert.Equal("", gmp.ItemSales[0].DiscountText);
    }

    [Fact]
    public async Task TAM_IKRAM_cihaza_satir_tutarina_ESIT_gider()
    {
        // ← ÇİVİ: yoklamada ölçüldü — cihaz `promotion == kalem tutarı`nı KABUL ediyor (rc 0) ve
        // fiş toplamı hiç artmıyor. Sınırı `<` yapmak ikramı imkânsız kılardı.
        var (t, gmp, _) = Kur();
        gmp.Ticket = new GmpTicket(0, 0, 0, 0);

        await t.SaleAsync(Req(amount: 100, lines: new[]
        {
            new FiscalLine("p", "İkram", 2, 990, 20m, DepartmentNo: 1, LineDiscountMinor: 990),
        }));

        Assert.Equal(1980, gmp.ItemSales[0].DiscountMinor);
        Assert.Equal(1980, gmp.ItemSales[0].UnitPriceMinor * gmp.ItemSales[0].Quantity);
    }

    [Theory]
    [InlineData(0, false)]      // ← ÇİVİ: SIFIRDA promotion KURULMAZ
    [InlineData(-1, false)]     // negatif zaten kapıda elenir; burada da kurulmaz
    [InlineData(1, true)]
    [InlineData(990, true)]
    public void Promotion_YALNIZ_indirim_varken_kurulur(long indirim, bool beklenen)
    {
        // ← ÇİVİ: boş bir `promotion` göndermek cihaza "0 TL indirim var" demek ve fişe satır
        // bastırabilir. Bu karar sarmalayıcıda (P/Invoke, birim testi YOK) duruyordu; mutasyon
        // orada `> 0`'ı `>= 0` yapınca HİÇBİR test kırılmadı. Karar Core'a taşındı ki çivilensin.
        Assert.Equal(beklenen, new GmpItem("x", 990, 1, 1, indirim).HasDiscount);
    }

    [Fact]
    public void Fis_metni_BOS_ve_bilincli()
    {
        // ← ÇİVİ: kullanıcı kararı — "fişte ne yazacağını sen belirleyemezsin, terminal otomatik
        // çıkarır". Platform da metin göndermiyor. Buraya uydurma bir cümle koymak, mali belgeye
        // bizim yazdığımız bir metni bastırmak olurdu.
        Assert.Equal("", GmpItemDefaults.DiscountText);
    }

    // ── W90: BANKA İNDİRİMİ DEVAM BACAĞINI KİLİTLEMESİN ────────────────────────────
    //
    // Saha (2026-09-09 18:11, fiş 88/11): kart bacağında GARANTİ BBVA fişe "İŞYERİ İND." bastı ve
    // cihazın fiş toplamını 2380 → **2331** düşürdü. Platform ise kalem listesinden **2380**
    // göndermeye devam ediyor. `DevamDogrula` ikisini birebir kıyasladığı için ikinci bacak
    // `TICKET_SALE_MISMATCH` ile reddedildi ve **fiş tamamlanamaz hâle geldi**.
    //
    // Bu yalnız bir kısıt değil: üreticinin kendi kurtarma akışı (GMP3OlumsuzDurumYönetimi §2
    // adım 7) bu durumda "kalan ödemeler tamamlanır" diyor — yani bizim kontrolümüz üreticinin
    // önerdiği çıkışı tıkıyordu.
    //
    // Tolerans DAR: fark, cihazın KENDİ bildirdiği indirime tam eşit olmalı. "Kasiyer kalem
    // ekledi/sildi" koruması bozulmuyor — aşağıdaki ikinci test onu çiviliyor.

    private static (GmpTerminalTransport, FakeGmp, FakeSnapshots) DevamKur(
        long cihazToplam, long cihazIndirim)
    {
        var (t, g, snap) = Kur();
        g.Codes["Start"] = GmpCodes.AlreadyDone;          // cihazda AÇIK fiş var
        snap.BindOpenTicket("t1", "oturum-A");            // ve o fiş BU satışa ait
        g.Ticket = new GmpTicket(cihazToplam, PaidAmountMinor: 941, PaymentCount: 1,
            GmpPaymentTypes.Card, TotalDiscountMinor: cihazIndirim);
        g.AfterPayment = new GmpTicket(cihazToplam, cihazToplam, 2, GmpPaymentTypes.Cash,
            PaymentsAreComplete: true, TotalDiscountMinor: cihazIndirim);
        return (t, g, snap);
    }

    [Fact]
    public async Task BANKA_INDIRIMI_kadar_fark_devam_bacagini_ENGELLEMEZ()
    {
        // ← ÇİVİ: 2331 + 49 = 2380. Cihazın indirimi farkı TAM açıklıyorsa devam edilir.
        var (t, gmp, _) = DevamKur(cihazToplam: 2331, cihazIndirim: 49);

        var r = await t.SaleAsync(Req(amount: 1390, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 2380));

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Contains("Payment", gmp.Calls);            // ← ÇİVİ: terminale GERÇEKTEN gidildi
        Assert.DoesNotContain("TicketHeader", gmp.Calls); // devam yolu: yeni fiş açılmadı
    }

    [Fact]
    public async Task INDIRIM_YOKKEN_ayni_fark_HALA_REDDEDILIR()
    {
        // ← ÇİVİ: toleransın tek meşru kaynağı cihazın bildirdiği indirimdir. İndirim 0 iken
        // 49 kuruşluk fark "kalemler değişti" demektir ve devam edilmemelidir. Bu test olmasaydı
        // düzeltme, korumanın tamamını sessizce kaldırırdı.
        var (t, gmp, _) = DevamKur(cihazToplam: 2331, cihazIndirim: 0);

        var r = await t.SaleAsync(Req(amount: 1390, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 2380));

        Assert.Equal(TransportOutcome.Declined, r.Outcome);
        Assert.Contains("TICKET_SALE_MISMATCH", r.ProviderResultCode);
        Assert.DoesNotContain("Payment", gmp.Calls);      // ← ÇİVİ: terminale HİÇ gidilmedi
    }

    [Fact]
    public async Task TAM_ESITLIK_eskisi_gibi_gecer()
    {
        // Gerileme çivisi: indirimsiz normal devam bacağı etkilenmemeli.
        var (t, gmp, _) = DevamKur(cihazToplam: 2380, cihazIndirim: 0);

        var r = await t.SaleAsync(Req(amount: 1390, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 2380));

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Contains("Payment", gmp.Calls);
    }

    private static SaleRequest Req(long amount = 3000, int paymentType = GmpPaymentTypes.Card,
        IReadOnlyList<FiscalLine>? lines = null, string? oturum = null, long? satisToplam = null,
        string komut = "c1", string odeme = "p1", int? banka = null) =>
        new(komut, odeme, "t1", amount, "TRY", 2, "prov",
            lines ?? new[] { new FiscalLine("prod1", "Kahve", 1, amount, 20m, DepartmentNo: 1) },
            paymentType, oturum, satisToplam, banka);

    /// <summary>Bellek-içi görüntü deposu. Gerçekte <see cref="CommandStore"/> (disk) kullanılır —
    /// süreç kart penceresinde ölürse görüntünün hayatta kalması şart.</summary>
    private sealed class FakeSnapshots : ITicketSnapshotStore
    {
        private readonly Dictionary<string, (long, long, int, string?)> _d = new();
        public void SaveSnapshot(string commandId, long total, long paid, int count,
            string? saleSessionId = null, long? now = null)
            => _d[commandId] = (total, paid, count, saleSessionId);
        public (long TotalMinor, long PaidMinor, int PaymentCount, string? SaleSessionId)? ReadSnapshot(string commandId)
            => _d.TryGetValue(commandId, out var v) ? v : null;

        /// <summary>Açık fiş ↔ satış oturumu bağı (gerçekte diskte, testte bellekte).</summary>
        private readonly Dictionary<string, string> _bag = new();
        private readonly Dictionary<string, string> _fisId = new();
        /// <summary>Kaç kez YENİ kimlik üretildi — "devam yolunda üretilmez" çivisi bunu okur.</summary>
        public int UretilenKimlik;

        public string BindOpenTicket(string terminalId, string saleSessionId, long? now = null)
        {
            // Gerçek depo gibi: kimlik yalnız fiş GERÇEKTEN yeniyse üretilir.
            if (!_fisId.TryGetValue(terminalId, out var f)
                || !_bag.TryGetValue(terminalId, out var o) || o != saleSessionId)
            {
                f = "tkt_" + (++UretilenKimlik);
                _fisId[terminalId] = f;
            }
            _bag[terminalId] = saleSessionId;
            return f;
        }
        public string? ReadOpenTicketBinding(string terminalId)
            => _bag.TryGetValue(terminalId, out var v) ? v : null;
        public string? ReadOpenTicketId(string terminalId)
            => _fisId.TryGetValue(terminalId, out var v) ? v : null;
        public void ClearOpenTicketBinding(string terminalId)
        {
            KapanisSirasi.Add("clear:" + terminalId);
            _bag.Remove(terminalId); _fisId.Remove(terminalId);
        }

        public IReadOnlyList<(string TerminalId, string? SaleSessionId, string? TicketId)> AllOpenTicketBindings()
            => _bag.Select(x => (x.Key, (string?)x.Value,
                _fisId.TryGetValue(x.Key, out var f) ? f : null)).ToList();

        // ── W105: TAMAMLANMAYI BEKLEYEN FİŞ ───────────────────────────────────────
        /// <summary>İşaretlenen fişler: fiş → (adım, kod, deneme). Sıra da tutulur.</summary>
        public Dictionary<string, (string? Adim, string? Kod, int Deneme)> Bekleyen { get; } = new();
        public List<string> BekleyenSirasi { get; } = new();

        public void MarkTicketNeedsFinish(string ticketId, string terminalId, string? saleSessionId,
            string? step, string? code, int ilkBeklemeSn = 15, long? now = null)
        {
            BekleyenSirasi.Add("mark:" + ticketId);
            // Gerçek deponun `ON CONFLICT` davranışı: ikinci işaretleme deneme sayacını
            // SIFIRLAMAZ, yalnız hata alanlarını tazeler.
            var deneme = Bekleyen.TryGetValue(ticketId, out var v) ? v.Deneme : 0;
            Bekleyen[ticketId] = (step, code, deneme);
            _bekleyenTerminal[ticketId] = (terminalId, saleSessionId);
            // Gerçek depo `marked_at`'ı çakışmada TAZELEMEZ — sahte de tazelemeyecek.
            if (!_bekleyenIsaret.ContainsKey(ticketId))
                _bekleyenIsaret[ticketId] = now ?? 0;
        }

        private readonly Dictionary<string, (string Terminal, string? Oturum)> _bekleyenTerminal = new();
        private readonly Dictionary<string, long> _bekleyenIsaret = new();

        /// <summary>W125: park boşaltma çağrıları — cihazın düzeldiği ANIN çivisi.</summary>
        public int ParkBosaltildi;
        public void UndeferPendingFinishes(long? now = null) => ParkBosaltildi++;

        /// <summary>W145: fişe yazılan mali fiş numarası.</summary>
        public readonly Dictionary<string, uint> MaliNumaralar = new();
        public void SetFiscalReceiptNo(string ticketId, uint no) => MaliNumaralar[ticketId] = no;

        public IReadOnlyList<PendingFinishRow> PendingFinishes(long? now = null)
            => Bekleyen.Select(x => new PendingFinishRow(x.Key,
                _bekleyenTerminal.TryGetValue(x.Key, out var t) ? t.Terminal : "t1",
                _bekleyenTerminal.TryGetValue(x.Key, out var o) ? o.Oturum : null,
                x.Value.Deneme, x.Value.Adim, x.Value.Kod,
                _bekleyenIsaret.TryGetValue(x.Key, out var i) ? i : 0)).ToList();

        /// <summary>Son denemede geçirilen aralık — ucuz/pahalı ayrımının çivisi bunu okur.</summary>
        public TimeSpan? SonAralik;
        public int MarkFinishAttempt(string ticketId, string? step, string? code, long? now = null,
            TimeSpan? sonrakiAralik = null)
        {
            SonAralik = sonrakiAralik;
            if (!Bekleyen.TryGetValue(ticketId, out var v)) return 0;
            Bekleyen[ticketId] = (step, code, v.Deneme + 1);
            return v.Deneme + 1;
        }

        /// <summary>W120: kâğıt uyarısı — `null` = uyarı yok.</summary>
        public (long GorulmeMs, string? Adim)? KagitUyarisi;
        public List<string> KagitSirasi { get; } = new();
        public void SetPaperWarning(string terminalId, bool var, string? step = null, long? now = null)
        {
            KagitSirasi.Add(var ? "ac:" + step : "kapat");
            // Gerçek depo gibi: İLK görülme saati korunur, yalnız adım tazelenir.
            KagitUyarisi = var
                ? (KagitUyarisi?.GorulmeMs ?? (now ?? 1_700_000_000_000), step)
                : null;
        }
        public (long GorulmeMs, string? Adim)? ReadPaperWarning(string terminalId) => KagitUyarisi;

        public void ClearPendingFinish(string ticketId)
        {
            BekleyenSirasi.Add("clearPending:" + ticketId);
            Bekleyen.Remove(ticketId);
            _bekleyenIsaret.Remove(ticketId);
        }

        /// <summary>Ertelenen fişler — sahtede zaman yürümediği için yalnız KAYDEDİLİR.</summary>
        public Dictionary<string, TimeSpan> Ertelenen { get; } = new();
        public void DeferFinish(string ticketId, TimeSpan sure, long? now = null)
        {
            BekleyenSirasi.Add("defer:" + ticketId);
            Ertelenen[ticketId] = sure;
        }

        /// <summary>Fiş → ödemeler (bizim paymentId'lerimiz).</summary>
        public Dictionary<string, List<TicketPaymentRow>> Odemeler { get; } = new();
        /// <summary>W79: kaydedilen TAHSİL tutarları — defter değişmezinin çivisi bunu okur.</summary>
        public Dictionary<string, long?> Tahsil { get; } = new();

        public void RecordTicketPayment(string ticketId, string paymentId, long amountMinor,
            int methodType, int? bankBkmId, long? now = null, long? collectedMinor = null)
        {
            if (!Odemeler.TryGetValue(ticketId, out var l)) Odemeler[ticketId] = l = new();
            if (l.Any(x => x.PaymentId == paymentId)) return;      // (fiş, ödeme) tekil
            Tahsil[paymentId] = collectedMinor;
            l.Add(new TicketPaymentRow(paymentId, amountMinor, methodType, bankBkmId));
        }
        public IReadOnlyList<TicketPaymentRow> ReadTicketPayments(string ticketId)
            => Odemeler.TryGetValue(ticketId, out var l) ? l : Array.Empty<TicketPaymentRow>();

        /// <summary>Kapanmış-bildirilmemiş fişler + hangi sırayla yazıldıkları.</summary>
        public List<ClosedTicketRow> Kapananlar { get; } = new();
        public List<string> KapanisSirasi { get; } = new();
        public void MarkTicketClosed(string ticketId, string terminalId, string? saleSessionId,
            long? totalMinor, long? paidMinor, long? now = null)
        {
            KapanisSirasi.Add("mark:" + ticketId);
            if (Kapananlar.Any(x => x.TicketId == ticketId)) return;
            Kapananlar.Add(new ClosedTicketRow(ticketId, terminalId, saleSessionId, totalMinor, paidMinor));
        }
        public IReadOnlyList<ClosedTicketRow> PendingClosedTickets(long? enGecKapanis = null) => Kapananlar;
        public void ConfirmTicketClosed(string ticketId, long? now = null)
            => Kapananlar.RemoveAll(x => x.TicketId == ticketId);
    }

    private static (GmpTerminalTransport, FakeGmp, FakeSnapshots) Kur()
    {
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        return (new GmpTerminalTransport(g, snap), g, snap);
    }

    [Fact]
    public async Task ECHO_FIRLATIRSA_SATIS_BOZULMAZ_ve_KAPANIS_DEFTERE_YAZILIR()
    {
        // ← ÇİVİ: BAĞIMSIZ DENETİM BUNU YAKALADI ve ben yazmıştım.
        //
        // `Echo` çağrısı ÖNCE `Close` ile `MarkTicketClosed` ARASINDA duruyordu. Yani para
        // ALINMIŞ, mali fiş KESİLMİŞ, ama kalıcı "kapandı" kaydı henüz yazılmamışken cihaza
        // bir çağrı daha yapılıyordu. O çağrı fırlatsaydı:
        //   · satış istisnayla düşer, kasa HTTP 500 alır
        //   · kapanış deftere HİÇ yazılmaz → platform kapanışı öğrenemez → masa kapanmaz
        //   · açık fiş bağı silinmez → aynı oturumun sonraki satışı KAPANMIŞ fişin kimliğini
        //     devralır
        //
        // `FP3_Echo` yanıtı JSON'dan çözülüyor ve W142 ile sayaç alanları eklendi; beklenmedik
        // bir değer ayrıştırıcıyı fırlatabilir. Bilgi amaçlı bir okumanın tamamlanmış bir satışı
        // patlatması kabul edilemez.
        var (t, g, snap) = Kur();
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.EchoPatlat = new InvalidOperationException("JSON bozuk");

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(TransportOutcome.Approved, r.Outcome);   // satış BOZULMADI
        Assert.Equal("CLOSED", r.TicketState);
        Assert.Null(r.FiscalReceiptNo);                       // numara yok, uydurulmadı
        // ⚠️ ASIL ÇİVİ: kapanış DEFTERE YAZILDI ve bağ SİLİNDİ.
        Assert.Single(snap.Kapananlar);
        Assert.Null(snap.ReadOpenTicketBinding("t1"));
    }

    [Fact]
    public async Task MALI_FIS_NUMARASI_KALICI_KAYDA_yazilir()
    {
        // ← ÇİVİ: denetim "numara hiçbir yere yazılmıyor, yalnız 14 gün saklanan log'a
        // düşüyor" dedi ve haklıydı. Commit "kaydediliyor" diyordu ama denetim sorduğunda
        // cevap verecek kalıcı bir yer YOKTU.
        var (t, g, snap) = Kur();
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.EchoDurumu = new EchoDurum(0x1D, ZNo: 92, FisNo: 4, EkuNo: 1, Handle: 0);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        var fisId = r.TicketId!;
        Assert.Equal(3u, snap.MaliNumaralar[fisId]);   // okunan 4 → kesilen 3, KALICI
    }

    [Fact]
    public async Task FIS_KAPANINCA_MALI_FIS_NUMARASI_kaydedilir_ve_BIR_EKSIGI_alinir()
    {
        // ← ÇİVİ: bir denetim "şu satış hangi mali fiş?" diye sorduğunda cevabımız YOKTU.
        // `tkt_…` bizim ürettiğimiz etiket, cihaz onu bilmiyor; kâğıttaki numara cihazın.
        //
        // ⚠️ ÇIKARMA BİLEREK: `FisNo` SONRAKİ numaradır. Sahada ölçüldü (2026-09-12 01:05→01:08):
        // satıştan önce 3 okundu, KÂĞIDA BASILAN numara 3 çıktı, satıştan sonra 4 okundu.
        // Çıkarmayı unutmak, her fişi bir fazla numarayla kaydetmek olurdu.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.EchoDurumu = new EchoDurum(0x1D, ZNo: 92, FisNo: 4, EkuNo: 1, Handle: 0);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("CLOSED", r.TicketState);
        Assert.Equal(3u, r.FiscalReceiptNo);      // okunan 4 → kesilen fiş 3
    }

    [Fact]
    public async Task FIS_ACIK_KALIRSA_MALI_NUMARA_YAZILMAZ()
    {
        // ← ÇİVİ: numara mali KESİNLEŞMEDE tüketiliyor. Fiş açıkken bir numara kaydetmek,
        // henüz kesilmemiş bir belgeye numara atamak olurdu — ve o numarayı başka bir fiş alır.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(2380, 990, 1, GmpPaymentTypes.Cash);   // kısmi

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 2380));

        Assert.Equal("OPEN", r.TicketState);
        Assert.Null(r.FiscalReceiptNo);
    }

    [Fact]
    public async Task ECHO_OKUNAMAZSA_MALI_NUMARA_UYDURULMAZ()
    {
        // ← ÇİVİ: "ölçülemedi" ile "numara 0" ayrı şeyler. Yanlış bir mali numara kaydetmek,
        // hiç kaydetmemekten KÖTÜDÜR — denetimde var olmayan bir belgeye işaret eder.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.Codes["Echo"] = GmpCodes.PortNotOpen;

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("CLOSED", r.TicketState);    // satış BOZULMAZ
        Assert.Null(r.FiscalReceiptNo);           // ama numara UYDURULMAZ
    }

    [Fact]
    public async Task ECHO_SIFIR_DONERSE_MALI_NUMARA_YAZILMAZ()
    {
        // ← ÇİVİ: bloke cihaz `retcode 0` ile TAMAMEN SIFIR dönebiliyor (2026-09-10 02:05:48).
        // Sıfırdan bir çıkarmak `4294967295` üretirdi — sessiz ve saçma bir mali numara.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.EchoDurumu = new EchoDurum(0, 0, 0, 0, 0);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Null(r.FiscalReceiptNo);
    }

    [Fact]
    public async Task ECHO_MALI_SAYACLARI_TASIR_cihaz_zaten_gonderiyordu()
    {
        // ← ÇİVİ: bu alanlar cihazdan HER Echo'da geliyordu ve `ST_ECHO` sınıfında karşılıkları
        // olmadığı için Newtonsoft onları SESSİZCE DÜŞÜRÜYORDU. Ham JSON'dan ölçüldü
        // (2026-09-11 00:11:26): {"status":29,…,"EkuNo":1,"ZNo":91,"FisNo":2,"Handle":0}
        //
        // Yani "FNo'yu okuyamıyoruz" dediğim şey aslında okunuyordu — taşınmıyordu. Bu,
        // "görünmez eksik alan" kusurunun ta kendisi: gövde doğru görünür, bir alan düşmüştür
        // ve ancak karşı tarafın davranışından anlaşılır.
        var loglar = new List<(string, object?)>();
        var g = new FakeGmp { EchoDurumu = new EchoDurum(0x1D, ZNo: 91, FisNo: 2, EkuNo: 1, Handle: 0) };
        var t = new GmpTerminalTransport(g, null, (m, o) => loglar.Add((m, o)));

        Assert.True(await t.EchoAsync());

        var veri = loglar.Single(x => x.Item1 == "[gmp] echo").Item2!.ToString()!;
        Assert.Contains("zNo = 91", veri);
        Assert.Contains("fisNo = 2", veri);
        Assert.Contains("ekuNo = 1", veri);
        // Asılı tanıtıcı da bedavaya geliyor — üreticinin `FP3_Start` yerine önerdiği bilgi.
        Assert.Contains("asiliTanitici = 0", veri);
    }

    [Fact]
    public async Task ECHO_SIFIRLARI_YORUMLANMAZ_sadece_tasinir()
    {
        // ← ÇİVİ: bloke cihaz `retcode 0` ile TAMAMEN SIFIR dönebiliyor (2026-09-10 02:05:48'de
        // ölçüldü) ve aynı desen `FP3_VoidPayment` hatasında "boş yanıt" demekti. Sıfırı
        // "sayaç sıfır" diye yorumlayan bir karar, boş yanıtı ölçüm sanardı.
        //
        // Bu yüzden burada sınanan şey bir KARAR değil: sıfırlar da olduğu gibi TAŞINIYOR mu.
        var loglar = new List<(string, object?)>();
        var g = new FakeGmp { EchoDurumu = new EchoDurum(0, 0, 0, 0, 0) };
        var t = new GmpTerminalTransport(g, null, (m, o) => loglar.Add((m, o)));

        Assert.True(await t.EchoAsync());

        var veri = loglar.Single(x => x.Item1 == "[gmp] echo").Item2!.ToString()!;
        Assert.Contains("status = 0x00000000", veri);
        Assert.Contains("zNo = 0", veri);
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
        // ⚠️ Sıraya `Payment` ile baskı arasında bir OptionFlags+GetTicket EKLENDİ (K-26): fiş
        // kapanınca deftere yazılacak ödeme satırları kapatMADAN ÖNCE okunmalı — `Close` sonrası
        // fiş erişilemez olur ve o satırların kaynağı kaybolur.
        // W50: `PrintUserMessage` zincirden ÇIKARILDI — BOŞ mesajla çağrılıyordu ve 14 kapanış
        // üzerinden 353 ms tutuyordu; sertifikalı referansın iki kapanış yolunda hiç yok.
        // W144: `Close`'dan SONRA bir `Echo` eklendi — bu satışın aldığı MALİ FİŞ NUMARASINI
        // okumak için. Bedeli kapanış başına bir çağrı (~0,2 sn) ve BİLİNÇLİ: numara mali
        // kesinleşmede tüketiliyor, cihaz kilidi hâlâ bizde, ve baskı zincirinin İÇİNE koymak
        // kasanın 180 sn'lik kapısına yeni bir başarısızlık noktası eklerdi.
        // Gizlenmedi, burada AÇIKÇA yazılı.
        Assert.Equal(
            new[] { "Start", "TicketHeader", "OptionFlags", "ItemSale", "GetTicket", "Payment",
                    "OptionFlags", "GetTicket",
                    "PrintTotalsAndPayments", "PrintBeforeMF", "PrintMF", "Close", "Echo" },
            g.Calls);
        // ← ÇİVİ: kalan üç baskı adımı ZORUNLU ve bu SIRADA olmalı. `PrintBeforeMF` fişi mali
        // hafızaya yazan taahhüt noktası; ondan önce `PrintMF`'e geçilirse fiş mali kayda hiç
        // girmez, sonra `VoidAll` denenirse 2357 alınır.
        Assert.DoesNotContain("PrintUserMessage", g.Calls);
        Assert.Equal("CLOSED", r.TicketState);
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
    public async Task Probe_odeme_sayaci_ARTMISSA_ve_fis_BIZIMSE_Landed()
    {
        // Sayaç artışı `Landed` için GEREKLİ ama YETERLİ DEĞİL: fişin bu komuta ait olduğu da
        // kanıtlanmalı (aşağıdaki `Probe_fis_BIZIM_DEGILSE_Landed_DEMEZ` testine bakın).
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(3000, 0, 0, 0);              // ödeme öncesi anlık görüntü
        g.Codes["Payment"] = GmpCodes.Timeout;
        await t.SaleAsync(Req(oturum: "oturum-A"));

        g.Ticket = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card, "RRN9");
        var p = await t.ProbeAsync(Req(oturum: "oturum-A"));

        Assert.Equal(ProbeVerdict.Landed, p.Verdict);
        Assert.Equal(1000, p.ApprovedAmountMinor);
        Assert.Equal(2000, p.RemainingMinor);   // kalan var ve bu ARIZA DEĞİL
    }

    [Fact]
    public async Task Probe_fis_BIZIM_DEGILSE_Landed_DEMEZ()
    {
        // SAHADA OLDU (2026-09-07 16:36): sabah 2086 alıp hiç para almamış bir kart komutu,
        // kurtarma turunda kullanıcının YENİ nakit satışının fişini gördü ve "benim ödemem
        // geçmiş" dedi. Defterde masa-5'e 4,90 TL HAYALET kart tahsilatı yazıldı; gerçek para
        // masa-7'de nakitti. İki fişin tutarı da aynı olduğu için tutar karşılaştırması da
        // yakalamadı.
        // ← ÇİVİ: sayaç artışı kimin ödemesi olduğunu SÖYLEMEZ. Sahiplik kanıtlanamıyorsa belirsiz.
        var (t, g, snap) = Kur();
        g.Ticket = new GmpTicket(3000, 0, 0, 0);
        g.Codes["Payment"] = GmpCodes.Timeout;
        await t.SaleAsync(Req(oturum: "oturum-A"));

        // Cihazdaki açık fiş artık BAŞKA bir satışın.
        snap.BindOpenTicket("t1", "oturum-B");
        g.Ticket = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card, "RRN9");

        var p = await t.ProbeAsync(Req(oturum: "oturum-A"));

        Assert.Equal(ProbeVerdict.Indeterminate, p.Verdict);   // ← ÇİVİ
        Assert.Null(p.ApprovedAmountMinor);
    }

    [Fact]
    public async Task Probe_sahiplik_BILINMIYORSA_Landed_DEMEZ()
    {
        // Oturum kimliği gelmeyen (eski platform) kayıtlarda kanıt kurulamaz. Maliyeti kabul
        // edildi: o komutlar operatöre çıkar. Alternatifi başkasının ödemesini sahiplenmek.
        var (t, g, _) = Kur();
        g.Ticket = new GmpTicket(3000, 0, 0, 0);
        g.Codes["Payment"] = GmpCodes.Timeout;
        await t.SaleAsync(Req(oturum: null));

        g.Ticket = new GmpTicket(3000, 1000, 1, GmpPaymentTypes.Card, "RRN9");
        var p = await t.ProbeAsync(Req(oturum: null));

        Assert.Equal(ProbeVerdict.Indeterminate, p.Verdict);
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
        await t.SaleAsync(Req(amount: 2000, paymentType: GmpPaymentTypes.Cash, oturum: "oturum-A"));

        g.Ticket = new GmpTicket(4000, 4000, 2, GmpPaymentTypes.Cash);
        var p = await t.ProbeAsync(Req(amount: 2000, paymentType: GmpPaymentTypes.Cash, oturum: "oturum-A"));

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
        //
        // İLK `OptionFlags` W128'in canlılık sınaması: elimizdeki tanıtıcı sıfır değil ama
        // YAŞADIĞININ KANITI YOK. Bedeli bir DLL çağrısı; karşılığı, ölü tanıtıcının iptali
        // süreç yeniden başlayana kadar kilitlememesi (sahada 2026-09-10'da tam bu oldu).
        // Gizlemek yerine burada AÇIKÇA yazılı: fazladan çağrı bilinçli bir seçim.
        Assert.Equal(new[] { "OptionFlags", "OptionFlags", "GetTicket", "VoidAll", "Close" }, g.Calls);
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
    public async Task BAYAT_TANITICI_YENILENINCE_ACIK_FIS_YOKSA_YOK_DENIR_BILINMIYOR_DEGIL()
    {
        // ← ÇİVİ: BAĞIMSIZ DENETİM BULDU. `GecerliTanitici`'nin "bayat → yenile → meğer açık
        // fiş yokmuş" dalı testsizdi; `return null` yerine `return h` yazsan bütün testler
        // yeşil kalıyordu.
        //
        // Etkisi tam olarak bu kod tabanının en çok önemsediği ayrım: "YOK" ile "BİLİNMİYOR".
        // Dal bozulsaydı kasiyer, iptal edilecek bir şey olmadığı hâlde
        // `Unknown/TICKET_READ_FAILED` görürdü — yani "belirsiz, operatöre git". Oysa doğru
        // cevap "ortada iptal edilecek fiş yok, işin tamam".
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 990, 1, GmpPaymentTypes.Cash);
        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 3000));

        g.StaleHandles.Add(42);                  // elimizdeki tanıtıcı ÖLDÜ
        g.NextStartHandle = 43;
        g.Codes["Start"] = GmpCodes.Ok;          // 2080 DEĞİL → cihazda açık fiş YOK
        g.Calls.Clear();

        var r = await t.VoidTicketAsync("t1");

        Assert.Equal(TransportOutcome.Approved, r.Outcome);   // "yok" bir HATA değil
        Assert.False(r.TicketWasOpen);
        Assert.Equal("NO_OPEN_TICKET", r.ProviderResultCode);
        Assert.DoesNotContain("VoidAll", g.Calls);            // fişe dokunulmadı
    }

    [Fact]
    public async Task BAYAT_TANITICI_IPTALDE_de_YENILENIR()
    {
        // ← ÇİVİ: BU KUSUR İKİNCİ KEZ TEKRARLADI ve sebebi tam olarak buydu.
        //
        // Bayat tanıtıcı kurtarması 2026-09-06/07'de ölçülüp `ReadTicket`e eklendi — ama YALNIZ
        // ORAYA. İptal yolları `_handle == 0` dışında bir geçersizlik tanımıyordu; SIFIR OLMAYAN
        // ama ÖLÜ bir tanıtıcı görünmez kalıyordu.
        //
        // Sahada (2026-09-10 16:39): rulo takılı, cihaz ekranı normal, iptal 273 ms'de
        // TICKET_READ_FAILED. Cihaza gidip bekleyen bir çağrı o kadar hızlı dönmez — ölü
        // tanıtıcı hemen döner. Ajan 12 saattir ayaktaydı; yeniden başlamadan iptal edilemezdi.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 990, 1, GmpPaymentTypes.Cash);
        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 3000));
        g.StaleHandles.Add(42);                    // elimizdeki tanıtıcı cihaz tarafında ÖLDÜ
        g.NextStartHandle = 43;                    // yenileme YENİ tanıtıcı verecek
        g.Codes["Start"] = GmpCodes.AlreadyDone;   // açık fiş VAR (2080)
        g.Calls.Clear();
        g.Ticket = new GmpTicket(3000, 990, 1, GmpPaymentTypes.Cash);

        var r = await t.VoidTicketAsync("t1");

        // Ölü tanıtıcıyla pes ETMEDİ: yeniledi ve iptali TAMAMLADI.
        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Contains("VoidAll", g.Calls);
        Assert.Equal(990, r.VoidedAmountMinor);
    }

    [Fact]
    public async Task FIS_OKUNAMAZSA_IPTAL_EDILMEZ_VoidAll_CAGRILMAZ()
    {
        // ← ÇİVİ: BU SAHADA AZ KALSIN PARAYI SESSİZCE KAYBETTİRİYORDU.
        //
        // Eskiden iptal öncesi okuma BAŞARISIZ olsa da akış devam ediyor, `iptalTutar`/
        // `iptalSayi` SIFIR kalıyor ve `VoidAll` yine de çağrılıyordu. Başarılı olsaydı:
        // 990 kuruşluk fiş iptal edilir, kasaya "iptal edildi, 0 kuruş" dönerdi — parayı geri
        // alır, geri aldığımızın TEK KAYDINI da yok ederdik (iptalden sonra fiş yok).
        //
        // Ölçüm (2026-09-10 16:30): kâğıt bitikken kasadan iptal denendi, okuma patladı
        // (log'da `[gmp] iptal öncesi fiş` satırı YOK) ve akış `VoidAll`a devam etti. Para
        // hareket etmedi çünkü `VoidAll` DA patladı — bizi kurtaran tasarım değil, cihazın
        // aynı anda tıkalı olmasıydı. Tasarıma güvenilmeliydi.
        // Kısmi ödeme: fiş AÇIK kalır, yani ortada gerçekten iptal edilecek bir şey var.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 990, 1, GmpPaymentTypes.Cash);
        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 3000));
        g.Calls.Clear();
        g.Codes["GetTicket"] = GmpCodes.RecvBusy;     // cihaz okumayı reddediyor

        var r = await t.VoidAsync();

        // ⚠️ FİŞE HİÇ DOKUNULMADI.
        Assert.DoesNotContain("VoidAll", g.Calls);
        Assert.Equal(TransportOutcome.Unknown, r.Outcome);
        Assert.Equal("VOID_READ_FAILED", r.ProviderResultCode);
        // "Olmadı" demek de yanlış olurdu: kasa bunu belirsizlik olarak görmeli.
        Assert.Equal("InProgress", r.ErrorCondition);
        Assert.False(r.PaymentInvoked);
    }

    [Fact]
    public async Task OKUNABILEN_fis_iptalinde_TUTAR_ve_SAYI_dogru_bildirilir()
    {
        // ← ÇİVİ: kuralın diğer yarısı. Okuma BAŞARILIYSA iptal olağan işler ve kasaya giden
        // sayılar GERÇEK olur — "0 kuruş iptal edildi" diye bir cevap bir daha çıkmamalı.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(3000, 990, 1, GmpPaymentTypes.Cash);
        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 3000));
        g.Calls.Clear();
        g.Ticket = new GmpTicket(3000, 990, 1, GmpPaymentTypes.Cash);

        var r = await t.VoidAsync();

        Assert.Contains("VoidAll", g.Calls);
        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal(990, r.VoidedAmountMinor);
        Assert.Equal(1, r.VoidedPaymentCount);
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

    // ── W41: DEVAM YOLUNDA FİŞ İKİ KEZ OKUNMUYOR ────────────────────────────────

    [Fact]
    public async Task Devam_yolunda_fis_odeme_oncesi_BIR_KEZ_okunur()
    {
        // ← ÇİVİ: `DevamDogrula` fişi zaten okudu; o okumadan ödemeye kadar cihaza başka çağrı
        // gitmiyor (kalem/başlık bloğu devam yolunda atlanıyor). İkinci `GetTicket` aynı sayıyı
        // ikinci kez sormaktan ibaretti — sahada ölçülen ~345 ms'lik bir cihaz turu.
        //
        // Yeniden kullanım kaldırılırsa ödeme ÖNCESİNDEKİ GetTicket sayısı 2'ye çıkar ve bu
        // test kırılır.
        var (t, g, snap) = Kur();
        snap.BindOpenTicket("t1", "oturum-A");
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(990, 500, 1, GmpPaymentTypes.Cash);
        g.AfterPayment = new GmpTicket(990, 990, 2, GmpPaymentTypes.Cash);

        await t.SaleAsync(Req(amount: 490, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        var odeme = g.Calls.IndexOf("Payment");
        Assert.True(odeme >= 0, "Payment çağrılmadı");
        Assert.Equal(1, g.Calls.Take(odeme).Count(c => c == "GetTicket"));
    }

    [Fact]
    public async Task Devam_yolunda_okuma_atlansa_da_anlik_goruntu_YAZILIR()
    {
        // ← ÇİVİ: W41 "okumayı atla" değil, "okunanı yeniden kullan". Anlık görüntü düşerse
        // belirsizlik çözümü (P34/W25) dayanaksız kalır: ödeme yanıtsız bitince "cihazda ne oldu"
        // sorusunu karşılaştıracak bir taban kalmaz.
        var (t, g, snap) = Kur();
        snap.BindOpenTicket("t1", "oturum-A");
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(990, 500, 1, GmpPaymentTypes.Cash);
        g.AfterPayment = new GmpTicket(990, 990, 2, GmpPaymentTypes.Cash);

        await t.SaleAsync(Req(amount: 490, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990, komut: "c-devam"));

        var g2 = snap.ReadSnapshot("c-devam");
        Assert.NotNull(g2);
        // ÖDEMEDEN ÖNCEKİ hâl yazılmış olmalı (990/500/1), ödemeden sonraki (990/990/2) DEĞİL.
        Assert.Equal((990L, 500L, 1, "oturum-A"), g2!.Value);
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

    // ── W16: FİŞ DURUMU (K-26) ──────────────────────────────────────────────────

    [Fact]
    public async Task Kismi_odemede_ticketState_OPEN()
    {
        // Platform satırları fiş KAPANINCA yazacak. Kısmi ödemede "OPEN" demezsek kasa o ödemenin
        // deftere geçtiğini sanır; sonradan fiş iptal edilirse satırı geri almak gerekirdi.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(990, 500, 1, GmpPaymentTypes.Cash);   // kısmi

        var r = await t.SaleAsync(Req(amount: 500, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("OPEN", r.TicketState);
        Assert.DoesNotContain("Close", g.Calls);
    }

    [Fact]
    public async Task Tam_odemede_ticketState_CLOSED_ve_yalniz_Close_BASARILIYSA()
    {
        // ← ÇİVİ: `CLOSED` yalnız `Close` gerçekten başarılıysa. Baskı/kapatma yarım kalmışsa fiş
        // hâlâ AÇIKTIR ve öyle bildirilmeli; aksi hâlde defter kapanmamış bir fişi kapanmış sanır.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);   // tam

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("CLOSED", r.TicketState);
        Assert.Contains("Close", g.Calls);
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
        Assert.Equal(1490, r.DeviceTicketTotalMinor);
        Assert.Equal(990, r.DeviceRemainingMinor);
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
        // Kasiyer CİHAZIN gördüğü sayıyı görmeli: panel kalanı ile cihaz kalanı ayrışabiliyor.
        Assert.Equal(990, r.DeviceTicketTotalMinor);
        Assert.Equal(490, r.DeviceRemainingMinor);
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
    public async Task Cihaz_tutarlari_YALNIZ_ayrisma_retlerinde_tasinir()
    {
        // Her gövdeye koymak, alanı "bazen doğru bazen bayat" bir veriye çevirirdi.
        var (t, g, _) = Kur();
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Null(r.DeviceTicketTotalMinor);
        Assert.Null(r.DeviceRemainingMinor);
    }

    [Fact]
    public async Task BASARISIZ_odeme_de_fisi_sahiplenir_ayni_adisyon_DEVAM_EDEBILIR()
    {
        // SAHADA ÖLÇÜLDÜ (2026-09-07): banka hattı yokken kart denemesi 2086 aldı, fiş açık
        // kaldı ama SAHİPSİZ kaldı (bağ yalnız başarılı ödemeden sonra yazılıyordu). Aynı
        // adisyonun ikinci denemesi kendi fişini "başka satışın bayat fişi" sanıp reddetti ve
        // kasa manuel ödemeye düştü.
        // ← ÇİVİ: bağ ödemeden ÖNCE yazılır; başarısız deneme de fişi sahiplenir.
        var (t, g, snap) = Kur();
        g.Codes["Payment"] = GmpCodes.PaymentFailedWithBankCode;   // 2086 — banka hattı yok

        var ilk = await t.SaleAsync(Req(amount: 990, oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(TransportOutcome.Unknown, ilk.Outcome);
        Assert.Equal("oturum-A", snap.ReadOpenTicketBinding("t1"));   // ← ÇİVİ: fiş sahiplendi

        // İkinci deneme: aynı adisyon, cihazda o fiş duruyor.
        g.Codes.Remove("Payment");
        g.StartSequence.Enqueue(GmpCodes.AlreadyDone);
        g.Ticket = new GmpTicket(990, 0, 1, GmpPaymentTypes.Card);
        g.AfterPayment = new GmpTicket(990, 990, 2, GmpPaymentTypes.Cash);
        g.Calls.Clear();

        var ikinci = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990, komut: "c2"));

        Assert.Equal(TransportOutcome.Approved, ikinci.Outcome);   // ← devam etti, reddetmedi
        Assert.DoesNotContain("ItemSale", g.Calls);                // kalemler tekrarlanmadı
        Assert.DoesNotContain("VoidAll", g.Calls);                 // fiş silinmedi
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

    // ── W104: BASKI PATLARSA KASA BUNU DOĞRUDAN GÖRSÜN ──────────────────────────
    //
    // Saha (2026-09-09 23:07): `PrintBeforeMF` zaman aşımına uğradı, fiş 990 kuruş tahsilatla açık
    // kaldı. Kasaya giden yanıt `result:"Success"` + `reason:null` idi; kasa arızayı ancak
    // "kalan 0 ama fiş açık" ÇELİŞKİSİNDEN çıkarabiliyordu. O çıkarım KIRILGAN: kısmi ödemede
    // baskı patlarsa kalan zaten 0 olmaz, çelişki hiç oluşmaz, arıza kasada GÖRÜNMEZ.

    /// <summary>Tam ödenmiş bir satış kurar; kapanış zinciri çalışır.</summary>
    private static (GmpTerminalTransport, FakeGmp) TamOdemeKur()
    {
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        return (new GmpTerminalTransport(g, snap), g);
    }

    [Fact]
    public async Task BASKI_ZAMAN_ASIMI_kasaya_PRINT_TIMEOUT_olarak_doner()
    {
        var (t, g) = TamOdemeKur();
        g.Codes["PrintBeforeMF"] = GmpCodes.Timeout;   // 0xF003 — sahada ölçülen kod

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 990));

        // ← ÇİVİ: sonuç `Approved` KALIR. Para ALINDI; "reddedildi" demek alınmış bir tahsilatı
        // yok saymak olurdu — yanlış yön, tehlikeli yön.
        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal("OPEN", r.TicketState);
        // ← ÇİVİ: sebep artık DOĞRUDAN geliyor, çelişkiden çıkarılmıyor.
        Assert.Equal(RestomenumReasons.PrintTimeout, r.Reason);
        Assert.Equal("PrintBeforeMF", r.PrintFailedStep);
        Assert.Equal("0xF003", r.PrintFailedCode);
        // ← ÇİVİ: `OUT_OF_PAPER` DEĞİL. Cihaz hiçbir izde kâğıt kodu döndürmedi; kâğıt iddiası
        // uydurma olurdu (kullanıcı 23:07 fişini elinde tutuyordu: rulo bitmemişti).
        Assert.DoesNotContain("PAPER", r.Reason);
    }

    [Fact]
    public async Task CIHAZ_KAGIT_BITTI_derse_sebep_OUT_OF_PAPER_olur()
    {
        var (t, g) = TamOdemeKur();
        g.Codes["PrintBeforeMF"] = GmpCodes.NoPaper;   // 0x0020 — SAHADA ÖLÇÜLDÜ 2026-09-10 02:21

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 990));

        // ← ÇİVİ: bu bir ÇIKARIM DEĞİL, cihazın BEYANI. Uzun süre "kâğıt hakkında sıfır ölçümümüz
        // var" dedik ve haklıydık — 10 sn'lik zaman aşımı cihazı cevap veremeden kesiyordu.
        // Süre uzayınca cihaz kodu gönderdi. Eşleme AJANDA çünkü sabit burada; kasada türetilseydi
        // aynı sabit iki yerde tutulur ve biri sessizce kayardı.
        Assert.Equal(TransportOutcome.Approved, r.Outcome);   // para ALINDI
        Assert.Equal(RestomenumReasons.OutOfPaper, r.Reason);
        Assert.Equal("PrintBeforeMF", r.PrintFailedStep);
        Assert.Equal("0x0020", r.PrintFailedCode);            // HAM kod da gidiyor
    }

    [Fact]
    public async Task KAGIT_UYARISI_KURTARMA_KAPATSA_BILE_DURUR()
    {
        // ← ÇİVİ: SAHADA ISIRDI (2026-09-10 04:20) ve kullanıcı gördü:
        //     04:20:03  baskı patladı (0x0020)  → uyarı AÇILDI
        //     04:20:09  kurtarma başarıyla bastı → uyarı KAPANDI
        //     ömür: **5,8 saniye**. Panel görmedi, kullanıcı görmedi.
        //
        // W120'de "yalnız başarılı bir baskı düşürür" demiş ve KURTARMAYI da o sayıya
        // katmıştım. Kural kendi içinde tutarlıydı ama sonucu saçmaydı: uyarıyı DOĞURAN olayı
        // çözen baskının o uyarıyı SİLMESİ, uyarının aynı olayın içinde doğup ölmesi demek.
        //
        // ⚠️ Üstelik "başarılı baskı = kâğıt var" da doğru değil: rulo ÇIKIKKEN `PrintBeforeMF`
        // 4 saniye sonra başarılı oldu (aynı ölçüm).
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.Codes["PrintBeforeMF"] = GmpCodes.NoPaper;
        var t = new GmpTerminalTransport(g, snap);

        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));
        Assert.NotNull(snap.KagitUyarisi);
        Assert.Equal("PrintBeforeMF", snap.KagitUyarisi!.Value.Adim);

        // KURTARMA fişi kapatır — uyarı DURMALI.
        g.Codes.Remove("PrintBeforeMF");
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash,
            TransactionFlags: TicketFlags.TotalsAndPaymentsPrinted);
        var k = ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: true);

        Assert.True(k.Kapandi);
        Assert.NotNull(snap.KagitUyarisi);          // ← uyarı DURUYOR, insanı bekliyor

        // Uyarıyı düşüren TEK şey: bir sonraki OLAĞAN satışın tam zinciri.
        g.Codes.Remove("Start");
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-B", satisToplam: 990, komut: "c2", odeme: "p2"));

        Assert.Null(snap.KagitUyarisi);             // ← normal işleyiş döndü, uyarı düştü
        Assert.Equal(new[] { "ac:PrintBeforeMF", "kapat" }, snap.KagitSirasi);
    }

    [Fact]
    public void Kurtarma_BASARISIZSA_kagit_uyarisi_DURUR()
    {
        // ← ÇİVİ: uyarıyı düşüren şey "kurtarma çalıştı" DEĞİL, "baskı BAŞARDI" olmalı.
        // Kâğıt hâlâ yokken kurtarma denemesi de patlar; orada uyarıyı düşürmek, kâğıdın
        // geldiğini cihaz söylemeden İDDİA ETMEK olurdu — ve rozet sönerdi.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        snap.BindOpenTicket("t1", "oturum-A");              // bağ: terminal kimliği buradan okunur
        snap.SetPaperWarning("t1", true, "PrintBeforeMF");
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Codes["PrintMF"] = GmpCodes.NoPaper;              // kâğıt hâlâ yok
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash,
            TransactionFlags: TicketFlags.TotalsAndPaymentsPrinted);
        var t = new GmpTerminalTransport(g, snap);

        var s = ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: true);

        Assert.False(s.Kapandi);
        Assert.NotNull(snap.KagitUyarisi);                  // ← uyarı DURUYOR
        Assert.Equal("PrintMF", snap.KagitUyarisi!.Value.Adim);   // ve adım TAZELENDİ
    }

    [Fact]
    public void BIRDEN_COK_bag_varsa_kagit_uyarisina_DOKUNULMAZ()
    {
        // ← ÇİVİ: hangi terminalin kâğıdı bittiği BELİRSİZ. Rastgele birine yazmak, başka bir
        // kasanın cihazına ait sağlık bilgisini bu cihaza atfetmek olurdu. `TekBag`'daki
        // ilkenin aynısı: belirsizlikte iddia edilmez.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        snap.BindOpenTicket("t1", "oturum-A");
        snap.BindOpenTicket("t2", "oturum-B");
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash,
            TransactionFlags: TicketFlags.MfPrinted);
        var t = new GmpTerminalTransport(g, snap);

        ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: true);

        Assert.Empty(snap.KagitSirasi);   // ne açıldı ne kapatıldı
    }

    [Fact]
    public async Task Fis_KAPANSA_bile_BASKI_PATLADIYSA_uyari_DUSMEZ()
    {
        // ← ÇİVİ: "fiş kapandı" ile "fiş BASILDI" AYRI şeyler. `PrintMF` üç denemede de
        // patlayıp `Close` başarılı olduğunda fiş KAPANIR ama mali dip BASILAMAMIŞTIR —
        // orada kâğıt uyarısını düşürmek, basılamamış bir fişi "her şey yolunda" saymak olurdu.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        snap.SetPaperWarning("t1", true, "PrintBeforeMF");
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.PrintMfFailures = 3;                     // üçü de patlar, `Close` yine de geçer
        var t = new GmpTerminalTransport(g, snap);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("CLOSED", r.TicketState);     // fiş kapandı
        Assert.Equal("PrintMF", r.PrintFailedStep); // ama basılamadı
        Assert.NotNull(snap.KagitUyarisi);          // ← uyarı DURUYOR
    }

    [Fact]
    public async Task BASARILI_BASKI_park_edilmis_fisleri_SERBEST_BIRAKIR()
    {
        // ← ÇİVİ: MUTASYON BUNU ÖLÜ SAVUNMA OLARAK YAKALADI — kancayı silsem 626 test yeşil
        // kalıyordu. Oysa W125'in CAN DAMARI burası: park edilmiş fişi serbest bırakan tek olay.
        //
        // Ölçüm (2026-09-10 06:37): cihaz kademeli kötüleşti (0xF01C 5,5 sn → 0xF003 13,8 sn →
        // 0xF000 66 sn), tek pahalı deneme pencereyi kapattı ve üzerinde 990 kuruş olan fiş park
        // edildi. Erteleme iptal edilebilir olmasaydı rulo takılsa bile kapanmayacaktı.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        var t = new GmpTerminalTransport(g, snap);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("CLOSED", r.TicketState);
        Assert.Null(r.PrintFailedStep);            // zincir baştan sona geçti
        Assert.Equal(1, snap.ParkBosaltildi);      // ← park boşaldı
    }

    [Fact]
    public async Task BASKI_PATLAYINCA_park_BOSALTILMAZ()
    {
        // ← ÇİVİ: kancanın diğer yarısı. Baskı patladıysa cihazın çalıştığına dair KANIT YOK;
        // parkı boşaltmak, ertelemenin varlık sebebini sessizce ortadan kaldırırdı — pahalı
        // denemeler yeniden peş peşe koşar ve cihaz rehin alınırdı.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.PrintMfFailures = 3;
        var t = new GmpTerminalTransport(g, snap);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal("PrintMF", r.PrintFailedStep);
        Assert.Equal(0, snap.ParkBosaltildi);
    }

    [Fact]
    public void KURU_PROVA_kagit_uyarisini_DUSURMEZ()
    {
        // ← ÇİVİ: kuru prova hiçbir şey BASMAZ, dolayısıyla kâğıdın geldiğini KANITLAMAZ.
        // Düşürseydi, cihaza hiç dokunmadan "kâğıt geldi" iddia etmiş olurduk.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        snap.SetPaperWarning("t1", true, "PrintBeforeMF");
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash,
            TransactionFlags: TicketFlags.TotalsAndPaymentsPrinted);
        var t = new GmpTerminalTransport(g, snap);

        ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: false);

        Assert.NotNull(snap.KagitUyarisi);
    }

    [Fact]
    public async Task Start_KAGIT_BITTI_derse_uyari_ORADA_dogar()
    {
        // Sahada ölçüldü (03:02:57): rulo tamamen çıkıkken cihaz fişi AÇMAYI reddediyor.
        // Baskıya hiç gelinmiyor — uyarı orada doğmazsa hiç doğmaz.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Codes["Start"] = GmpCodes.NoPaper;
        var t = new GmpTerminalTransport(g, snap);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 990));

        Assert.Equal(TransportOutcome.Declined, r.Outcome);
        Assert.Equal(RestomenumReasons.OutOfPaper, r.Reason);
        Assert.NotNull(snap.KagitUyarisi);
        Assert.Equal("Start", snap.KagitUyarisi!.Value.Adim);
    }

    [Fact]
    public async Task BASKI_RED_zaman_asimindan_AYRI_sebep_alir()
    {
        var (t, g) = TamOdemeKur();
        g.Codes["PrintTotalsAndPayments"] = 2119u;   // zaman aşımı DEĞİL: cevap geldi ve hayır dedi

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 990));

        // ← ÇİVİ: "cevap gelmedi" ile "cevap geldi ve hayır dedi" FARKLI olgular ve farklı
        // müdahale gerektirir. Tek sebebe katlamak teşhisi kör bırakırdı.
        Assert.Equal(RestomenumReasons.PrintFailed, r.Reason);
        Assert.Equal("PrintTotalsAndPayments", r.PrintFailedStep);
        Assert.Equal("0x0847", r.PrintFailedCode);
    }

    [Fact]
    public async Task CLOSE_basarisizsa_baski_sebebinden_AYRI_bildirilir()
    {
        var (t, g) = TamOdemeKur();
        g.Codes["Close"] = GmpCodes.Timeout;

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 990));

        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal("OPEN", r.TicketState);
        // ← ÇİVİ: burada kâğıt/yazıcı sorusu YOKTUR — baskı tamamlandı, fiş kapanmadı. Kasiyere
        // "ruloyu kontrol edin" dedirtmek yanlış işe yönlendirmek olurdu.
        Assert.Equal(RestomenumReasons.TicketCloseFailed, r.Reason);
        Assert.Equal("Close", r.PrintFailedStep);
    }

    [Fact]
    public async Task PrintMF_UC_DENEMEDE_de_patlarsa_fis_kapansa_BILE_bildirilir()
    {
        var (t, g) = TamOdemeKur();
        g.PrintMfFailures = 3;   // üçü de başarısız; `Close` yine denenir (eski davranış)

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 990));

        // ← ÇİVİ: fiş KAPANDI ama mali dip BASILAMADI. Eskiden bu tamamen yutuluyordu; kapanmak,
        // basılmış olmak demek değildir ve fark yalnız buradan görülebilir.
        Assert.Equal("CLOSED", r.TicketState);
        Assert.Equal(RestomenumReasons.PrintFailed, r.Reason);
        Assert.Equal("PrintMF", r.PrintFailedStep);
    }

    [Fact]
    public async Task BASKI_patlayinca_fis_TEKRAR_DENENMEK_UZERE_isaretlenir()
    {
        // ← ÇİVİ: bu kayıt olmadan yarım kalmış fişi tekrar deneyecek HİÇBİR ŞEY yok.
        // 2026-09-09'da tam bu boşluk 83 dakika sürdü (23:07 → 00:30) ve ancak insan
        // müdahalesiyle kapandı — ajan bütün o süre boyunca sorunsuz çalışıyordu.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.Codes["PrintBeforeMF"] = GmpCodes.Timeout;
        var t = new GmpTerminalTransport(g, snap);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        var fisId = Assert.Single(snap.Bekleyen).Key;
        Assert.Equal(r.TicketId, fisId);
        Assert.Equal("PrintBeforeMF", snap.Bekleyen[fisId].Adim);
        Assert.Equal("0xF003", snap.Bekleyen[fisId].Kod);
    }

    [Fact]
    public async Task Isaret_baski_SIRASINDA_zaten_DURUYOR_surec_olurse_kaybolmasin()
    {
        // ← ÇİVİ: SAHADA ISIRDI (2026-09-10 02:05). Süreç baskı zincirinin ORTASINDA öldü
        // (yayım için durduruldu, cihaz kâğıt bekliyordu). İşaret zincir DÖNDÜKTEN sonra
        // yazıldığı için HİÇ yazılmadı ve fiş 990 kuruş tahsilatla açık kaldı — süpürme onu
        // GÖRMEDİ. `closed_tickets`'ın kapattığı boşluğun birebir kardeşi.
        //
        // Doğru kural: TEHLİKELİ ADIMDAN ÖNCE kalıcı iz bırak. Bunu ancak baskı ÇAĞRISININ
        // İÇİNDEN ölçebiliriz; öncesi/sonrası bu soruyu cevaplayamaz.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        int baskiAninda = -1;
        g.BaskiSirasinda = () => baskiAninda = snap.Bekleyen.Count;
        var t = new GmpTerminalTransport(g, snap);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.Equal(1, baskiAninda);          // ← baskı sürerken işaret DURUYOR
        Assert.Equal("CLOSED", r.TicketState);
        Assert.Empty(snap.Bekleyen);           // ← zincir bitince düştü
    }

    [Fact]
    public async Task Baski_SORUNSUZSA_bekleyen_kayit_YAZILMAZ()
    {
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        var t = new GmpTerminalTransport(g, snap);

        // OTURUM ŞART: bağ olmadan fiş kimliği doğmaz ve kayıt kapısı hiç sınanmamış olurdu
        // (ilk yazımda tam bu yüzden mutasyon YEŞİL kaldı — test kapıya hiç değmiyordu).
        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        Assert.NotNull(r.TicketId);          // kapı gerçekten sınanabilir durumda
        Assert.Equal("CLOSED", r.TicketState);
        // ← ÇİVİ: zincir tamamlanınca işaret DÜŞER. Kalsaydı süpürme her başarılı satıştan
        // sonra cihaza gider ve kapanmış fişi boş yere yoklardı.
        Assert.Empty(snap.Bekleyen);
    }

    // ── W105: TAMAMLAMA — CİHAZ TARAFI ──────────────────────────────────────────

    [Fact]
    public void FinishStuckTicket_kapatinca_DEFTERE_yazar_ve_bagi_siler()
    {
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        snap.BindOpenTicket("t1", "oturum-A");
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash,
            TransactionFlags: TicketFlags.TotalsAndPaymentsPrinted);
        var t = new GmpTerminalTransport(g, snap);

        var s = ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: true);

        Assert.True(s.Kapandi);
        Assert.Equal(new[] { "Start", "OptionFlags", "GetTicket", "PrintMF", "Close" }, g.Calls);
        // ← ÇİVİ: cihazda kapatmak masayı AÇMAZ. Defter kaydı olmadan platform fişin
        // ödemelerini hiç yazmaz ve kullanıcının gördüğü arıza aynen sürer.
        Assert.Single(snap.Kapananlar);
        Assert.Equal(990, snap.Kapananlar[0].PaidMinor);
        Assert.Contains("clear:t1", snap.KapanisSirasi);
        Assert.DoesNotContain("VoidAll", g.Calls);
        Assert.DoesNotContain("VoidPayment", g.Calls);
    }

    [Fact]
    public void FinishStuckTicket_BIRDEN_COK_bag_varsa_HICBIRI_bildirilmez()
    {
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        snap.BindOpenTicket("t1", "oturum-A");
        snap.BindOpenTicket("t2", "oturum-B");   // ikinci terminal de açık görünüyor
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash,
            TransactionFlags: TicketFlags.MfPrinted);
        var t = new GmpTerminalTransport(g, snap);

        var s = ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: true);

        // Fiş cihazda KAPANDI (bu doğru ve geri alınamaz).
        Assert.True(s.Kapandi);
        // ← ÇİVİ: ama HANGİ fişin kapandığı belirsiz. Rastgele birini seçip onun ödemelerini
        // deftere yazmak, BAŞKA BİR MASANIN tahsilatını bu masaya geçirmek olurdu — hiç
        // yazmamaktan çok daha kötü. Belirsizlikte iddia edilmez.
        Assert.Empty(snap.Kapananlar);
        Assert.Null(s.TicketId);
        Assert.NotNull(snap.ReadOpenTicketBinding("t1"));
        Assert.NotNull(snap.ReadOpenTicketBinding("t2"));
    }

    [Fact]
    public void FinishStuckTicket_ONCE_kapanisi_yazar_SONRA_bagi_siler()
    {
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        snap.BindOpenTicket("t1", "oturum-A");
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Ticket = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash,
            TransactionFlags: TicketFlags.MfPrinted);
        var t = new GmpTerminalTransport(g, snap);

        ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: true);

        // ← ÇİVİ: SIRA. Bağ önce silinseydi ve ikisi arasında süreç ölseydi, fişi bildirecek
        // KİMLİK kalmazdı ve o tahsilat deftere HİÇ yazılmazdı. (Aynı gerekçe satış yolunda
        // da yazılı — burada da geçerli, çünkü kayıp aynı kayıp.)
        var yaz = snap.KapanisSirasi.FindIndex(x => x.StartsWith("mark:", StringComparison.Ordinal));
        var sil = snap.KapanisSirasi.IndexOf("clear:t1");
        Assert.True(yaz >= 0 && sil >= 0, "iki adım da çalışmalı");
        Assert.True(yaz < sil, "kapanış kaydı bağ silinmeden ÖNCE yazılmalı");
    }

    [Fact]
    public void FinishStuckTicket_acik_fis_YOKSA_yoklama_fisini_kapatir_ve_DEFTERE_DOKUNMAZ()
    {
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        snap.BindOpenTicket("t1", "oturum-A");
        var t = new GmpTerminalTransport(g, snap);   // Start → Ok (açık fiş yok)

        var s = ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: true);

        Assert.True(s.FisAcikDegil);
        Assert.False(s.Kapandi);
        // ← ÇİVİ: probe fişi AÇIK bırakılırsa sonraki satış onu bayat sanır ve kasa manuel
        // ödemeye düşer. Ve "kapandı" diye deftere yazmak, ölçmediğimiz bir kapanışı iddia
        // etmek olurdu — bu tur o fişi kapatmadı.
        Assert.Equal(new[] { "Start", "Close" }, g.Calls);
        Assert.Empty(snap.Kapananlar);
    }

    [Fact]
    public void FinishStuckTicket_cihaz_MESGULSE_denenmedi_sayilir()
    {
        var g = new FakeGmp();
        g.Codes["Start"] = GmpCodes.RecvBusy;
        var t = new GmpTerminalTransport(g, new FakeSnapshots());

        var s = ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: true);

        // ← ÇİVİ: "cevap gelmedi" ile "denendi ve olmadı" AYRI. İlkini alarma çevirmek,
        // olmamış bir denemeyi başarısızlık diye raporlamak olurdu.
        Assert.True(s.Meshgul);
        Assert.False(s.Kapandi);
        Assert.False(s.FisAcikDegil);
        Assert.Equal(new[] { "Start" }, g.Calls);
    }

    [Fact]
    public void FinishStuckTicket_ODEMESI_EKSIK_fise_dokunmaz()
    {
        var g = new FakeGmp();
        g.Codes["Start"] = GmpCodes.AlreadyDone;
        g.Ticket = new GmpTicket(2380, 990, 1, GmpPaymentTypes.Cash,
            TransactionFlags: TicketFlags.TotalsAndPaymentsPrinted);
        var snap = new FakeSnapshots();
        snap.BindOpenTicket("t1", "oturum-A");
        var t = new GmpTerminalTransport(g, snap);

        var s = ((IStuckTicketFinisher)t).FinishStuckTicket(uygula: true);

        // ← ÇİVİ (şart ③): kalanı almadan basıp kapatmak, ÖDENMEMİŞ bir tutarı ödenmiş gibi
        // mali hafızaya yazmak olurdu. Otomatik yol burada DURMALI.
        Assert.False(s.Kapandi);
        Assert.Equal(TicketFinish.OdemeTamamlanmamis, s.Plan.Neden);
        Assert.Equal(new[] { "Start", "OptionFlags", "GetTicket" }, g.Calls);
        Assert.NotNull(snap.ReadOpenTicketBinding("t1"));   // ← bağ DURUYOR, silinmedi
    }

    [Fact]
    public async Task BUTCE_bittiyse_baskiya_HIC_GIRILMEZ_ve_AYRI_sebep_doner()
    {
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        // Saat her okunuşta 60 sn atlıyor: bütçe ödeme biter bitmez tükeniyor.
        long tik = 0;
        var t = new GmpTerminalTransport(g, snap, tik: () => tik += 60_000,
            butce: TimeSpan.FromSeconds(30));

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        // ← ÇİVİ: para ALINDI, sonuç `Approved` KALIR.
        Assert.Equal(TransportOutcome.Approved, r.Outcome);
        Assert.Equal("OPEN", r.TicketState);
        // ← ÇİVİ: "cihaza gittik cevap gelmedi" (PRINT_TIMEOUT) ile "cihaza HİÇ GİTMEDİK" AYRI
        // olgular. İkincisinde o adımın gerçekleşmediği KESİN; tek sebebe katlamak kesin olanı
        // belirsiz göstermek olurdu.
        Assert.Equal(RestomenumReasons.PrintBudgetExhausted, r.Reason);
        Assert.Equal("PrintTotalsAndPayments", r.PrintFailedStep);
        // ← ÇİVİ: cihaza GERÇEKTEN gidilmedi.
        Assert.DoesNotContain("PrintTotalsAndPayments", g.Calls);
        Assert.DoesNotContain("PrintMF", g.Calls);
        Assert.DoesNotContain("Close", g.Calls);
        // ← ÇİVİ: fiş açık kaldı ve KURTARMA için işaretlendi — bütçe kesintisi arızayı
        // saklamıyor, kuyruğa alıyor.
        Assert.Single(snap.Bekleyen);
    }

    [Fact]
    public async Task Butce_PrintMF_TEKRARLARI_arasinda_biterse_TEKRAR_DENENMEZ()
    {
        // ← ÇİVİ: `PrintMF`'in ÜÇ denemesi zincirin EN BÜYÜK tek kalemiydi (3 × 100 sn = 300 sn)
        // ve W104'ten ÖNCE de öyleydi — kasanın 180 sn'lik kapısını asıl aşan buydu. Tekrarlar
        // bütçeden muaf tutulsaydı bütçe koymanın anlamı kalmazdı.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(990, 0, 0, 0);
        g.AfterPayment = new GmpTicket(990, 990, 1, GmpPaymentTypes.Cash);
        g.PrintMfFailures = 3;                   // üçü de başarısız olacaktı
        long tik = 0;
        // Her saat okumasında 1 sn ilerliyor; bütçe 4,5 sn. İki baskı adımı geçer, `PrintMF`
        // BİR kez çalışır, ikinci tekrar için bütçe kalmaz.
        var t = new GmpTerminalTransport(g, snap, tik: () => tik += 1_000,
            butce: TimeSpan.FromMilliseconds(4_500));

        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash,
            oturum: "oturum-A", satisToplam: 990));

        // ← ÇİVİ: ÜÇ değil BİR. Bütçe tekrarları da kısıtlıyor.
        Assert.Equal(1, g.Calls.Count(x => x == "PrintMF"));
    }

    [Fact]
    public async Task Butce_BOLKEN_adimlara_TAVAN_gecilir_kalan_degil()
    {
        var (t, g) = TamOdemeKur();

        await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 990));

        // ← ÇİVİ: bütçe bolken davranış DEĞİŞMEMELİ — her adım kendi tavanını alır. Bütçe bir
        // kısıt, bir hedef değil; normal satışı kısaltmak yazıcıyı boş yere zorlamak olurdu.
        var baski = g.Sureler.Where(x => x.Ad != "Close").ToList();
        Assert.NotEmpty(baski);
        Assert.All(baski, x => Assert.Equal(100_000, x.Sure));
        // `Close` bütçeden MUAF: baskı bitti, geriye yalnız fişi kapatmak kaldı ve onu bütçe
        // yüzünden atlamak basılmış bir fişi AÇIK bırakmak olurdu.
        Assert.Contains(g.Sureler, x => x.Ad == "Close" && x.Sure is null);
    }

    [Fact]
    public async Task Zincir_TAMAMLANIRSA_sebep_YOKTUR()
    {
        var (t, _) = TamOdemeKur();

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 990));

        // ← ÇİVİ: olağan satışa sebep iliştirmek, her başarılı fişi platformda arıza gibi
        // gösterirdi (`reason` "neden başarısız" sorusunun cevabıdır).
        Assert.Equal("CLOSED", r.TicketState);
        Assert.Null(r.Reason);
        Assert.Null(r.PrintFailedStep);
        Assert.Null(r.PrintFailedCode);
    }

    [Fact]
    public async Task KISMI_odemede_baski_patlarsa_CELISKI_olusmaz_ama_sebep_YINE_gelir()
    {
        // ← ÇİVİ: bu testin var olma sebebi kasanın uyarısı. Kısmi ödemede `kalan` zaten 0
        // değildir; "kalan 0 ama fiş açık" çelişkisi HİÇ oluşmaz. Sebep alanı olmasaydı arıza
        // kasada tamamen görünmez kalırdı.
        var g = new FakeGmp();
        var snap = new FakeSnapshots();
        g.Ticket = new GmpTicket(2380, 0, 0, 0);
        g.AfterPayment = new GmpTicket(2380, 990, 1, GmpPaymentTypes.Cash);   // kısmi
        var t = new GmpTerminalTransport(g, snap);

        var r = await t.SaleAsync(Req(amount: 990, paymentType: GmpPaymentTypes.Cash, satisToplam: 2380));

        // Fiş zaten AÇIK kalır (kısmi ödeme Türkiye'de NORMAL) — ve baskı hiç çalışmadığı için
        // sebep de yoktur. Çelişki yolu burada kör: kasa bir sorun OLMADIĞINI da göremezdi.
        Assert.Equal("OPEN", r.TicketState);
        Assert.Null(r.Reason);
        Assert.Equal(1390, r.DeviceRemainingMinor);
    }
}
