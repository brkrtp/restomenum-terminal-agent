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
public sealed partial class GmpTerminalTransport : ITerminalTransport
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
        GmpTicket? devamFisi = null;                  // W41: `DevamDogrula`'nin OKUDUGU fis
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
                var engel = DevamDogrula(handle, request, out var okunanFis);
                if (engel is not null) return engel;
                devam = true;
                devamFisi = okunanFis;                // W41: asagida anlik goruntu olarak kullanilacak
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
        //
        // W41 — DEVAM yolunda fiş ZATEN okundu, ikinci kez okumuyoruz.
        //
        // <para><b>Neden geçerli:</b> `DevamDogrula` fişi AYNI kilit kapsamında okuyor
        // (`LocalSaleHandler._islemKilidi`, satış boyunca tutuluyor) ve o okumadan bu satıra kadar
        // cihaza TEK BİR çağrı gitmiyor: aradaki her şey yerel (bağ okuması, günlük), üstteki
        // `TicketHeader`/`OptionFlags`/`ItemSale`/`BindOpenTicket` bloğu ise `devam` yolunda
        // ATLANIYOR. Yani iki okuma arasında fişin değişmesi mümkün değil.</para>
        //
        // <para><b>Ne kazanıyor:</b> sahada ölçülen ~345 ms'lik bir cihaz turu. Kısmi ödeme
        // Türkiye'de normal olduğu için bu, her ek ödemede kasiyerin beklediği süreden düşüyor.</para>
        //
        // <para>⚠️ <b>Varsayım TEK bir koşula bağlı: arada cihaz çağrısı olmaması.</b> Buraya yeni
        // bir cihaz adımı eklenirse varsayım SESSİZCE bozulur — anlık görüntü bayatlar, belirsizlik
        // çözümünün (P34/W25) tek dayanağı o olduğu için bayat sayı yanlış "ödendi/ödenmedi"
        // kararına dönüşür. Yeni adım eklerken bu bloğa bakın.</para>
        var anlik = devamFisi;
        if (anlik is null && _gmp.GetTicket(handle, out var once).Ok) anlik = once;
        if (anlik is GmpTicket ag)
        {
            _snapshots?.SaveSnapshot(request.CommandId,
                ag.TotalAmountMinor, ag.PaidAmountMinor, ag.PaymentCount, request.SaleSessionId);
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
            // Kullanılan bankayı BAŞARISIZLIKTA da bildir: bacak fişte oluşuyor ve hangi bankaya
            // gidildiği paneldeki teşhisin yarısı. Ödeme yankısında son dolu satır bu denemedir;
            // tutarı 0 olsa bile bankası bilinir. Satır okunamazsa alan KONMAZ.
            var basarisizSatir = tk.Payments is { Count: > 0 } bs ? bs[^1] : (GmpPaymentLine?)null;
            if (basarisizSatir?.BankBkmId is int kullanilan)
                cevap = cevap with { UsedBankBkmId = kullanilan };
            _log("[gmp] ödeme başarısız/yanıtsız", new
            {
                request.CommandId,
                gmp = pr.ToString(),
                sinif = cevap.Outcome.ToString(),
                errorCondition = cevap.ErrorCondition,
                banka = basarisizSatir?.BankName ?? "(yok)",
                bkm = basarisizSatir?.BankBkmId,
            });
            return cevap;
        }

        // Ödeme işlendi. Fiş tamamlandıysa basılır ve kapatılır; tamamlanmadıysa AÇIK bırakılır —
        // kasiyer kalanı ekleyecek. Fişi burada kapatmak, yarım ödenmiş fiş üretmek olurdu.
        // Fiş durumu ödemeden SONRA belirlenir; `CLOSED` yalnız `Close` gerçekten başarılıysa.
        var fisDurumu = "OPEN";
        IReadOnlyList<TicketPaymentRow>? kapanisOdemeleri = null;
        long? cihazTahsil = null, cihazToplam = null, cihazKalan = null;

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

        // ── W38: AÇIK FİŞTE CİHAZIN TOPLAM/KALAN RAKAMI ──────────────────────────
        // Kısmi ödemede kasa kalanı KENDİ tabanından hesaplamak zorundaydı; cihazla panel
        // ayrışırsa kimse fark etmiyordu. Cihazın rakamı buradan gidiyor ve panel için
        // otorite oluyor; ayrışma kapanışı beklemeden ilk kısmi ödemede yakalanabiliyor.
        //
        // ⚠️ ANLAMI: "ÖDEMEDEN HEMEN SONRAKİ hâl". Aynı fişe başka bir kasadan ödeme
        // eklenirse bu rakam eskir — "şu anki kalan" DEĞİL.
        //
        // Kaynak `FP3_Payment` yankısı; ölçümlerde bu iki alan (aksine ödeme satırı dizisi)
        // hep dolu geldi. Yine de doğrulanıyor: tutarsızsa İKİSİ DE konmuyor — yanlış sayı
        // göndermektense hiç göndermemek (sözleşme ilkesi).
        if (fisDurumu == "OPEN" && CihazTutarlariGecerli(tk))
        {
            cihazToplam = tk.TotalAmountMinor;
            cihazKalan = tk.TotalAmountMinor - tk.PaidAmountMinor;
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
            DeviceTicketTotalMinor: cihazToplam,
            DeviceRemainingMinor: cihazKalan);
    }
}
