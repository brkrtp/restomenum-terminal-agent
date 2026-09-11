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

    /// <summary>
    /// <b>W109</b> — monotonik ms sayacı. Bütçe bir SÜRE ölçümüdür, saat değil: duvar saati NTP
    /// ile geri atlarsa bütçe anında tükenmiş görünürdü.
    /// </summary>
    private readonly Func<long> _tik;

    /// <summary>Satış çağrısının toplam süre bütçesi (bkz. <see cref="BaskiButcesi"/>).</summary>
    private readonly TimeSpan _butce;

    public GmpTerminalTransport(
        IGmpWrapper gmp, ITicketSnapshotStore? snapshots = null, Action<string, object?>? log = null,
        Func<long>? tik = null, TimeSpan? butce = null)
    {
        _gmp = gmp;
        _snapshots = snapshots;
        _log = log ?? ((_, _) => { });
        _tik = tik ?? (() => Environment.TickCount64);
        _butce = butce ?? BaskiButcesi.Varsayilan;
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

        // ── W109: BÜTÇE SATIŞIN EN BAŞINDA BAŞLAR ────────────────────────────────
        // Kasanın 180 sn'lik POST kapısı TÜM çağrıyı kapsıyor — yalnız baskıyı değil. Bütçeyi
        // baskı zincirinde başlatmak, ödemenin yediği 90 sn'yi görmezden gelmek olurdu ve
        // toplam yine kapıyı aşardı.
        var butce = new BaskiButcesi(_butce, _tik);

        string? bilgi = null;
        string? fisId = null;                         // fisin kalici kimligi (P27)
        var devam = false;                            // AYNI satisin acik fisine odeme ekleme
        GmpTicket? devamFisi = null;                  // W41: `DevamDogrula`'nin OKUDUGU fis
        var r = _gmp.Start(out var handle);

        // W120: cihaz daha fişi AÇARKEN "kâğıt bitti" diyorsa uyarı ORADA doğar — baskıya hiç
        // gelinmiyor, dolayısıyla aşağıdaki dal çalışmıyor (sahada ölçüldü, 03:02:57).
        if (r.Code == GmpCodes.NoPaper)
            _snapshots?.SetPaperWarning(request.TerminalId, true, "Start");

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
                // ── W70: İNDİRİM CİHAZA GİDİYOR ─────────────────────────────────────
                // `FiscalLine.LineDiscountMinor` BİRİM başınadır (kuruş dağıtımı satırı ikiye/üçe
                // bölebiliyor; her alt satırın kendi birim indirimi var). Cihazın
                // `ST_ITEM.promotion.amount` alanı ise SATIR toplamıdır — yoklamada ölçüldü
                // (2026-09-09 14:57, adım 6): `count 2, amount 990, promotion 100` gönderildi ve
                // fiş toplamı 2×990−**100** arttı, indirim toplamı **100** oldu. Birim bazlı
                // olsaydı 200 olurdu.
                //
                // Bu yüzden `× Quantity`. Çarpmayı unutmak, indirimi adet katı kadar EKSİK
                // uygulamak demek — müşteriden fazla tahsilat ve geri alınamaz mali kayıt.
                r = _gmp.ItemSale(handle, new GmpItem(l.Name, l.UnitPriceMinor, l.Quantity, l.DepartmentNo,
                    DiscountMinor: l.LineDiscountMinor * l.Quantity, DiscountText: GmpItemDefaults.DiscountText), out _);
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
        uint? mailFisNo = null;   // W144: bu satışın aldığı mali fiş numarası
        // W104: baskı/kapatma zinciri yarıda kalırsa NEREDE kaldığı kasaya taşınır. `null` = sorun yok.
        string? baskiSebebi = null, baskiAdimi = null, baskiKodu = null;
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
        // ── W79: BANKA ATFI TUTARDAN AYRILDI ─────────────────────────────────────
        // Eskiden banka yalnız `sat.AmountMinor == request.AmountMinor` ise yazılıyordu. Banka
        // indirimi tutarı düşürdüğü için (990 istendi, 941 tahsil) koşul tutmuyordu ve `bkm 62`
        // cihazdan AÇIKÇA geldiği hâlde deftere NULL yazıldı. Kod tutarsızlığı GÖRÜYORDU ama onu
        // yalnız "bankayı söyleme" kararında kullandı; oysa tutar uyuşmazlığı BİLDİRİLMELİ,
        // banka YİNE DE yazılmalı. Satırın bu denemeye ait olduğunun ölçütü artık tek: cihazın
        // ödeme satırı sayısı bu denemede arttıysa son satır bizimkidir.
        // Bu dala YALNIZ `pr.Ok` ile gelinir: ödeme işlendi, yani son dolu satır BU denemenindir.
        // Başarısızlık dalı da (yukarıda) aynı kuralı kullanıyor ve sahada doğru çalışıyor.
        var banka = sonSatir?.BankBkmId;

        // ── W79: TAHSİL EDİLEN TUTAR — İKİ KAYNAK, ÇELİŞİRSE İDDİA YOK ───────────
        // Birincil kaynak bu denemenin satırı; çapraz doğrulama fişin tahsil toplamındaki artış.
        // İkisi tutmuyorsa hiçbir tutar iddia edilmez: yanlış tutar bildirmektense "bilmiyorum".
        // Tutar için ÖLÇÜT DAHA SIKI: satırın bu denemeye ait olduğunu ödeme sayacının ARTMASIYLA
        // doğrularız, sonra tutarı fişin tahsil artışıyla ÇAPRAZ kontrol ederiz. İkisi tutmuyorsa
        // hiçbir tutar iddia edilmez — yanlış tutar bildirmektense "bilmiyorum".
        long? tahsil = null;
        if (sonSatir is { } tahsilSatiri && tahsilSatiri.AmountMinor > 0)
        {
            if (anlik is not GmpTicket oncekiFis)
                tahsil = tahsilSatiri.AmountMinor;      // karşılaştıracak görüntü yok
            else if (tk.PaymentCount > oncekiFis.PaymentCount)
            {
                var delta = tk.PaidAmountMinor - oncekiFis.PaidAmountMinor;
                if (delta == tahsilSatiri.AmountMinor) tahsil = tahsilSatiri.AmountMinor;
                else
                    _log("[gmp] TAHSİL TUTARI ÇELİŞKİLİ — hiçbir tutar iddia edilmiyor", new
                    {
                        request.CommandId, satir = tahsilSatiri.AmountMinor, delta,
                        istenen = request.AmountMinor,
                    });
            }
            // Sayaç artmadıysa son satır ESKİ bir ödeme olabilir → iddia YOK (sessiz, alarm değil).
        }
        // Fark yalnız POZİTİFSE indirimdir. Negatif fark (tahsil > istenen) ayrı ve daha ciddi bir
        // olaydır — alan konmaz, uyarı yazılır.
        long? isyeriIndirimi = null;
        if (tahsil is long t2 && t2 != request.AmountMinor)
        {
            var fark = request.AmountMinor - t2;
            if (fark > 0) isyeriIndirimi = fark;
            _log(fark > 0
                    ? "[gmp] TAHSİL ≠ İSTENEN — üye işyeri indirimi"
                    : "[gmp] TAHSİL İSTENENDEN FAZLA — beklenmeyen durum",
                new { request.CommandId, istenen = request.AmountMinor, tahsil = t2, fark,
                      banka = sonSatir?.BankName ?? "(yok)", bkm = banka });
        }
        if (fisId is not null)
            _snapshots?.RecordTicketPayment(fisId, request.PaymentId, request.AmountMinor,
                request.PaymentType, banka, collectedMinor: tahsil);

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

                // ── W73: CİHAZIN FİŞE BASTIĞI SATIRLAR GÜNLÜĞE ───────────────────────
                // 2026-09-09: "indirim fişte çıktı mı" sorusunu saatlerce cevaplayamadık; elimizde
                // yalnız toplamlar vardı, kâğıt kullanıcıdaydı. Oysa cihaz bastığı satırları ZATEN
                // gönderiyordu (`ECHO_PRINTER`) ve biz atıyorduk. Artık yazılıyor — bir daha
                // "basıldı mı" diye kâğıt beklemeyeceğiz.
                //
                // `eksik` ayrımı önemli: cihaz "3 satır bastım" deyip 2 satır göndermiş olabilir.
                // "Üç satır bastı" ile "üç satırını gördük" ayrı şeyler (ödeme satırlarında bir kez
                // ısırdı) — eksik kopyayı tam sanmak yanlış teşhis üretir.
                if (kapanisFisi.PrinterLines is { } yazici)
                    _log("[gmp] fişe basılan satırlar", new
                    {
                        request.CommandId,
                        adet = yazici.Count,
                        cihazinBildirdigi = kapanisFisi.PrinterLineCount,
                        eksik = yazici.Count < kapanisFisi.PrinterLineCount,
                        // Liste DEĞİL metin: host `{Arg}` ile ToString'liyor ve koleksiyon
                        // orada tip adına dönüşüyordu (16:07'de ölçüldü) — satırlar görünmüyordu.
                        satirlar = PrinterCopy.Yazdirilabilir(yazici),
                    });
                // Cihazın TAHSİL EDİLMİŞ satır sayısı (tutarı 0 olanlar başarısız denemedir).
                // Kendi defterimizle tutmuyorsa bu gürültü değil ALARM: gerçekleşmiş bir tahsilat
                // bizim kaydımızda olmayabilir ve deftere hiç yazılmaz.
                var cihazSatir = kapanisFisi.Payments?.Count(x => x.AmountMinor > 0) ?? -1;
                var bizim = fisId is null ? null : _snapshots?.ReadTicketPayments(fisId);
                if (bizim is not null && cihazSatir >= 0 && bizim.Count != cihazSatir)
                    _log("[gmp] KAPANIŞ UYUŞMAZLIĞI: cihazdaki tahsilat sayısı defterimizle tutmuyor",
                        new { request.CommandId, fisId, cihazSatir, bizim = bizim.Count });
            }

            // ── W111: İŞARET BASKIDAN **ÖNCE** YAZILIR ───────────────────────────
            // Buraya YALNIZ fiş tam ödenmişken gelinir, yani fişte GERÇEK PARA var ve şimdi
            // baskı zinciri başlayacak. Kayıt eskiden zincir DÖNDÜKTEN sonra yazılıyordu ve
            // sahada bunun bedeli ölçüldü (2026-09-10 02:05): süreç zincirin ORTASINDA öldü
            // (yayım için durduruldu, cihaz kâğıt bekliyordu) ve satır HİÇ yazılmadı — fiş
            // 990 kuruş tahsilatla açık kaldı ama süpürme onu GÖRMEDİ.
            //
            // Bu, `closed_tickets`'ın kapattığı boşluğun birebir kardeşi: orada da "bağ silindi
            // ama gövde yazılamadan süreç öldü" aralığı kalıcı bir kayıtla kapatılıyor. Aynı
            // ilke: TEHLİKELİ ADIMDAN ÖNCE kalıcı iz bırak.
            //
            // Maliyeti her kapanışta bir yerel SQLite yazması; kazancı, süreç ölümünün fişi
            // görünmez bırakmaması.
            if (fisId is not null)
                _snapshots?.MarkTicketNeedsFinish(fisId, request.TerminalId, request.SaleSessionId,
                    null, null);

            var (kapanisHatasi, kapandi, baskiAdim, baskiKod) = Kapat(handle, butce);
            if (kapanisHatasi is not null) return kapanisHatasi;
            baskiAdimi = baskiAdim;
            baskiKodu = baskiKod?.ToString();

            if (fisId is not null)
            {
                if (kapandi)
                    // Zincir tamamlandı: işaret düşer. (Bu satır ile `Close` arasında süreç
                    // ölürse işaret KALIR ve süpürme cihazda açık fiş bulamayıp kaydı düşürür —
                    // "kapandı" diye deftere YAZMADAN. Doğru taraf bu.)
                    _snapshots?.ClearPendingFinish(fisId);
                else
                    // Zincir yarıda kaldı: hata alanlarını tazele. Deneme sayacı SIFIRLANMAZ.
                    _snapshots?.MarkTicketNeedsFinish(fisId, request.TerminalId, request.SaleSessionId,
                        baskiAdim, baskiKodu);
            }
            if (baskiAdim is not null && baskiKod is { } bk)
                // ⚠️ Zaman aşımı ile RET ayrı sebeplerdir: "cevap gelmedi" ile "cevap geldi ve
                // hayır dedi" farklı olgular. `Close` ayrı: orada kâğıt/yazıcı sorusu yoktur.
                // ── W119: KASAYA NE GÖNDERDİĞİMİZİ GÜNLÜĞE YAZ ──────────────────
                // Kör noktaydı: `[gmp] baskı adımı başarısız` yalnız ADIMI ve KODU yazıyordu,
                // o koddan TÜRETTİĞİMİZ sebebi değil. Kullanıcı "kâğıt bittiği hâlde UI'a
                // gitmedi" dediğinde ajan tarafından DOĞRULAYAMADIM — gövde de loglanmıyor
                // (bilerek: kart/kişisel veri taşıyabilir). Sebep serbest metin değil, kapalı
                // bir sözlükten geliyor; onu yazmak gizlilik riski taşımaz ve "gönderdik mi"
                // sorusunu ölçülebilir yapar.
                baskiSebebi = bk.Code switch
                {
                    // Cihaz KÂĞIT BİTTİ dedi — çıkarım değil, beyan (ölçüm 2026-09-10 02:21).
                    GmpCodes.NoPaper => RestomenumReasons.OutOfPaper,
                    // Bütçe doldu, adım cihaza HİÇ gönderilmedi.
                    GmpCodes.BudgetExhausted => RestomenumReasons.PrintBudgetExhausted,
                    _ => baskiAdim == "Close"
                        ? RestomenumReasons.TicketCloseFailed
                        // "cevap gelmedi" ile "cevap geldi ve hayır dedi" AYRI olgular.
                        : GmpCodes.IsTimeout(bk.Code) ? RestomenumReasons.PrintTimeout
                                                      : RestomenumReasons.PrintFailed,
                };
            if (baskiSebebi is not null)
                _log("[gmp] kasaya bildirilen baskı sebebi", new
                {
                    request.CommandId, reason = baskiSebebi,
                    printFailedStep = baskiAdimi, printFailedCode = baskiKodu,
                });

            // ── W120: KÂĞIT UYARISI FİŞTEN BAĞIMSIZ YAŞAR ────────────────────────
            // Bu satış çözülse, fiş kapansa, kasa kaydı tazelense bile uyarı DURUR.
            // Yalnız BAŞARILI bir baskı düşürür — kâğıdın geldiğinin kanıtı odur.
            if (baskiSebebi == RestomenumReasons.OutOfPaper)
                _snapshots?.SetPaperWarning(request.TerminalId, true, baskiAdimi);
            else if (kapandi && baskiAdimi is null)
                // W122: uyarıyı düşüren TEK yer burası — OLAĞAN bir satışın zinciri baştan sona
                // sorunsuz geçti, yani normal işleyiş geri döndü. Kurtarmanın başarısı bunu
                // KANITLAMAZ: kurtarma zaten uyarıyı doğuran olayı çözüyor ve rulo çıkıkken bile
                // başarılı olabildiği ölçüldü (04:20).
            {
                _snapshots?.SetPaperWarning(request.TerminalId, false);
                // ══ W125 — CİHAZIN DÜZELDİĞİNİN TEK KANITI BURASI ═════════════════════
                //
                // Ölçüm (2026-09-10 06:37): kâğıt bitti, süpürme 484 deneme boyunca uğraştı ve
                // cihaz kademeli kötüleşti — 0xF01C 5,5 sn → 0xF003 13,8 sn → 0xF000 66 sn.
                // Tek PAHALI deneme otomatik pencereyi kapattı ve fiş **24 saat** park edildi.
                // Üzerinde 990 kuruş vardı. Rulo takılsa bile kendiliğinden kapanmayacaktı:
                // kural doğru çalıştı, eksik olan şey ertelemenin İPTAL EDİLEBİLİR olmasıydı.
                //
                // Burası, cihazın çalıştığını KANITLAYAN tek nokta: olağan bir satışın zinciri
                // baştan sona geçti, kâğıt var ve cihaz komut alıyor. Tahmin değil, ölçüm.
                // Bu yüzden park edilmiş fişler burada serbest bırakılır — yeni bir cihaz
                // çağrısı eklemeden, bedelsiz.
                //
                // ⚠️ Kurtarmanın başarısı bunu yapamaz: kurtarma rulo çıkıkken bile
                // başarılı olabiliyor (04:20'de ölçüldü) — yani cihazın düzeldiğini kanıtlamaz.
                _snapshots?.UndeferPendingFinishes();
            }
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

                // ══ W145 — MALİ FİŞ NUMARASI: KALICI KAYITTAN SONRA, ve YUTULARAK ═══════
                //
                // ⚠️ BU BLOK ÖNCE YANLIŞ YERDEYDİ ve bağımsız denetim yakaladı. `Close` ile
                // `MarkTicketClosed` ARASINDA duruyordu: yani PARA ALINMIŞ ve mali fiş KESİLMİŞ
                // olduğu hâlde, kalıcı "kapandı, bildirilmedi" kaydı henüz yazılmamışken cihaza
                // bir çağrı daha yapıyordu. O çağrı fırlatsaydı satış istisnayla düşer, kapanış
                // deftere HİÇ yazılmaz, bağ silinmez ve aynı oturumun sonraki satışı kapanmış
                // fişin kimliğini devralırdı. Hemen yukarıdaki "SIRA ÖNEMLİ" uyarısı zaten bunu
                // söylüyordu; ben onu ihlal etmişim.
                //
                // Şimdi: önce para güvenliği (kapanış kaydı + bağın silinmesi), SONRA numara.
                // Numara bir ZENGİNLEŞTİRME; elde edilemezse kapanış yine sağlam.
                //
                // ⚠️ try/catch BİLEREK: `FP3_Echo` yanıtı JSON'dan çözülüyor ve W142 ile
                // sayaç alanları eklendi — beklenmedik bir değer ayrıştırıcıyı FIRLATABİLİR.
                // Tamamlanmış bir satışı, bilgi amaçlı bir okuma yüzünden patlatmak kabul
                // edilemez. "Okunamazsa alan boş kalır" kuralı dönüş koduna ek olarak
                // İSTİSNAYI da kapsamalı.
                //
                // ⚠️ ÇIKARMA: `FisNo` SONRAKİ numaradır (2026-09-12 01:05→01:08: önce 3,
                // kâğıtta 3, sonra 4).
                try
                {
                    if (_gmp.Echo(out var sayac).Ok && sayac.FisNo > 0)
                    {
                        mailFisNo = sayac.FisNo - 1;
                        if (fisId is not null) _snapshots?.SetFiscalReceiptNo(fisId, mailFisNo.Value);
                        _log("[gmp] mali fiş numarası", new
                        {
                            fisNo = mailFisNo, okunanSonraki = sayac.FisNo, zNo = sayac.ZNo, fisId,
                        });
                    }
                    else
                    {
                        _log("[gmp] mali fiş numarası OKUNAMADI — alan boş bırakıldı", new { fisId });
                    }
                }
                catch (Exception e)
                {
                    // Satış TAMAMLANDI ve deftere yazıldı; burası yalnız zenginleştirme.
                    _log("[gmp] mali fiş numarası okunurken HATA — satış etkilenmedi", new
                    {
                        fisId, hata = e.GetType().Name,
                    });
                }
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

        // ── W79 §6b: KAPANMIŞ FİŞTE KALAN = 0, AMA YALNIZ ÖLÇÜLMÜŞSE ────────────
        // Platform iki alanı ÇİFT kabul ediyor (`bodyRead.js:99-106`): `deviceRemainingMinor`
        // yoksa `deviceTicketTotalMinor` de düşüyor. Kapanmış fişin kalanı GERÇEKTEN sıfırdır —
        // belirsizlik değil. Ama kapanış okuması başarısızsa toplam da yoktur; o hâlde ÜÇÜ DE
        // konmaz. Uydurulmuş bir sıfır hiçbir zaman gitmez.
        if (fisDurumu == "CLOSED" && cihazToplam is not null && cihazTahsil is not null)
            cihazKalan = 0;

        return new TransportResult(
            TransportOutcome.Approved,
            ApprovedAmountMinor: request.AmountMinor,
            RequestedAmountMinor: request.AmountMinor,
            CollectedAmountMinor: tahsil,
            MerchantDiscountMinor: isyeriIndirimi,
            Rrn: tk.Rrn,
            CardLast4: tk.CardLast4,
            ProviderResultCode: pr.ToString(),
            Info: bilgi,
            // W104: ödeme ALINDI (sonuç `Approved` KALIR) ama fiş basılamadı/kapanamadı.
            Reason: baskiSebebi,
            PrintFailedStep: baskiAdimi,
            PrintFailedCode: baskiKodu,
            UsedBankBkmId: banka,
            TicketState: fisDurumu,
            TicketId: fisId,
            ClosedTicketPayments: kapanisOdemeleri,
            DevicePaidMinor: cihazTahsil,
            DeviceTicketTotalMinor: cihazToplam,
            DeviceRemainingMinor: cihazKalan,
            FiscalReceiptNo: mailFisNo);
    }
}
