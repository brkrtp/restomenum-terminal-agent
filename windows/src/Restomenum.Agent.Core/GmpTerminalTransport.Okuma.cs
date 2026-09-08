namespace Restomenum.Agent.Core;

/// <summary>
/// <b>FİŞ OKUMA VE BELİRSİZLİK YOKLAMASI</b> — <see cref="GmpTerminalTransport"/>'un parçası (W43 bölmesi).
///
/// Fişin o anki hâlini okuma (tanıtıcı elde yoksa yenileyerek) ve ödeme yanıtsız bittiğinde
/// "cihazda ne oldu" sorusunu ödeme SAYACIYLA çözen yoklama. Tutar karşılaştırmasına
/// güvenilmez: 20 ₺ + 20 ₺ ödenmiş bir fişte tutar farkı iki ödemeyi ayırt edemez.
///
/// <para><b>Neden `partial`, ayrı sınıf değil:</b> bu metotlar `_handle` (cihaz tanıtıcısı)
/// ve onu koruyan `_gate` kilidi üzerinde ORTAK ÇALIŞIYOR. Ayrı sınıflara bölmek o değişken
/// durumu sınıflar arasında paylaştırmak demekti — bölmenin amacı okunabilirlikti, yeni bir
/// paylaşım yüzeyi açmak değil. `partial` ile derlenen tür AYNI kalıyor: davranış değişmez.</para>
/// </summary>
public sealed partial class GmpTerminalTransport
{
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
            // ÇIKARIM, okuma DEĞİL: fiş ödeme tamamlandığı için de kapanmış olabilir. `CounterRead`
            // bilerek `false` — bu sonuç kasaya "kesin olmadı" diye bildirilmemeli.
            if (once is null || once.Value.PaymentCount == 0)
                return new PaymentProbe(ProbeVerdict.NotLanded, Note: "açık fiş yok, önceki ödeme de yok");
            return new PaymentProbe(ProbeVerdict.Indeterminate, Note: "fiş kapanmış — akıbet okunamıyor");
        }

        if (once is not null && simdi.PaymentCount > once.Value.PaymentCount)
        {
            // ── SAHİPLİK KAPISI ────────────────────────────────────────────────────────
            // "Sayaç arttı" gözlemi TEK BAŞINA "benim ödemem geçti" demek DEĞİL. Cihazda o an
            // BAŞKA bir satışın fişi duruyor olabilir ve onun ödemesi sayacı artırır.
            //
            // SAHADA OLDU (2026-09-07 16:36): sabah 2086 alıp hiç para almamış bir kart komutu,
            // kurtarma turunda kullanıcının YENİ nakit satışının fişini gördü, "benim ödemem
            // geçmiş" dedi ve kendini Approved ilan etti. Defterde masa-5'e 4,90 TL'lik HAYALET
            // kart tahsilatı yazıldı; gerçek para masa-7'de nakitti. İki fişin tutarı da 990
            // olduğu için tutar karşılaştırması da yakalamadı.
            //
            // Bu yüzden `Landed` ancak fişin BU komuta ait olduğu KANITLIYSA verilir. Kanıt:
            // cihazdaki açık fişin bağı (`open_ticket.saleSessionId`) ile bu komutun anlık
            // görüntüsünde saklanan oturumun EŞLEŞMESİ. Biri yoksa kanıt yoktur → belirsiz.
            var fisSahibi = _snapshots?.ReadOpenTicketBinding(request.TerminalId);
            var komutSahibi = once.Value.SaleSessionId;
            if (komutSahibi is null || fisSahibi is null
                || !string.Equals(komutSahibi, fisSahibi, StringComparison.Ordinal))
            {
                _log("[gmp] sayaç arttı ama fişin bu komuta aitliği KANITLANAMADI", new
                {
                    request.CommandId, komutSahibi = komutSahibi ?? "(yok)", fisSahibi = fisSahibi ?? "(yok)",
                });
                return new PaymentProbe(ProbeVerdict.Indeterminate,
                    RemainingMinor: simdi.RemainingMinor,
                    Note: "fişin sahipliği kanıtlanamadı — başka satışın ödemesi olabilir");
            }

            // ÇELİŞKİ SAVUNMASI: sayaç arttı ama ödenen tutar artmadı (delta ≤ 0). "Landed" = para
            // HAREKET ETTİ demek; 0/negatif tutarla Landed dönmek sahte-onay üretir (Success +
            // AuthorizedAmount 0). Canlı ölçüldü: başarısız kart bacağında sayaç artıp tutar
            // artmayabiliyor. Böyle bir okuma güvenilmez — belirsiz de, insana çıksın.
            var delta = simdi.PaidAmountMinor - once.Value.PaidMinor;
            if (delta <= 0)
            {
                // ⚠️ BURADA BİR KURAL DENENDİ VE ÖLÇÜMLE ÇÜRÜTÜLDÜ (2026-09-07).
                // Varsayım şuydu: cihaz ödeme kaydına "istek iletilmedi" yazıyorsa çelişki çözülür.
                // DLL log dökümünde `ODEME_ERROR_CODE "2085"` ve `"ÖDEME İSTEĞİ İLETİLMEDİ"`
                // görülmüştü. Ama `ST_PaymentErrMessage` canlı okunduğunda gelen şey bu DEĞİL:
                //     ErrorCode=(boş)  ErrorMsg="NO RESPONSE"  AppErrorCode="0000"  AppErrorMsg="(00000000)-DEFAULT"
                // "NO RESPONSE" = "cevap gelmedi" — yani tam da paranın HAREKET ETMİŞ OLABİLECEĞİ
                // durum, "iletilmedi"nin tersi. Bu kanalı kesin sonuç üretmek için kullanmak,
                // belirsizi kesin saymanın bir başka kılığı olurdu. Kural KALDIRILDI; alanlar
                // yalnız TEŞHİS için taşınıyor.
                //
                // ── W25: ÇELİŞKİ ARTIK ÇÖZÜLEBİLİYOR ─────────────────────────────────
                // Yukarıdaki not, TEK bir kanala (fiş düzeyindeki `LastPaymentError*`) dayanan bir
                // kuralın çürütülmesiydi ve o karar hâlâ geçerli: o kanal kullanılmıyor.
                //
                // Değişen şey KANIT: 2026-09-07'de ölçüldü ki başarısız kart denemesi fişte KENDİ
                // SATIRINI açıyor — tip 4, banka adı + BKM dolu, `payAmount 0`, hata alanları dolu.
                // Yani "sayaç arttı, tutar artmadı" bir okuma arızası değil, BAŞARISIZ DENEMENİN
                // İMZASI. Artık satırın kendisini okuyabildiğimiz için tutarı sıfır olan yeni
                // satırlar "fişe para yazılmadı"nın kanıtıdır.
                //
                // ⚠️ Bu "bankadan geçmedi" demek DEĞİL. Ayrımı satırın hata kodu kurar:
                //   • kullanılabilir uygulama kodu VAR (ör. 2202)  → banka açıkça reddetti → Refusal
                //   • kod yok / "0000" ("NO RESPONSE")            → cevap gelmedi → UnreachableHost
                // İkincisinde fişte para yok ama bankada yetim provizyon KALABİLİR; bu yüzden
                // "güvenle tekrar dene" DENMEZ.
                var yeniSatirlar = YeniOdemeSatirlari(simdi, once.Value.PaymentCount);
                if (yeniSatirlar is null)
                {
                    // Satırlar okunamadı → hiçbir şey iddia etme. Eski davranış aynen.
                    _log("[gmp] çelişki çözülemedi — ödeme satırları okunamadı", new
                    {
                        request.CommandId, delta,
                        errorCode = simdi.LastPaymentErrorCode ?? "(bos)",
                        appErrorCode = simdi.LastPaymentAppErrorCode ?? "(bos)",
                        errorText = simdi.LastPaymentErrorText ?? "(bos)",
                    });
                    return new PaymentProbe(ProbeVerdict.Indeterminate,
                        RemainingMinor: simdi.RemainingMinor,
                        Note: $"ödeme sayacı arttı ama tutar artmadı (delta {delta}) — satırlar okunamadı");
                }

                if (yeniSatirlar.Any(x => x.AmountMinor > 0))
                {
                    // Tutarı olan yeni bir satır var ama fişin tahsil toplamı artmamış: iki okuma
                    // birbirini tutmuyor. Böyle bir çelişkiyi çözmüş gibi yapmak, kanıtsız kesinlik
                    // üretmek olurdu.
                    _log("[gmp] çelişki: tutarlı satır var ama fiş toplamı artmamış", new
                    {
                        request.CommandId, delta, satir = yeniSatirlar.Count,
                    });
                    return new PaymentProbe(ProbeVerdict.Indeterminate,
                        RemainingMinor: simdi.RemainingMinor,
                        Note: "yeni ödeme satırında tutar var ama fiş toplamı artmadı — çelişkili okuma");
                }

                var basarisiz = yeniSatirlar[^1];
                var acikRet = KullanilabilirKod(basarisiz.AppErrorCode);
                var kosul = acikRet ? "Refusal" : "UnreachableHost";
                _log("[gmp] fişe para YAZILMADI — başarısız deneme satırı okundu", new
                {
                    request.CommandId, delta, satir = yeniSatirlar.Count,
                    tip = basarisiz.Type, banka = basarisiz.BankName ?? "(yok)",
                    bkm = basarisiz.BankBkmId, uygulamaKodu = basarisiz.AppErrorCode ?? "(bos)",
                    hataMetni = basarisiz.ErrorMessage ?? "(bos)", kosul,
                });
                // `reason` CİHAZIN SEBEBİ, `info` DURUM. `reason:NOT_LANDED` yazmak yasak:
                // platform onu "kart hiç çekilmedi, tekrar dene" diye okuyor ve burada
                // `FP3_Payment` ÇAĞRILMIŞTI — cevapsızlık hâlinde çift tahsilat üretirdi.
                return new PaymentProbe(ProbeVerdict.NotLanded,
                    RemainingMinor: simdi.RemainingMinor, CounterRead: true,
                    ErrorCondition: kosul,
                    Reason: acikRet ? RestomenumReasons.BankDeclined : RestomenumReasons.NoResponse,
                    Info: RestomenumReasons.NotLandedOnTicket,
                    ProviderResultCode: $"NOT_LANDED:{basarisiz.AppErrorCode ?? "-"}:{basarisiz.ErrorMessage ?? "-"}",
                    Note: acikRet
                        ? $"banka açıkça reddetti ({basarisiz.AppErrorCode}) — fişe para yazılmadı, tekrar güvenli"
                        : "bankadan cevap gelmedi — fişe para yazılmadı, ama yetim provizyon olabilir: tekrar GÜVENLİ DEĞİL");
            }
            return new PaymentProbe(ProbeVerdict.Landed,
                ApprovedAmountMinor: delta,
                RemainingMinor: simdi.RemainingMinor, Rrn: simdi.Rrn, CardLast4: simdi.CardLast4);
        }

        if (once is not null && simdi.PaymentCount == once.Value.PaymentCount)
            // KANIT: fiş okundu, sayaç kıpırdamadı → bizim ödememiz cihazda oluşmadı.
            return new PaymentProbe(ProbeVerdict.NotLanded,
                RemainingMinor: simdi.RemainingMinor, CounterRead: true);

        // Anlık görüntü yok (agent yeniden başlamış). Sayaç varsa ödeme İŞLENMİŞ olabilir ama
        // BİZİM ödememiz olduğunu söyleyemeyiz — bu yüzden belirsiz.
        if (simdi.PaymentCount > 0)
            return new PaymentProbe(ProbeVerdict.Indeterminate,
                RemainingMinor: simdi.RemainingMinor, Note: "anlık görüntü yok, ödeme sahibi belirsiz");

        // KANIT: fiş AÇIK ve üzerinde hiç ödeme yok (sayaç 0) → bizimki de yok.
        return new PaymentProbe(ProbeVerdict.NotLanded,
            RemainingMinor: simdi.RemainingMinor, CounterRead: true);
    }

    public Task<bool> EchoAsync(CancellationToken ct = default) => Task.Run(() => _gmp.Echo().Ok, ct);
}
