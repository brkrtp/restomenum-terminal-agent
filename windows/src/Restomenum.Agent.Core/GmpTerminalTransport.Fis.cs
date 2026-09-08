namespace Restomenum.Agent.Core;

/// <summary>
/// <b>FİŞ YAŞAM DÖNGÜSÜ</b> — <see cref="GmpTerminalTransport"/>'un parçası (W43 bölmesi).
///
/// Açık fişe devam etme kararı, önceki denemeden kalan bayat fişin temizlenmesi ve fişin
/// kapatılması. Üçü de "cihazdaki fişe DOKUNMADAN önce kanıt iste" kuralının uygulaması:
/// devam etmeden önce içerik doğrulanır, silmeden önce ödeme sayacı okunur, kapandı demeden
/// önce `FP3_Close`'un dönüş kodu okunur.
///
/// <para><b>Neden `partial`, ayrı sınıf değil:</b> bu metotlar `_handle` (cihaz tanıtıcısı)
/// ve onu koruyan `_gate` kilidi üzerinde ORTAK ÇALIŞIYOR. Ayrı sınıflara bölmek o değişken
/// durumu sınıflar arasında paylaştırmak demekti — bölmenin amacı okunabilirlikti, yeni bir
/// paylaşım yüzeyi açmak değil. `partial` ile derlenen tür AYNI kalıyor: davranış değişmez.</para>
/// </summary>
public sealed partial class GmpTerminalTransport
{
    /// <summary>
    /// Acik fise devam etmeden onceki guvenlik kontrolleri. <c>null</c> = devam edilebilir.
    ///
    /// <para><b>Neden sart:</b> bag "bu fis bu satisa ait" der ama fisin ICERIGININ hala o satisla
    /// ayni oldugunu SOYLEMEZ. Kasiyer kismi odemeden sonra kalem eklemis/silmisse, devam etmek
    /// fise yanlis tutarda odeme yazmaktir - mali kayit bozulur ve geri alinamaz.</para>
    /// </summary>
    /// <param name="fis">
    /// Bu metodun OKUDUĞU fiş (W41). Dönüş <c>null</c> ise — yani devam edilebiliyorsa — çağıran
    /// bunu anlık görüntü olarak yeniden kullanır ve <b>ikinci bir <c>GetTicket</c> yapmaz</b>.
    /// Hata dönüşlerinde de atanmıştır ama anlamı yoktur (okuma başarısızsa boş kayıt).
    /// </param>
    private TransportResult? DevamDogrula(ulong handle, SaleRequest request, out GmpTicket fis)
    {
        var of = _gmp.OptionFlags(handle, GmpEchoFlags.Reload);
        var gt = _gmp.GetTicket(handle, out fis);
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
            // ── W50: `PrintUserMessage` ÇIKARILDI ─────────────────────────────────
            // Ajan bu adımı BOŞ bir mesajla çağırıyordu (`GmpWrapper`: varsayılan
            // `ST_USER_MESSAGE`), yani basılan bir kullanıcı mesajı YOKTU — ama 14 kapanış
            // üzerinden ölçülen maliyeti **353 ms** (338–378), zincirin %11'i, her tam ödemede.
            //
            // Mali kapanış için gerekli DEĞİL: sertifikalı referansın üç kapanış yolundan İKİSİNDE
            // bu adım hiç yok (`restomenum.gmp3/Controllers/DLLController.cs:508-525` ve
            // `ClosePaidTicket()` ~1212). Ajan, adımı içeren üçüncü yolu (satır 907-947)
            // kopyalamıştı.
            //
            // ⚠️ Kalan üç adım ZORUNLU ve sırası değişmez: `PrintBeforeMF` fişi mali hafızaya
            // yazan taahhüt noktası (ondan sonra `VoidAll` 2357 döner), `PrintMF` fiziksel basım,
            // `PrintTotalsAndPayments` üçünün de ilk adımı.
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
}
