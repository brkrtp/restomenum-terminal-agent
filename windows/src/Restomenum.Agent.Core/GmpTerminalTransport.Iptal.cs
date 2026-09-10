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
        if (!_gmp.OptionFlags(h, GmpEchoFlags.Reload).Ok || !_gmp.GetTicket(h, out var oncesi).Ok)
        {
            // ══ W127 — OKUNAMADI, "BOŞ" DEĞİLDİR: OKUYAMIYORSAK İPTAL ETMEYİZ ═══════
            //
            // Eskiden bu okuma BAŞARISIZ olsa da akış devam ediyordu ve `iptalTutar`/`iptalSayi`
            // sıfır KALIYORDU. Sonuç: `VoidAll` başarılı olsaydı fiş iptal edilir ve kasaya
            // <b>"iptal edildi, 0 kuruş"</b> dönerdi. Yani parayı geri alır, geri aldığımızın
            // TEK KAYDINI da yok ederdik — iptalden sonra fiş yok, sorulacak yer kalmaz.
            //
            // Sahada 2026-09-10 16:30'da bu yola girildi: kâğıt bitikken kasadan iptal denendi,
            // okuma patladı (log'da `[gmp] iptal öncesi fiş` satırı YOK) ve akış `VoidAll`a
            // devam etti. Para hareket etmedi çünkü `VoidAll` DA patladı — yani bizi kurtaran
            // şey tasarım değil, cihazın aynı anda tıkalı olmasıydı.
            //
            // ⚠️ Fişte 990 kuruş VARDI. "Ölçemedim" ile "sıfır" arasındaki farkı silen her yol
            // er ya da geç parayı sessizce kaybettirir.
            _log("[gmp] iptal ÖNCESİ fiş OKUNAMADI — iptal edilmedi (fişe dokunulmadı)", new
            {
                terminal = _terminalId.Length > 0 ? _terminalId : "(bilinmiyor)",
            });
            return new TransportResult(TransportOutcome.Unknown,
                ProviderResultCode: "VOID_READ_FAILED", ErrorCondition: "InProgress",
                Reason: RestomenumReasons.VoidIncomplete, PaymentInvoked: false,
                CancelledSaleSessionId: fisSahibi);
        }

        iptalTutar = oncesi.PaidAmountMinor;
        iptalSayi = oncesi.PaymentCount;
        _log("[gmp] iptal öncesi fiş", new
        {
            toplam = oncesi.TotalAmountMinor, tahsil = oncesi.PaidAmountMinor,
            odemeSayisi = oncesi.PaymentCount, bankaBacagi = oncesi.HasBankLeg,
        });

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
            Rrn: tk.Rrn, Info: RestomenumReasons.TicketCancelled,
            VoidedPaymentCount: iptalSayi, VoidedAmountMinor: iptalTutar,
            CancelledSaleSessionId: fisSahibi);
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
        // ── W83: "DENEME" ile "GERİ ALINAN ÖDEME" AYRI SAYILAR ──────────────────
        // Ölçüm 2026-09-09 (fiş 88/10): üç başarısız kart denemesi olan fişin iptalinde gövde
        // `voidedPaymentCount 3` + `voidedAmountMinor 0` gitti — "üç ödeme geri alındı, sıfır lira".
        // Cihaz ikisini zaten ayrı tutuyor; biz de öyle tutuyoruz.
        var deneme = fis.PaymentCount;                                   // tümü (tutarı 0 olanlar dahil)
        var sayi = fis.Payments?.Count(x => x.AmountMinor > 0) ?? 0;      // yalnız PARASI olanlar
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
            for (var i = deneme - 1; i >= 0; i--)
            {
                var vp = _gmp.VoidPayment(h, i);
                if (!vp.Ok)
                {
                    _log("[gmp] fiş iptali — banka ters işlemi BAŞARISIZ", new { index = i, code = vp.ToString() });
                    return new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                        // W88: geri ALINMADI — yalnız DENENDİ. Eskiden burada `tutar` gidiyordu ve gövde
                        // aynı anda "VOID_INCOMPLETE" ile "941 geri alındı" diyordu; platform onu
                        // 400 `invalidResult` ile reddetti (ölçüm 18:11). İki ayrı soru, iki ayrı alan.
                        VoidedPaymentCount: 0, VoidedAmountMinor: 0,
                        VoidedAttemptCount: deneme,
                        AttemptedVoidAmountMinor: tutar, AttemptedVoidCount: sayi,
                        CancelledSaleSessionId: sahibi, CancelledTicketId: iptalEdilenFis,
                        ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                        ProviderResultCode: $"REVERSAL_FAILED:{vp}");
                }
            }
            vr = _gmp.VoidAll(h, out _);
        }

        if (!vr.Ok)
            return new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                VoidedPaymentCount: 0, VoidedAmountMinor: 0,          // W88: geri alınmadı
                VoidedAttemptCount: deneme,
                AttemptedVoidAmountMinor: tutar, AttemptedVoidCount: sayi,
                CancelledSaleSessionId: sahibi, CancelledTicketId: iptalEdilenFis,
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                ProviderResultCode: $"VOIDALL_FAILED:{vr}");

        var kapat = _gmp.Close(h);
        lock (_gate) _handle = 0;
        _snapshots?.ClearOpenTicketBinding(terminalId);

        if (!kapat.Ok)
        {
            // Fiş iptal edildi ama kapatılamadı: durum BELİRSİZ, "olmadı" değil.
            return new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                VoidedPaymentCount: 0, VoidedAmountMinor: 0,          // W88: geri alınmadı
                VoidedAttemptCount: deneme,
                AttemptedVoidAmountMinor: tutar, AttemptedVoidCount: sayi,
                CancelledSaleSessionId: sahibi, CancelledTicketId: iptalEdilenFis,
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                ProviderResultCode: $"CLOSE_FAILED:{kapat}");
        }

        _log("[gmp] fiş iptal edildi", new
        {
            terminalId, odemeSayisi = sayi, denemeSayisi = deneme, tutar,
            sahibi = sahibi ?? "(bağ yok)",
        });
        return new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: true,
            VoidedPaymentCount: sayi, VoidedAmountMinor: tutar, VoidedAttemptCount: deneme,
            CancelledSaleSessionId: sahibi, CancelledTicketId: iptalEdilenFis);
    }
}
