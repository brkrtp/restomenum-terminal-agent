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
            return Hata(TransportOutcome.Declined, "FISCAL_LINES_REQUIRED");

        // Departmanı eklenti çözer (§7.2b). Negatif = eşlenmemiş → terminale GİTMEDEN reddedilir;
        // tahmin edilmiş bir departman yanlış mali kayıt yazar ve geri alınamaz.
        var eksik = request.FiscalLines.FirstOrDefault(l => l.DepartmentNo < 0);
        if (eksik is not null)
            return Hata(TransportOutcome.Declined, $"PRODUCT_UNMAPPED:{eksik.ProductId}");

        var r = _gmp.Start(out var handle);
        if (!r.Ok) return Cevir(r, "Start");
        _handle = handle;

        // GmpTicketTypes.Sale (1). Burada 0 (`TasnifDisi`) yazıyordu ve **fiş hiç açılamıyordu**:
        // canlı terminalde `TicketHeader(0)` → 0x0008 EKÜ_PROBLEM, `TicketHeader(1)` → 0x0000 OK.
        r = _gmp.TicketHeader(handle, GmpTicketTypes.Sale);
        if (!r.Ok) return TemizleVeCevir(handle, r, "TicketHeader");

        // Fişi GÜVENİLİR okuyabilmek için bayraklar burada set edilir; tek başına `GetTicket`
        // ödeme detayını eksik döndürebilir ve kurtarma o alana dayanır.
        r = _gmp.OptionFlags(handle, GmpEchoFlags.Reload);
        if (!r.Ok) return TemizleVeCevir(handle, r, "OptionFlags");

        foreach (var l in request.FiscalLines)
        {
            r = _gmp.ItemSale(handle, new GmpItem(l.Name, l.UnitPriceMinor, l.Quantity, l.DepartmentNo), out _);
            if (!r.Ok) return TemizleVeCevir(handle, r, "ItemSale");
        }

        // ── ANLIK GÖRÜNTÜ: belirsizlik çözümünün tek dayanağı ────────────────────
        if (_gmp.GetTicket(handle, out var once).Ok)
        {
            _snapshots?.SaveSnapshot(request.CommandId,
                once.TotalAmountMinor, once.PaidAmountMinor, once.PaymentCount);
        }

        // ── ÖDEME: kartta 20–32 sn bloke eder ────────────────────────────────────
        var pr = _gmp.Payment(handle, new GmpPaymentRequest(request.AmountMinor, request.PaymentType), out var tk);

        if (GmpCodes.IsTimeout(pr.Code) || pr.Code == GmpCodes.RecvBusy)
        {
            // Cevap yok. **Tekrar GÖNDERİLMEZ** — çözüm `ProbeAsync`'te, terminale sorarak.
            _log("[gmp] ödeme yanıtsız", new { request.CommandId, code = pr.ToString() });
            return new TransportResult(TransportOutcome.Unknown, ProviderResultCode: pr.ToString());
        }
        if (!pr.Ok) return Cevir(pr, "Payment");

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
            ProviderResultCode: pr.ToString());
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
            var probe = _gmp.Start(out var yeni);
            if (probe.Code == GmpCodes.AlreadyDone)
            {
                // Açık fiş VAR ama tanıtıcısı bizde değil; içeriğini okuyamayız.
                return new TicketState(HasOpenTicket: true, TotalAmountMinor: 0, PaidAmountMinor: 0);
            }
            if (!probe.Ok) throw new TerminalBusyException($"fiş okunamadı: {probe}");
            // Yoklama için açtığımız fişi bırakmayız — açık fiş bir sonraki satışı bozar.
            _gmp.Close(yeni);
            return new TicketState(HasOpenTicket: false, TotalAmountMinor: 0, PaidAmountMinor: 0);
        }

        var of = _gmp.OptionFlags(h, GmpEchoFlags.Reload);
        if (of.Code == GmpCodes.RecvBusy) throw new TerminalBusyException();
        var r = _gmp.GetTicket(h, out var tk);
        if (r.Code == GmpCodes.RecvBusy) throw new TerminalBusyException();
        if (!r.Ok) throw new TerminalBusyException($"GetTicket {r}");
        return Cevir(tk, acik: true);
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
            return new PaymentProbe(ProbeVerdict.Landed,
                ApprovedAmountMinor: simdi.PaidAmountMinor - once.Value.PaidMinor,
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
        if (h == 0) return new TransportResult(TransportOutcome.Declined, ProviderResultCode: "NO_OPEN_TICKET");

        var r = _gmp.VoidAll(h, out _);
        if (r.Ok) { _gmp.Close(h); lock (_gate) _handle = 0; return new TransportResult(TransportOutcome.Approved); }

        if (r.Code == GmpCodes.CannotVoid)
        {
            // `PrintBeforeMF` geçilmiş; fiş mali hafızada. İptal artık MÜMKÜN DEĞİL.
            return new TransportResult(TransportOutcome.Declined, ProviderResultCode: "ALREADY_FISCALIZED");
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

    private static TransportResult Hata(TransportOutcome o, string kod) =>
        new(o, ProviderResultCode: kod);

    private static TransportResult Cevir(GmpResult r, string adim) => r.Code switch
    {
        GmpCodes.AlreadyDone => new TransportResult(TransportOutcome.TicketAlreadyOpen, ProviderResultCode: $"{adim}:{r}"),
        GmpCodes.RecvBusy => new TransportResult(TransportOutcome.Busy, ProviderResultCode: $"{adim}:{r}"),
        _ when GmpCodes.IsTimeout(r.Code) => new TransportResult(TransportOutcome.Unknown, ProviderResultCode: $"{adim}:{r}"),
        // Ödeme ÖNCESİ adımların hatası para hareketi üretmez; kesin ret olarak raporlanır.
        _ => new TransportResult(TransportOutcome.Declined, ProviderResultCode: $"{adim}:{r}"),
    };

    private TransportResult TemizleVeCevir(ulong handle, GmpResult r, string adim)
    {
        // Yarım açılmış fiş bırakılmaz: bir sonraki `StartTicket` onu SESSİZCE iptal eder ve
        // o sırada üzerinde ödeme varsa para kaybolur.
        _gmp.VoidAll(handle, out _);
        _gmp.Close(handle);
        lock (_gate) _handle = 0;
        return Cevir(r, adim);
    }
}
