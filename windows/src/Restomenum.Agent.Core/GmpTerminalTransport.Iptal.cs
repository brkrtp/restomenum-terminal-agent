namespace Restomenum.Agent.Core;

/// <summary>
/// <b>İPTAL</b> — <see cref="GmpTerminalTransport"/>'un parçası (W43 bölmesi).
///
/// Ödemenin ve fişin iptali. Yol ödeme tipine göre ayrılır (canlı terminalde doğrulandı):
/// nakit doğrudan `VoidAll`, kart önce banka ters işlemi ister.
///
/// <para><b>Neden `partial`, ayrı sınıf değil:</b> bu metotlar `_handle` (cihaz tanıtıcısı)
/// ve onu koruyan `_gate` kilidi üzerinde ORTAK ÇALIŞIYOR. Ayrı sınıflara bölmek o değişken
/// durumu sınıflar arasında paylaştırmak demekti — bölmenin amacı okunabilirlikti, yeni bir
/// paylaşım yüzeyi açmak değil. `partial` ile derlenen tür AYNI kalıyor: davranış değişmez.</para>
/// </summary>
public sealed partial class GmpTerminalTransport
{
    /// <summary>
    /// Fişi iptal eder. <b>Yol ödeme tipine göre ayrılır</b> (§8.3d, canlı terminalde doğrulandı):
    /// nakit doğrudan <c>VoidAll</c> (~1,5–2 sn), kart önce banka ters işlemi ister.
    /// </summary>
    public Task<TransportResult> VoidAsync(CancellationToken ct = default) => Task.Run(Void, ct);

    private TransportResult Void()
    {
        ulong h;
        lock (_gate) h = _handle;

        // TANITICI KURTARMASI: iptal isteği çoğu zaman satıştan SONRA, hatta ajan yeniden
        // başladıktan sonra gelir — tanıtıcı bellekte olmaz. Eskiden burada doğrudan
        // "NO_OPEN_TICKET" dönülüyordu, yani **iptalin en tipik vakası hiç çalışmıyordu.**
        // `FP3_Start` açık fiş varsa 2080 döner ve hTrx'i AÇIK FİŞİN tanıtıcısıyla doldurur.
        if (h == 0)
        {
            var yok = TanitciyiYenile();
            if (yok is not null)
                return new TransportResult(TransportOutcome.Declined,
                    ProviderResultCode: "NO_OPEN_TICKET", ErrorCondition: "NotFound");
            lock (_gate) h = _handle;
        }

        // Denetim izi: neyi iptal ettiğimiz, iptalden ÖNCE yazılır. Aynı okuma kasaya dönecek
        // sayaçların da tek kaynağı — iptalden SONRA fiş yok, sorulacak yer kalmaz.
        long iptalTutar = 0;
        int iptalSayi = 0;
        var fisSahibi = _snapshots?.ReadOpenTicketBinding(_terminalId);
        if (_gmp.OptionFlags(h, GmpEchoFlags.Reload).Ok && _gmp.GetTicket(h, out var oncesi).Ok)
        {
            iptalTutar = oncesi.PaidAmountMinor;
            iptalSayi = oncesi.PaymentCount;
            _log("[gmp] iptal öncesi fiş", new
            {
                toplam = oncesi.TotalAmountMinor, tahsil = oncesi.PaidAmountMinor,
                odemeSayisi = oncesi.PaymentCount, bankaBacagi = oncesi.HasBankLeg,
            });
        }

        var r = _gmp.VoidAll(h, out _);
        if (r.Ok)
        {
            _gmp.Close(h);
            lock (_gate) _handle = 0;
            _snapshots?.ClearOpenTicketBinding(_terminalId);   // fis gitti, bag da gitmeli
            return new TransportResult(TransportOutcome.Approved,
                ApprovedAmountMinor: iptalTutar > 0 ? iptalTutar : null,
                Info: RestomenumReasons.TicketCancelled,
                VoidedPaymentCount: iptalSayi, VoidedAmountMinor: iptalTutar,
                CancelledSaleSessionId: fisSahibi);
        }

        if (r.Code == GmpCodes.CannotVoid)
        {
            // `PrintBeforeMF` geçilmiş; fiş mali hafızada. İptal artık MÜMKÜN DEĞİL.
            return new TransportResult(TransportOutcome.Declined, ProviderResultCode: "ALREADY_FISCALIZED",
                ErrorCondition: "PaymentRestriction", PaymentInvoked: false,
                Reason: RestomenumReasons.AlreadyFiscalized);
        }

        if (r.Code != GmpCodes.PaymentFound)
            return new TransportResult(TransportOutcome.Unknown, ProviderResultCode: r.ToString());

        // 2069 = fişte BANKA ödemesi var. ⚠️ Bu yol SAHADA HİÇ ÇALIŞMADI ve ölçülmedi;
        // `VoidPayment` imzası tahminidir. Başarısız olursa `REVERSAL_FAILED` (§7.6a): para
        // hareket etti, geri alınamadı — **tekrar denenmez**, insana gider.
        if (!_gmp.GetTicket(h, out var tk).Ok)
            return new TransportResult(TransportOutcome.Unknown, ProviderResultCode: "VOID_READ_FAILED",
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete);

        for (var i = tk.PaymentCount - 1; i >= 0; i--)
        {
            var vp = _gmp.VoidPayment(h, i);
            if (!vp.Ok)
            {
                _log("[gmp] REVERSAL_FAILED — banka ters işlemi başarısız", new { index = i, code = vp.ToString() });
                return new TransportResult(TransportOutcome.Unknown, Rrn: tk.Rrn,
                    ProviderResultCode: $"REVERSAL_FAILED:{vp}", ErrorCondition: "InProgress",
                    Reason: RestomenumReasons.VoidIncomplete);
            }
        }

        var son = _gmp.VoidAll(h, out _);
        if (!son.Ok)
            return new TransportResult(TransportOutcome.Unknown, Rrn: tk.Rrn,
                ProviderResultCode: $"REVERSAL_FAILED:{son}", ErrorCondition: "InProgress",
                Reason: RestomenumReasons.VoidIncomplete);

        _gmp.Close(h);
        lock (_gate) _handle = 0;
        _snapshots?.ClearOpenTicketBinding(_terminalId);
        return new TransportResult(TransportOutcome.Approved,
            ApprovedAmountMinor: iptalTutar > 0 ? iptalTutar : null,
            Rrn: tk.Rrn, Info: RestomenumReasons.TicketCancelled);
    }

    /// <summary>
    /// AÇIK FİŞİN TAMAMINI iptal eder (kasiyerin "Fiş İptal" düğmesi).
    ///
    /// <para><b>Sıra:</b> tanıtıcıyı kurtar → fişi OKU (denetim izi, silmeden ÖNCE) →
    /// <c>VoidAll</c> → banka bacağı varsa (2069) <c>VoidPayment</c> → <c>VoidAll</c> →
    /// <c>Close</c> → bağı sil.</para>
    ///
    /// <para><b>Açık fiş yoksa hata DEĞİL:</b> kasiyer düğmeye bastı, iptal edilecek bir şey yoktu.
    /// Cihaza dokunulmaz ve sonuç başarılıdır (<c>TicketWasOpen=false</c>).</para>
    /// </summary>
    public Task<TicketVoidResult> VoidTicketAsync(string terminalId, CancellationToken ct = default) =>
        Task.Run(() => VoidTicket(terminalId), ct);

    private TicketVoidResult VoidTicket(string terminalId)
    {
        ulong h;
        lock (_gate) h = _handle;

        if (h == 0)
        {
            var yok = TanitciyiYenile();
            if (yok is not null)
                return new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: false,
                    ProviderResultCode: "NO_OPEN_TICKET");
            lock (_gate) h = _handle;
        }

        // Denetim izi: neyi iptal ettiğimiz SİLİNMEDEN ÖNCE okunur ve yazılır.
        var of = _gmp.OptionFlags(h, GmpEchoFlags.Reload);
        var gt = _gmp.GetTicket(h, out var fis);
        if (!of.Ok || !gt.Ok)
            return new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                ProviderResultCode: $"TICKET_READ_FAILED:{of}/{gt}");

        var sahibi = _snapshots?.ReadOpenTicketBinding(terminalId);
        // ⚠️ ŞİMDİ oku: aşağıda `ClearOpenTicketBinding` çağrılıyor ve ondan sonra kimlik YOK.
        var iptalEdilenFis = _snapshots?.ReadOpenTicketId(terminalId);
        var sayi = fis.PaymentCount;
        var tutar = fis.PaidAmountMinor;
        _log("[gmp] fiş iptali — iptal öncesi fiş", new
        {
            terminalId, toplam = fis.TotalAmountMinor, tahsil = tutar,
            odemeSayisi = sayi, bankaBacagi = fis.HasBankLeg, sahibi = sahibi ?? "(bağ yok)",
        });

        var vr = _gmp.VoidAll(h, out _);

        if (vr.Code == GmpCodes.PaymentFound)
        {
            // 2069 = fişte BANKA ödemesi var; önce ters işlem gerekiyor.
            // ⚠️ Bu yol sahada HİÇ ölçülmedi (banka hattı yok) — başarısız olursa YARIM kalır ve
            // tekrar denenmez; operatöre gider.
            for (var i = sayi - 1; i >= 0; i--)
            {
                var vp = _gmp.VoidPayment(h, i);
                if (!vp.Ok)
                {
                    _log("[gmp] fiş iptali — banka ters işlemi BAŞARISIZ", new { index = i, code = vp.ToString() });
                    return new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                        VoidedPaymentCount: sayi, VoidedAmountMinor: tutar, CancelledSaleSessionId: sahibi, CancelledTicketId: iptalEdilenFis,
                        ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                        ProviderResultCode: $"REVERSAL_FAILED:{vp}");
                }
            }
            vr = _gmp.VoidAll(h, out _);
        }

        if (!vr.Ok)
            return new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                VoidedPaymentCount: sayi, VoidedAmountMinor: tutar, CancelledSaleSessionId: sahibi, CancelledTicketId: iptalEdilenFis,
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                ProviderResultCode: $"VOIDALL_FAILED:{vr}");

        var kapat = _gmp.Close(h);
        lock (_gate) _handle = 0;
        _snapshots?.ClearOpenTicketBinding(terminalId);

        if (!kapat.Ok)
        {
            // Fiş iptal edildi ama kapatılamadı: durum BELİRSİZ, "olmadı" değil.
            return new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                VoidedPaymentCount: sayi, VoidedAmountMinor: tutar, CancelledSaleSessionId: sahibi, CancelledTicketId: iptalEdilenFis,
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                ProviderResultCode: $"CLOSE_FAILED:{kapat}");
        }

        _log("[gmp] fiş iptal edildi", new { terminalId, odemeSayisi = sayi, tutar, sahibi = sahibi ?? "(bağ yok)" });
        return new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: true,
            VoidedPaymentCount: sayi, VoidedAmountMinor: tutar, CancelledSaleSessionId: sahibi,
            CancelledTicketId: iptalEdilenFis);
    }
}
