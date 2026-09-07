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
    /// Son satistan ogrenilen terminal kimligi. Iptal istegi terminal kimligi TASIMIYOR (nexo
    /// zarfindaki POIID kasanin adlandirmasi, bizim depo anahtarimiz degil); bagi silmek icin
    /// satista gordugumuz degeri kullaniriz. Bos ise silinecek bag da yoktur.
    /// </summary>
    private string _terminalId = "";

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

        _terminalId = request.TerminalId;

        string? bilgi = null;
        string? fisId = null;                         // fisin kalici kimligi (P27)
        var devam = false;                            // AYNI satisin acik fisine odeme ekleme
        var r = _gmp.Start(out var handle);

        if (r.Code == GmpCodes.AlreadyDone)
        {
            // Cihazda acik fis var. TEK soru: bu fis BU satisa mi ait?
            //
            // Ayrimi `saleSessionId` yapar (platformda `sale.uuid`). `SaleReferenceID` KULLANILMAZ:
            // platform onu `docNo ?? sale.id`'den uretiyor ve `sale.id` masa slug'i - "masa-1"
            // bugun de yarin da ayni. O anahtarla DUNDEN kalmis bayat bir fis "ayni satis" sanilir
            // ve uzerine odeme eklenirdi.
            var bagli = _snapshots?.ReadOpenTicketBinding(request.TerminalId);
            var ayniSatis = request.SaleSessionId is not null && bagli is not null
                && string.Equals(bagli, request.SaleSessionId, StringComparison.Ordinal);

            if (ayniSatis)
            {
                var engel = DevamDogrula(handle, request);
                if (engel is not null) return engel;
                devam = true;
                _handle = handle;
                // Kimlik fis ACILISINDA uretildi; devam yolunda YENIDEN uretilmez, OKUNUR.
                // Uretilseydi ayni fisin odemeleri iki ayri fise bolunur ve kapanis listesi
                // eksik giderdi - yani gerceklesmis bir tahsilat deftere hic yazilmazdi.
                fisId = _snapshots?.ReadOpenTicketId(request.TerminalId);
                _log("[gmp] ayni satisin acik fisine devam ediliyor", new
                {
                    request.CommandId, saleSessionId = request.SaleSessionId,
                });
            }
            else
            {
                var engel = BayatFisiTemizle(handle, request.CommandId);
                if (engel is not null) return engel;  // temizlenemedi -> dokunulmadi, kesin ret
                bilgi = RestomenumReasons.StaleTicketCleared;
                r = _gmp.Start(out handle);           // temizlendi -> fisi SIMDI ac
            }
        }

        if (!devam)
        {
            if (!r.Ok) return Cevir(r, "Start", GmpStep.BeforePayment);
            _handle = handle;
        }

        // ⚠️ BASLIK VE KALEMLER YALNIZ YENI FISTE. Devam yolunda kalemler fiste ZATEN var;
        // yeniden `ItemSale` cagirmak fis toplamini iki katina cikarir (990 -> 1980) ve bu
        // geri alinamaz bir mali kayit olurdu.
        if (!devam)
        {
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

            // ── BAG, ODEMEDEN ONCE YAZILIR ─────────────────────────────────────────────
            // Fis SU ANDA acik ve BU satisa ait. Bagi odeme BASARILI olduktan sonra yazmak
            // yetmiyordu: basarisiz bir odeme (orn. banka hatti yokken kart -> 2086) de fisi
            // acik birakir, ama o fis SAHIPSIZ kalirdi. Sonuc sahada olculdu (2026-09-07):
            // ayni adisyon icin ikinci deneme kendi fisini "baska satisin bayat fisi" sanip
            // TICKET_ALREADY_OPEN ile reddediyor, kasa da manuel odemeye dusuyordu.
            //
            // Odemeden ONCE yazmak guvenli: bag yalnizca "bu acik fis bu satisin" der. Tam
            // odemede Close ile, iptalde VoidAll ile silinir; baska satis geldiginde zaten
            // eslesmez ve bayat-fis mantigi isler.
            if (request.SaleSessionId is string yeniOturum)
                fisId = _snapshots?.BindOpenTicket(request.TerminalId, yeniOturum);
        }

        // ── ANLIK GÖRÜNTÜ: belirsizlik çözümünün tek dayanağı ────────────────────
        if (_gmp.GetTicket(handle, out var once).Ok)
        {
            _snapshots?.SaveSnapshot(request.CommandId,
                once.TotalAmountMinor, once.PaidAmountMinor, once.PaymentCount, request.SaleSessionId);
        }

        // ── ÖDEME: kartta 20–32 sn bloke eder ────────────────────────────────────
        var pr = _gmp.Payment(handle,
            new GmpPaymentRequest(request.AmountMinor, request.PaymentType, request.BankBkmId), out var tk);

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
        // Fiş durumu ödemeden SONRA belirlenir; `CLOSED` yalnız `Close` gerçekten başarılıysa.
        var fisDurumu = "OPEN";
        IReadOnlyList<TicketPaymentRow>? kapanisOdemeleri = null;
        long? cihazTahsil = null, cihazToplam = null;

        // ── ÖDEMEYİ KENDİ DEFTERİMİZE YAZ ────────────────────────────────────────
        // Cihaz bu ödemeyi tutarı ve tipiyle tutar ama BİZİM `paymentId`'mizi bilmez. Fiş kapanınca
        // platforma "bu fişte şu denemeler var" diyebilmemizin tek yolu bu kayıt. Kapanış anında
        // cihazın listesinden sırayla türetmek sessizce kayardı: cihaz BAŞARISIZ denemeleri de
        // kayıt olarak tutuyor (ölçüm 2026-09-07: kart bacağı `payAmount=0` ile fişte duruyor).
        // Kullanılan bankayı cihaz söyler: ödeme yankısında SON dolu satır bu ödemedir. Tutar
        // tutmuyorsa banka İDDİA EDİLMEZ — yanlış banka bildirmektense "bilmiyorum" demek.
        var sonSatir = tk.Payments is { Count: > 0 } satirlar ? satirlar[^1] : (GmpPaymentLine?)null;
        var banka = sonSatir is { } sat && sat.AmountMinor == request.AmountMinor
            ? sat.BankBkmId : null;
        if (fisId is not null)
            _snapshots?.RecordTicketPayment(fisId, request.PaymentId, request.AmountMinor,
                request.PaymentType, banka);

        if (tk.IsFullyPaid)
        {
            // Fişi kapatMADAN ÖNCE oku: `Close` sonrası fiş erişilemez olur.
            //
            // AYRI bir `GetTicket` şart: `FP3_Payment`'ın döndürdüğü fişte cihaz yalnız SON kaydı
            // doldurur (ölçüm 2026-09-07 18:00: 3 kayıtlık dizide 1 dolu, ilk ikisi sıfır).
            if (_gmp.OptionFlags(handle, GmpEchoFlags.Reload).Ok
                && _gmp.GetTicket(handle, out var kapanisFisi).Ok
                && kapanisFisi.PaymentsAreComplete)
            {
                cihazTahsil = kapanisFisi.PaidAmountMinor;
                cihazToplam = kapanisFisi.TotalAmountMinor;
                // Cihazın TAHSİL EDİLMİŞ satır sayısı (tutarı 0 olanlar başarısız denemedir).
                // Kendi defterimizle tutmuyorsa bu gürültü değil ALARM: gerçekleşmiş bir tahsilat
                // bizim kaydımızda olmayabilir ve deftere hiç yazılmaz.
                var cihazSatir = kapanisFisi.Payments?.Count(x => x.AmountMinor > 0) ?? -1;
                var bizim = fisId is null ? null : _snapshots?.ReadTicketPayments(fisId);
                if (bizim is not null && cihazSatir >= 0 && bizim.Count != cihazSatir)
                    _log("[gmp] KAPANIŞ UYUŞMAZLIĞI: cihazdaki tahsilat sayısı defterimizle tutmuyor",
                        new { request.CommandId, fisId, cihazSatir, bizim = bizim.Count });
            }

            var (kapanisHatasi, kapandi) = Kapat(handle);
            if (kapanisHatasi is not null) return kapanisHatasi;
            if (kapandi)
            {
                fisDurumu = "CLOSED";
                // Bağ SİLİNMEDEN önce oku — silindikten sonra fişin ödemelerini soracak kimlik kalmaz.
                if (fisId is not null) kapanisOdemeleri = _snapshots?.ReadTicketPayments(fisId);
                // ⚠️ SIRA ÖNEMLİ. Bağ silinmek ZORUNDA: bırakılsaydı aynı oturumun bir sonraki
                // satışı `BindOpenTicket`'ta eski satırı bulup KAPANMIŞ fişin kimliğini devralırdı.
                // Ama kapanış gövdesi bir üst katmanda kuruluyor; silme ile outbox'a yazma
                // arasında süreç ölürse replay edecek kimlik kalmaz ve o fişin ödemeleri deftere
                // HİÇ yazılmaz. Bu yüzden önce kalıcı "kapandı, bildirilmedi" kaydı, sonra silme.
                if (fisId is not null)
                    _snapshots?.MarkTicketClosed(fisId, request.TerminalId, request.SaleSessionId,
                        cihazToplam, cihazTahsil);
                _snapshots?.ClearOpenTicketBinding(request.TerminalId);
            }
        }
        else if (request.SaleSessionId is string oturum)
        {
            // Fis ACIK kaldi (kismi odeme - Turkiye'de NORMAL aradurum). Bag zaten odemeden once
            // yazildi; burada tazeleniyor (devam yolunda bag mevcut fisten gelir ve dokunulmaz).
            _snapshots?.BindOpenTicket(request.TerminalId, oturum);
        }

        return new TransportResult(
            TransportOutcome.Approved,
            ApprovedAmountMinor: request.AmountMinor,
            Rrn: tk.Rrn,
            CardLast4: tk.CardLast4,
            ProviderResultCode: pr.ToString(),
            Info: bilgi,
            UsedBankBkmId: banka,
            TicketState: fisDurumu,
            TicketId: fisId,
            ClosedTicketPayments: kapanisOdemeleri,
            DevicePaidMinor: cihazTahsil,
            DeviceTicketTotalMinor: cihazToplam);
    }

    /// <summary>
    /// Acik fise devam etmeden onceki guvenlik kontrolleri. <c>null</c> = devam edilebilir.
    ///
    /// <para><b>Neden sart:</b> bag "bu fis bu satisa ait" der ama fisin ICERIGININ hala o satisla
    /// ayni oldugunu SOYLEMEZ. Kasiyer kismi odemeden sonra kalem eklemis/silmisse, devam etmek
    /// fise yanlis tutarda odeme yazmaktir - mali kayit bozulur ve geri alinamaz.</para>
    /// </summary>
    private TransportResult? DevamDogrula(ulong handle, SaleRequest request)
    {
        var of = _gmp.OptionFlags(handle, GmpEchoFlags.Reload);
        var gt = _gmp.GetTicket(handle, out var fis);
        if (!of.Ok || !gt.Ok)
            return Hata(TransportOutcome.Declined, $"TICKET_READ_FAILED:{of}/{gt}",
                "PaymentRestriction", RestomenumReasons.TicketAlreadyOpen);

        // Toplam dogrulamasi. Karsilastirma degeri platformun `SaleTotalAmountMinor`'i - kalem
        // toplamini burada YENIDEN hesaplamiyoruz: iki ayri hesap iki ayri hata kaynagi olurdu.
        if (request.SaleTotalMinor is not long satisToplam)
            return Hata(TransportOutcome.Declined, "SALE_TOTAL_UNKNOWN",
                "PaymentRestriction", RestomenumReasons.TicketSaleMismatch);

        if (fis.TotalAmountMinor != satisToplam)
        {
            _log("[gmp] fis toplami satis toplamiyla UYUSMUYOR - devam edilmiyor", new
            {
                request.CommandId, fisToplam = fis.TotalAmountMinor, satisToplam,
            });
            return HataCihazTutarli(
                $"TICKET_SALE_MISMATCH:fis={fis.TotalAmountMinor},satis={satisToplam}",
                RestomenumReasons.TicketSaleMismatch, fis);
        }

        var kalan = fis.RemainingMinor;
        if (request.AmountMinor > kalan)
        {
            _log("[gmp] istenen tutar fisin kalanini asiyor - terminale gidilmiyor", new
            {
                request.CommandId, istenen = request.AmountMinor, kalan,
            });
            return HataCihazTutarli(
                $"AMOUNT_EXCEEDS_REMAINING:istenen={request.AmountMinor},kalan={kalan}",
                RestomenumReasons.AmountExceedsRemaining, fis);
        }

        return null;
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
            errorCode = fis.LastPaymentErrorCode ?? "(bos)",
            appErrorCode = fis.LastPaymentAppErrorCode ?? "(bos)",
            errorText = fis.LastPaymentErrorText ?? "(bos)",
        });

        // `PaymentCount < 0` = BOZUK okuma (sarmalayıcının sınır koruması). Bunu "ödeme yok" saymak
        // en kötü hata olurdu: okunamayan bir fişi silmek.
        //
        // Sayaç 0 değilse DOKUNULMAZ. Cihazın hata kaydına dayanan bir istisna denendi ve ölçümle
        // çürütüldü (yukarıdaki `Probe` notuna bakın: kanal "NO RESPONSE" diyor, "iletilmedi" değil).
        // Ödemesi görünen bir fişi silmenin tek meşru yolu insan kararıdır (`--void --onayla`).
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
    /// <returns>
    /// <c>Hata</c>: kasaya dönecek erken sonuç (bugün hep <c>null</c> — ödeme alınmıştır, baskı
    /// hatası "reddedildi" diye raporlanamaz). <c>Kapandi</c>: fiş cihazda GERÇEKTEN kapandı mı.
    ///
    /// <para><b><c>Kapandi</c> neden ayrı bir cevap:</b> baskı adımı yarıda kalırsa <c>FP3_Close</c>
    /// hiç çağrılmaz ve fiş cihazda AÇIK kalır. Bunu "kapandı" saymak iki ayrı hasar üretirdi:
    /// (1) platform o fişin ödemelerini deftere yazar — oysa fiş hâlâ iptal edilebilir durumda;
    /// (2) bağ silindiği için aynı satışın bir sonraki ödemesi kendi fişini "başkasının bayat
    /// fişi" sanıp <c>TICKET_ALREADY_OPEN</c> ile reddedilir ve kasa manuel ödemeye düşer.</para>
    /// </returns>
    private (TransportResult? Hata, bool Kapandi) Kapat(ulong handle)
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
            // Ama fiş de KAPANMADI: `Close`'a hiç gelinmedi.
            if (!r.Ok)
            {
                _log("[gmp] baskı adımı başarısız (ödeme ALINDI, fiş AÇIK kaldı)",
                    new { ad, code = r.ToString() });
                return (null, false);
            }
        }

        for (var i = 0; i < 3; i++)
        {
            if (_gmp.PrintMF(handle).Ok) break;
            _log("[gmp] PrintMF tekrar", new { deneme = i + 1 });
        }
        var kapanis = _gmp.Close(handle);
        if (!kapanis.Ok)
        {
            // Ödeme alındı ama fiş kapanmadı: açık fiş olarak bildirilir, bağ KORUNUR.
            _log("[gmp] Close başarısız (ödeme ALINDI, fiş AÇIK kaldı)", new { code = kapanis.ToString() });
            return (null, false);
        }
        lock (_gate) _handle = 0;
        return (null, true);
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
                // TEŞHİS: çelişki çözülemedi. Cihazın hata alanlarını OLDUĞU GİBİ yaz — hangisinin
                // boş kaldığı ancak böyle görülür. Boş görünüyorsa kanal hiç dolmuyor demektir.
                _log("[gmp] çelişki çözülemedi — cihaz hata alanları", new
                {
                    request.CommandId, delta,
                    errorCode = simdi.LastPaymentErrorCode ?? "(bos)",
                    appErrorCode = simdi.LastPaymentAppErrorCode ?? "(bos)",
                    errorText = simdi.LastPaymentErrorText ?? "(bos)",
                });

                return new PaymentProbe(ProbeVerdict.Indeterminate,
                    RemainingMinor: simdi.RemainingMinor,
                    Note: $"ödeme sayacı arttı ama tutar artmadı (delta {delta}) — çelişkili okuma");
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
                        VoidedPaymentCount: sayi, VoidedAmountMinor: tutar, CancelledSaleSessionId: sahibi,
                        ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                        ProviderResultCode: $"REVERSAL_FAILED:{vp}");
                }
            }
            vr = _gmp.VoidAll(h, out _);
        }

        if (!vr.Ok)
            return new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                VoidedPaymentCount: sayi, VoidedAmountMinor: tutar, CancelledSaleSessionId: sahibi,
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                ProviderResultCode: $"VOIDALL_FAILED:{vr}");

        var kapat = _gmp.Close(h);
        lock (_gate) _handle = 0;
        _snapshots?.ClearOpenTicketBinding(terminalId);

        if (!kapat.Ok)
        {
            // Fiş iptal edildi ama kapatılamadı: durum BELİRSİZ, "olmadı" değil.
            return new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
                VoidedPaymentCount: sayi, VoidedAmountMinor: tutar, CancelledSaleSessionId: sahibi,
                ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
                ProviderResultCode: $"CLOSE_FAILED:{kapat}");
        }

        _log("[gmp] fiş iptal edildi", new { terminalId, odemeSayisi = sayi, tutar, sahibi = sahibi ?? "(bağ yok)" });
        return new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: true,
            VoidedPaymentCount: sayi, VoidedAmountMinor: tutar, CancelledSaleSessionId: sahibi);
    }

    // ── yardımcılar ─────────────────────────────────────────────────────────

    private static TicketState Cevir(GmpTicket t, bool acik) => new(
        HasOpenTicket: acik, TotalAmountMinor: t.TotalAmountMinor, PaidAmountMinor: t.PaidAmountMinor,
        Rrn: t.Rrn, CardLast4: t.CardLast4, PaymentCount: t.PaymentCount,
        LastPaymentErrorCode: t.LastPaymentErrorCode, LastPaymentErrorText: t.LastPaymentErrorText,
        LastPaymentAppErrorCode: t.LastPaymentAppErrorCode,
        LastPaymentAppErrorText: t.LastPaymentAppErrorText,
        Payments: t.Payments, PaymentsAreComplete: t.PaymentsAreComplete);

    /// <summary>
    /// Terminale <b>hiç gidilmeden</b> üretilen ret. <c>FP3_Payment</c> çağrılmadığı için para
    /// hareket edemez — ama <c>Refusal</c> DEĞİL: banka hiç devrede olmadığından "kart reddedildi"
    /// mesajı yanlış olurdu (W1 kuralı: kesin-ret yalnız host'un açık reddiyle).
    /// </summary>
    /// <summary>
    /// Cihazın gördüğü tutarları da taşıyan ret. YALNIZ kalan/toplam ayrışmasından doğan iki
    /// sebepte kullanılır — kasiyer panelin değil CİHAZIN sayısını görmeli, çünkü ödemeyi
    /// kabul edecek olan cihaz.
    /// </summary>
    private static TransportResult HataCihazTutarli(string kod, string reason, GmpTicket fis) =>
        new(TransportOutcome.Declined, ProviderResultCode: kod, ErrorCondition: "PaymentRestriction",
            PaymentInvoked: false, Reason: reason,
            DeviceTicketTotalMinor: fis.TotalAmountMinor,
            DeviceRemainingMinor: fis.RemainingMinor);

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
