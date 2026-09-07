namespace Restomenum.Agent.Core;

/// <summary>
/// **Türkiye / Ingenico GMP-3 taşıması** — <see cref="IGmpWrapper"/> üzerinden mali akışı sürer.
///
/// <para>Bu sınıf sertifikasyon sınırının <b>dışındadır</b> (§8.3b, karar C): sıra, kurtarma ve
/// hata yorumu burada; sarmalayıcı yalnız çağrıyı geçirir. Böylece bir kurtarma dalını düzeltmek
/// yeniden sertifikasyon gerektirmez.</para>
///
/// ## Ödeme modeli — <see cref="PaymentModel.Incremental"/>
///
/// Türkiye'de kalemler girildikten sonra ödeme <b>parça parça</b> eklenir (20 ₺ nakit + 30 ₺ kart).
/// Ama fiş <b>yarım ödenmiş olarak KAPANAMAZ</b>: ya tamamı ödenir ve kapanır, ya da yarım ödenen
/// de dahil her şey iptal edilir. Yani kısmi ödeme <b>geçici bir aradurumdur</b>, kalıcı bir
/// sonuç değil.
///
/// <para>Bunun doğrudan sonucu: <b>bu komutun başarısı fişin kapanmasına bağlı değildir.</b>
/// Başarı "benim ödemem işlendi mi"dir. Fişin kapanması, tutar tamamlandığında olur.</para>
///
/// ## Belirsizlik: anlık görüntü farkı
///
/// <see cref="ProbeAsync"/> tutar karşılaştırmasına <b>güvenmez</b>: 20 ₺ + 20 ₺ ödenmiş bir fişte
/// tutar farkı iki ödemeyi ayırt edemez. Ödeme <b>sayacı</b> ayırt eder. Bu yüzden ödeme öncesi
/// fişin anlık görüntüsü alınır ve belirsizlik o farkla çözülür.
/// </summary>
public sealed class GmpTerminalTransport : ITerminalTransport
{
    private readonly IGmpWrapper _gmp;
    private readonly Action<string, object?> _log;

    /// <summary>
    /// Ödeme öncesi anlık görüntü — belirsizliği çözen tek dayanak. <b>Kalıcı</b> olmalı: bellekte
    /// tutulsaydı süreç kart penceresinde öldüğünde görüntü de ölür ve çözülebilir bir vaka
    /// gereksiz yere insana çıkardı.
    /// </summary>
    private readonly ITicketSnapshotStore? _snapshots;

    private readonly object _gate = new();
    private ulong _handle;

    public GmpTerminalTransport(
        IGmpWrapper gmp, ITicketSnapshotStore? snapshots = null, Action<string, object?>? log = null)
    {
        _gmp = gmp;
        _snapshots = snapshots;
        _log = log ?? ((_, _) => { });
    }

    public PaymentModel Model => PaymentModel.Incremental;

    /// <summary>
    /// Bir ödeme alır. Fiş yoksa açar, kalemleri yazar, ödemeyi gönderir; tutar tamamlandıysa
    /// basar ve kapatır.
    /// </summary>
    public Task<TransportResult> SaleAsync(SaleRequest request, CancellationToken ct = default) =>
        Task.Run(() => Sale(request), ct);

    private TransportResult Sale(SaleRequest request)
    {
        // FAIL-CLOSED: ÖKC kalemsiz komut kabul etmez ve kalem UYDURULAMAZ — uydurulan bir döküm
        // yanlış departmana mali kayıt yazar ve bu geri alınamaz (§20.2).
        if (request.FiscalLines is null || request.FiscalLines.Count == 0)
            return Hata(TransportOutcome.Declined, "FISCAL_LINES_REQUIRED", "PaymentRestriction",
                RestomenumReasons.FiscalLinesRequired);

        // Departmanı eklenti çözer (§7.2b). Negatif = eşlenmemiş → terminale GİTMEDEN reddedilir;
        // tahmin edilmiş bir departman yanlış mali kayıt yazar ve geri alınamaz.
        var eksik = request.FiscalLines.FirstOrDefault(l => l.DepartmentNo < 0);
        if (eksik is not null)
            return Hata(TransportOutcome.Declined, $"PRODUCT_UNMAPPED:{eksik.ProductId}", "PaymentRestriction",
                RestomenumReasons.ProductUnmapped);

        string? bilgi = null;
        var r = _gmp.Start(out var handle);

        if (r.Code == GmpCodes.AlreadyDone)
        {
            // ÖNCEKİ denemeden kalan açık fiş. Bu denemede ödeme HİÇ başlamadı; asıl soru
            // "o fişte para var mı".
            var engel = BayatFisiTemizle(handle, request.CommandId);
            if (engel is not null) return engel;      // temizlenemedi → dokunulmadı, kesin ret
            bilgi = RestomenumReasons.StaleTicketCleared;
            r = _gmp.Start(out handle);               // temizlendi → fişi ŞİMDİ aç
        }

        if (!r.Ok) return Cevir(r, "Start", GmpStep.BeforePayment);
        _handle = handle;

        // GmpTicketTypes.Sale (1). Burada 0 (`TasnifDisi`) yazıyordu ve **fiş hiç açılamıyordu**:
        // canlı terminalde `TicketHeader(0)` → 0x0008 EKÜ_PROBLEM, `TicketHeader(1)` → 0x0000 OK.
        r = _gmp.TicketHeader(handle, GmpTicketTypes.Sale);
        if (!r.Ok) return TemizleVeCevir(handle, r, "TicketHeader", GmpStep.BeforePayment);

        // Fişi GÜVENİLİR okuyabilmek için bayraklar burada set edilir; tek başına `GetTicket`
        // ödeme detayını eksik döndürebilir ve kurtarma o alana dayanır.
        r = _gmp.OptionFlags(handle, GmpEchoFlags.Reload);
        if (!r.Ok) return TemizleVeCevir(handle, r, "OptionFlags", GmpStep.BeforePayment);

        foreach (var l in request.FiscalLines)
        {
            r = _gmp.ItemSale(handle, new GmpItem(l.Name, l.UnitPriceMinor, l.Quantity, l.DepartmentNo), out _);
            if (!r.Ok) return TemizleVeCevir(handle, r, "ItemSale", GmpStep.BeforePayment);
        }

        // ── ANLIK GÖRÜNTÜ: belirsizlik çözümünün tek dayanağı ────────────────────
        if (_gmp.GetTicket(handle, out var once).Ok)
        {
            _snapshots?.SaveSnapshot(request.CommandId,
                once.TotalAmountMinor, once.PaidAmountMinor, once.PaymentCount);
        }

        // ── ÖDEME: kartta 20–32 sn bloke eder ────────────────────────────────────
        var pr = _gmp.Payment(handle, new GmpPaymentRequest(request.AmountMinor, request.PaymentType), out var tk);

        if (!pr.Ok)
        {
            // `FP3_Payment` çağrıldı: sonuç ne olursa olsun **tekrar GÖNDERİLMEZ**; çözüm
            // `ProbeAsync`'te, terminale sorarak. Sınıf ve nexo koşulu `GmpErrorMap`'ten gelir —
            // burada kod okunup yorumlanmaz (2085'i "kesin ret" yapan hata tam da buydu).
            var cevap = Cevir(pr, "Payment", GmpStep.Payment);
            _log("[gmp] ödeme başarısız/yanıtsız", new
            {
                request.CommandId,
                gmp = pr.ToString(),
                sinif = cevap.Outcome.ToString(),
                errorCondition = cevap.ErrorCondition,
            });
            return cevap;
        }

        // Ödeme işlendi. Fiş tamamlandıysa basılır ve kapatılır; tamamlanmadıysa AÇIK bırakılır —
        // kasiyer kalanı ekleyecek. Fişi burada kapatmak, yarım ödenmiş fiş üretmek olurdu.
        if (tk.IsFullyPaid)
        {
            var kapanis = Kapat(handle);
            if (kapanis is not null) return kapanis;
        }

        return new TransportResult(
            TransportOutcome.Approved,
            ApprovedAmountMinor: request.AmountMinor,
            Rrn: tk.Rrn,
            CardLast4: tk.CardLast4,
            ProviderResultCode: pr.ToString(),
            Info: bilgi);
    }

    /// <summary>
    /// Önceki denemeden kalan açık fişi <b>KANITLA</b> temizler. <c>null</c> dönerse fiş gitti ve
    /// satışa devam edilebilir; dolu dönerse <b>dokunulmadı</b> ve bu deneme kesin retle biter.
    ///
    /// <para><b>Neden otomatik:</b> her başarısız kart bir açık fiş bırakıyor ve o fiş sonraki HER
    /// satışı bloke ediyor. 2026-09-07'de bu tam 14 saat sürdü; kasiyer art arda "operatöre danışın"
    /// gördü ve terminal elle temizlenene kadar satış yapılamadı. Kasiyerin bir operatör beklemesi
    /// kabul edilebilir bir tasarım değil.</para>
    ///
    /// <para><b>Neden yine de güvenli:</b> silme kararını biz vermiyoruz, <b>cihazın kendi defteri</b>
    /// veriyor. Ödeme sayacı 0 değilse ya da fiş okunamıyorsa dokunulmaz. Okumadan silmek, üzerinde
    /// tahsilat olan bir fişi yok etmek olurdu — geri alınamaz ve para kaybıdır.</para>
    /// </summary>
    private TransportResult? BayatFisiTemizle(ulong handle, string commandId)
    {
        TransportResult Dokunma(string neden)
        {
            _log("[gmp] bayat fiş temizlenmedi — dokunulmadı", new { commandId, neden });
            try { _gmp.Close(handle); } catch { /* en iyi çaba */ }
            lock (_gate) _handle = 0;
            return new TransportResult(TransportOutcome.Declined,
                ProviderResultCode: $"TICKET_ALREADY_OPEN:{neden}", ErrorCondition: "PaymentRestriction",
                PaymentInvoked: false, Reason: RestomenumReasons.TicketAlreadyOpen);
        }

        var of = _gmp.OptionFlags(handle, GmpEchoFlags.Reload);
        var gt = _gmp.GetTicket(handle, out var fis);
        if (!of.Ok || !gt.Ok) return Dokunma($"OKUNAMADI:{of}/{gt}");

        // DENETİM İZİ: silinen şeyin ne olduğu, silinmeden ÖNCE yazılır. Sonra yazılsaydı, silme
        // sırasında süreç ölürse ne sildiğimizi kimse bilemezdi.
        _log("[gmp] bayat fiş bulundu", new
        {
            commandId, toplam = fis.TotalAmountMinor, tahsil = fis.PaidAmountMinor,
            odemeSayisi = fis.PaymentCount, bankaBacagi = fis.HasBankLeg,
        });

        // `PaymentCount < 0` = BOZUK okuma (sarmalayıcının sınır koruması). Bunu "ödeme yok" saymak
        // en kötü hata olurdu: okunamayan bir fişi silmek.
        if (fis.PaymentCount != 0) return Dokunma($"ODEME_VAR:{fis.PaymentCount}");
        if (fis.HasBankLeg) return Dokunma("BANKA_BACAGI");

        var vr = _gmp.VoidAll(handle, out _);
        if (!vr.Ok) return Dokunma($"VOIDALL:{vr}");

        _gmp.Close(handle);
        lock (_gate) _handle = 0;
        _log("[gmp] bayat fiş temizlendi (ödeme yoktu, kanıtlandı) — satışa devam", new { commandId });
        return null;
    }

    /// <summary>Baskı + kapatma dizisi. <c>PrintMF</c> sahada 3 denemeye kadar tekrarlanıyor.</summary>
    private TransportResult? Kapat(ulong handle)
    {
        foreach (var (ad, cagri) in new (string, Func<GmpResult>)[]
        {
            ("PrintTotalsAndPayments", () => _gmp.PrintTotalsAndPayments(handle)),
            ("PrintBeforeMF", () => _gmp.PrintBeforeMF(handle)),
            ("PrintUserMessage", () => _gmp.PrintUserMessage(handle)),
        })
        {
            var r = cagri();
            // ⚠️ Baskı başarısız olsa bile ödeme ALINMIŞTIR. Burada `Declined` dönmek, alınmış bir
            // parayı "reddedildi" diye raporlamak olurdu — yanlış yön, tehlikeli yön.
            if (!r.Ok) { _log("[gmp] baskı adımı başarısız (ödeme ALINDI)", new { ad, code = r.ToString() }); return null; }
        }

        for (var i = 0; i < 3; i++)
        {
            if (_gmp.PrintMF(handle).Ok) break;
            _log("[gmp] PrintMF tekrar", new { deneme = i + 1 });
        }
        _gmp.Close(handle);
        lock (_gate) _handle = 0;
        return null;
    }

    /// <summary>
    /// Fişi okur. <b>İki yol:</b> tanıtıcı elimizdeyse `OptionFlags`+`GetTicket`; yoksa (servis
    /// yeniden başlamış) `Start` ile yoklanır ve <see cref="GmpCodes.AlreadyDone"/> "açık fiş var"
    /// demektir. İkinci yol olmazsa yeniden başlatma sonrası belirsizlik çözümü tamamen çöker.
    /// </summary>
    public Task<TicketState> ReadTicketAsync(CancellationToken ct = default) => Task.Run(ReadTicket, ct);

    private TicketState ReadTicket()
    {
        ulong h;
        lock (_gate) h = _handle;

        if (h == 0)
        {
            var durum = TanitciyiYenile();
            if (durum is not null) return durum;   // açık fiş yok
            lock (_gate) h = _handle;
        }

        var oku = Oku(h);
        if (oku is not null) return oku;

        // ── BAYAT TANITICI KURTARMASI ───────────────────────────────────────────────
        // Elimizdeki tanıtıcı cihaz tarafında geçersizleşmiş (2317). Bu ölçüldü: 2026-09-06
        // 01:50 ve 2026-09-07 14:30'daki denemelerde yoklama 6 turun 6'sında 2317 aldı ve
        // belirsizlik ÇÖZÜLEMEDİ — tam da en gerekli anda. Tanıtıcıyı yenileyip BİR KEZ daha
        // dene; `FP3_Start` açık fiş varsa 2080 döner ve hTrx'i AÇIK FİŞİN tanıtıcısıyla
        // doldurur (sertifikalı DLLController.GetTicket ile birebir aynı kurtarma).
        _log("[gmp] tanıtıcı bayat (2317) — yenileniyor", new { eski = h });
        var yenidenDurum = TanitciyiYenile();
        if (yenidenDurum is not null) return yenidenDurum;

        ulong yeniH;
        lock (_gate) yeniH = _handle;
        var ikinci = Oku(yeniH);
        if (ikinci is not null) return ikinci;

        // Yenilenmiş tanıtıcıyla da okunamadı: TAHMİN YOK.
        throw new TerminalBusyException("fiş okunamadı: tanıtıcı yenilendi ama GetTicket yine başarısız");
    }

    /// <summary>
    /// Fişi verilen tanıtıcıyla okur. <c>null</c> = tanıtıcı geçersiz (çağıran yenilemeli).
    /// <c>RECV_BUSY</c> ise <see cref="TerminalBusyException"/> atar (meşguliyet, geçersizlik değil).
    /// </summary>
    private TicketState? Oku(ulong h)
    {
        var of = _gmp.OptionFlags(h, GmpEchoFlags.Reload);
        if (of.Code == GmpCodes.RecvBusy) throw new TerminalBusyException();
        if (BayatTanitici(of.Code)) return null;

        var r = _gmp.GetTicket(h, out var tk);
        if (r.Code == GmpCodes.RecvBusy) throw new TerminalBusyException();
        if (BayatTanitici(r.Code)) return null;
        if (!r.Ok) throw new TerminalBusyException($"GetTicket {r}");
        return Cevir(tk, acik: true);
    }

    private static bool BayatTanitici(uint code) =>
        code == GmpCodes.InvalidHandle || code == GmpCodes.NoHandle;

    /// <summary>
    /// <c>FP3_Start</c> yoklamasıyla tanıtıcıyı tazeler. Dönüş <c>null</c> ise <b>açık fiş VAR</b> ve
    /// tanıtıcısı <c>_handle</c>'a yazıldı; dolu dönerse okunacak fiş yoktur.
    /// </summary>
    private TicketState? TanitciyiYenile()
    {
        var probe = _gmp.Start(out var yeni);

        if (probe.Code == GmpCodes.AlreadyDone)
        {
            // ⚠️ Burada eskiden `TotalAmountMinor: 0, PaidAmountMinor: 0` dönülüyordu — yani
            // "açık fiş var ama içini bilmiyoruz". `FP3_Start` 2080'de bile hTrx'i AÇIK FİŞİN
            // tanıtıcısıyla dolduruyor (GmpWrapper.Start: `handle = hTrx`, rc'ye BAKMADAN), yani
            // içerik OKUNABİLİRDİ. Okumamak, belirsizlik çözümünü kör bırakıyordu.
            lock (_gate) _handle = yeni;
            return null;
        }

        if (!probe.Ok) throw new TerminalBusyException($"fiş okunamadı: {probe}");

        // Açık fiş yoktu; yoklama YENİ fiş açtı — bırakmayız, bir sonraki satışı bozar.
        _gmp.Close(yeni);
        lock (_gate) _handle = 0;
        return new TicketState(HasOpenTicket: false, TotalAmountMinor: 0, PaidAmountMinor: 0);
    }

    /// <summary>
    /// "BENİM ödemem işlendi mi?" — <b>ödeme sayacı farkıyla</b>. Tutar karşılaştırması iki eşit
    /// ödemeyi (20 ₺ + 20 ₺) ayırt edemez ve yanlış "işlendi" der.
    /// </summary>
    public Task<PaymentProbe> ProbeAsync(SaleRequest request, CancellationToken ct = default) =>
        Task.Run(() => Probe(request), ct);

    private PaymentProbe Probe(SaleRequest request)
    {
        var once = _snapshots?.ReadSnapshot(request.CommandId);
        var simdi = ReadTicket();

        // BOZUK OKUMA SAVUNMASI. Sarmalayıcı, fiş dizisinin sınırını aşan bir ödeme sayacı
        // gördüğünde `PaymentCount = -1` bildirir. Bu değeri normal bir sayı gibi ele almak
        // ölümcül olurdu: karşılaştırma "sayaç arttı" der ve **gerçekleşmemiş bir ödeme
        // `Landed` sayılır** — yani para hareket etmemişken tahsilat yazılır. Bozuk veriyle
        // karar vermek yerine belirsiz denir ve insana çıkar.
        if (simdi.PaymentCount < 0)
        {
            return new PaymentProbe(ProbeVerdict.Indeterminate,
                Note: "fiş okuması bozuk (ödeme sayacı geçersiz)");
        }

        if (!simdi.HasOpenTicket)
        {
            // Fiş yok. İki ihtimal var ve **ayırt edemeyiz**: ya ödeme hiç işlenmedi, ya işlendi ve
            // fiş kapandı. Kapanmış olsaydı tutar tamamlanmış demektir — ama bunu kanıtlayamıyoruz.
            // Tahmin yerine belirsiz denir; "işlenmedi" demek çift tahsilat riskidir.
            if (once is null || once.Value.PaymentCount == 0)
                return new PaymentProbe(ProbeVerdict.NotLanded, Note: "açık fiş yok, önceki ödeme de yok");
            return new PaymentProbe(ProbeVerdict.Indeterminate, Note: "fiş kapanmış — akıbet okunamıyor");
        }

        if (once is not null && simdi.PaymentCount > once.Value.PaymentCount)
        {
            // ÇELİŞKİ SAVUNMASI: sayaç arttı ama ödenen tutar artmadı (delta ≤ 0). "Landed" = para
            // HAREKET ETTİ demek; 0/negatif tutarla Landed dönmek sahte-onay üretir (Success +
            // AuthorizedAmount 0). Canlı ölçüldü: başarısız kart bacağında sayaç artıp tutar
            // artmayabiliyor. Böyle bir okuma güvenilmez — belirsiz de, insana çıksın.
            var delta = simdi.PaidAmountMinor - once.Value.PaidMinor;
            if (delta <= 0)
                return new PaymentProbe(ProbeVerdict.Indeterminate,
                    RemainingMinor: simdi.RemainingMinor,
                    Note: $"ödeme sayacı arttı ama tutar artmadı (delta {delta}) — çelişkili okuma");
            return new PaymentProbe(ProbeVerdict.Landed,
                ApprovedAmountMinor: delta,
                RemainingMinor: simdi.RemainingMinor, Rrn: simdi.Rrn, CardLast4: simdi.CardLast4);
        }

        if (once is not null && simdi.PaymentCount == once.Value.PaymentCount)
            return new PaymentProbe(ProbeVerdict.NotLanded, RemainingMinor: simdi.RemainingMinor);

        // Anlık görüntü yok (agent yeniden başlamış). Sayaç varsa ödeme İŞLENMİŞ olabilir ama
        // BİZİM ödememiz olduğunu söyleyemeyiz — bu yüzden belirsiz.
        if (simdi.PaymentCount > 0)
            return new PaymentProbe(ProbeVerdict.Indeterminate,
                RemainingMinor: simdi.RemainingMinor, Note: "anlık görüntü yok, ödeme sahibi belirsiz");

        return new PaymentProbe(ProbeVerdict.NotLanded, RemainingMinor: simdi.RemainingMinor);
    }

    public Task<bool> EchoAsync(CancellationToken ct = default) => Task.Run(() => _gmp.Echo().Ok, ct);

    /// <summary>
    /// Fişi iptal eder. <b>Yol ödeme tipine göre ayrılır</b> (§8.3d, canlı terminalde doğrulandı):
    /// nakit doğrudan <c>VoidAll</c> (~1,5–2 sn), kart önce banka ters işlemi ister.
    /// </summary>
    public Task<TransportResult> VoidAsync(CancellationToken ct = default) => Task.Run(Void, ct);

    private TransportResult Void()
    {
        ulong h;
        lock (_gate) h = _handle;
        if (h == 0) return new TransportResult(TransportOutcome.Declined, ProviderResultCode: "NO_OPEN_TICKET", ErrorCondition: "NotAllowed");

        var r = _gmp.VoidAll(h, out _);
        if (r.Ok) { _gmp.Close(h); lock (_gate) _handle = 0; return new TransportResult(TransportOutcome.Approved); }

        if (r.Code == GmpCodes.CannotVoid)
        {
            // `PrintBeforeMF` geçilmiş; fiş mali hafızada. İptal artık MÜMKÜN DEĞİL.
            return new TransportResult(TransportOutcome.Declined, ProviderResultCode: "ALREADY_FISCALIZED", ErrorCondition: "PaymentRestriction",
                PaymentInvoked: false, Reason: RestomenumReasons.AlreadyFiscalized);
        }

        if (r.Code != GmpCodes.PaymentFound)
            return new TransportResult(TransportOutcome.Unknown, ProviderResultCode: r.ToString());

        // 2069 = fişte BANKA ödemesi var. ⚠️ Bu yol SAHADA HİÇ ÇALIŞMADI ve ölçülmedi;
        // `VoidPayment` imzası tahminidir. Başarısız olursa `REVERSAL_FAILED` (§7.6a): para
        // hareket etti, geri alınamadı — **tekrar denenmez**, insana gider.
        if (!_gmp.GetTicket(h, out var tk).Ok)
            return new TransportResult(TransportOutcome.Unknown, ProviderResultCode: "VOID_READ_FAILED");

        for (var i = tk.PaymentCount - 1; i >= 0; i--)
        {
            var vp = _gmp.VoidPayment(h, i);
            if (!vp.Ok)
            {
                _log("[gmp] REVERSAL_FAILED — banka ters işlemi başarısız", new { index = i, code = vp.ToString() });
                return new TransportResult(TransportOutcome.Unknown, Rrn: tk.Rrn, ProviderResultCode: $"REVERSAL_FAILED:{vp}");
            }
        }

        var son = _gmp.VoidAll(h, out _);
        if (!son.Ok)
            return new TransportResult(TransportOutcome.Unknown, Rrn: tk.Rrn, ProviderResultCode: $"REVERSAL_FAILED:{son}");

        _gmp.Close(h);
        lock (_gate) _handle = 0;
        return new TransportResult(TransportOutcome.Approved);
    }

    // ── yardımcılar ─────────────────────────────────────────────────────────

    private static TicketState Cevir(GmpTicket t, bool acik) => new(
        HasOpenTicket: acik, TotalAmountMinor: t.TotalAmountMinor, PaidAmountMinor: t.PaidAmountMinor,
        Rrn: t.Rrn, CardLast4: t.CardLast4, PaymentCount: t.PaymentCount);

    /// <summary>
    /// Terminale <b>hiç gidilmeden</b> üretilen ret. <c>FP3_Payment</c> çağrılmadığı için para
    /// hareket edemez — ama <c>Refusal</c> DEĞİL: banka hiç devrede olmadığından "kart reddedildi"
    /// mesajı yanlış olurdu (W1 kuralı: kesin-ret yalnız host'un açık reddiyle).
    /// </summary>
    private static TransportResult Hata(TransportOutcome o, string kod, string errorCondition, string reason) =>
        new(o, ProviderResultCode: kod, ErrorCondition: errorCondition,
            PaymentInvoked: false, Reason: reason);

    /// <summary>
    /// Ham GMP-3 kodu → taşıma sonucu. <b>Karar burada verilmez</b>; tek eşleme tablosu
    /// <see cref="GmpErrorMap"/>'tedir. Bu metot yalnız adımı ve iz kaydını ekler.
    ///
    /// <para><b>Eski hâli para riskiydi:</b> son satır <c>_ =&gt; Declined</c> idi, yani
    /// <i>kapsanmayan her kod</i> "kesin ret" sayılıyordu. Canlı terminal (banka hattı yokken)
    /// <c>FP3_Payment</c>'tan <b>2085</b> döndürdü, hiçbir dal yakalamadı ve deneme kasaya
    /// <c>Refusal</c> = "kart reddedildi, başka kart isteyin" diye kapandı. Doğru cevap "bilmiyorum,
    /// terminale soralım"dı.</para>
    /// </summary>
    private static TransportResult Cevir(GmpResult r, string adim, GmpStep asama)
    {
        var e = GmpErrorMap.Map(r.Code, asama);
        return new TransportResult(e.Outcome,
            ProviderResultCode: $"{adim}:{r}", ErrorCondition: e.ErrorCondition,
            PaymentInvoked: asama != GmpStep.BeforePayment, Reason: e.Reason);
    }

    private TransportResult TemizleVeCevir(ulong handle, GmpResult r, string adim, GmpStep asama)
    {
        // Yarım açılmış fiş bırakılmaz: bir sonraki `StartTicket` onu SESSİZCE iptal eder ve
        // o sırada üzerinde ödeme varsa para kaybolur.
        _gmp.VoidAll(handle, out _);
        _gmp.Close(handle);
        lock (_gate) _handle = 0;
        return Cevir(r, adim, asama);
    }
}
