using System.Globalization;
using System.Text.Json.Nodes;

namespace Restomenum.Agent.Core;

/// <summary>
/// Terminal sonucundan kanonik <c>SaleToPOIResponse</c> üretir (yerel sözleşme, K-21).
/// <b>TEK gövde:</b> hem kasaya senkron döner hem platforma bildirim olarak gider — iki şekil olsaydı
/// kasiyerin gördüğü ile deftere yazılan ıraksardı.
///
/// <para><b>Güvenlik değişmezi (W1):</b> para HAREKET ETMİŞ OLABİLECEK bir sonucu ASLA kesin-ret
/// (<c>Refusal</c>) diye bildirmeyiz — yoksa kasiyer yeniden dener ve ilk işlem geçmişse ikinci çekim
/// olur. Bu üretici <b>hiçbir yerde <c>Refusal</c> üretmez</b>: kesin-ret yalnız banka host'unun açık
/// ret yanıtıyla (issuer yanıt kodu) doğrulanabilir ve agent bugün o kanalı okumuyor
/// (<c>ST_PaymentErrMessage.ErrorCode</c> sarmalayıcıda yüzeye çıkarılmadı, canlıda da ölçülmedi).
/// Okunduğu gün <c>Refusal</c>'ın tek doğum yeri orası olacak — kova bazında ASLA.</para>
///
/// <para>Kart verisi: yalnız maskeli (<c>MaskedPan</c> ≤4 hane — <see cref="TransportResult"/> zaten
/// ham PAN taşımıyor, §12.3). Bahşiş gönderilmez (v1'de kapalı). <c>Currency</c> gönderilmez
/// (emin değilsek göndermeme kuralı → currencyMismatch riski yok); <c>AuthorizedAmount</c> yeterli.</para>
/// </summary>
public static class SaleToPoiResponseBuilder
{
    /// <summary>
    /// Terminal sonucu → sonuç gövdesi (<c>PaymentResponse</c>). Kasaya senkron + platforma bildirim.
    /// <paramref name="exponent"/> AuthorizedAmount'ı tel ondalığına çevirmek için (para biriminin
    /// minor basamağı, GET yanıtından; sabit değil).
    /// </summary>
    public static string BuildResult(SaleToPoiRequest req, TransportResult result, int exponent, DateTimeOffset now)
    {
        var (success, varsayilan) = MapOutcome(result.Outcome);
        // KAYNAK ÖNCELİĞİ: koşulu, kodu gören yer (`GmpErrorMap`) belirler; kova (`TransportOutcome`)
        // yalnız yedektir. Tersi olsaydı "host'a ulaşılamadı" ile "sonuç belirsiz" aynı kovada
        // eriyip aynı şeye dönerdi.
        var errorCondition = success ? null : (result.ErrorCondition ?? varsayilan);
        var additional = result.ProviderResultCode;

        // SÖZLEŞME DEĞİŞMEZİ (§30.5): Result:"Success" = "para HAREKET ETTİ" → AuthorizedAmount ZORUNLU
        // ve POZİTİF olmalı. Approved ama tutar yok/≤0 (çelişkili sonuç: örn. probe sayaç-artışını
        // Landed sandı ama tutar 0) ise Success DEME. Güvenli belirsize düşür (unknown → operatör;
        // kesin-ret DEĞİL, kasiyeri yeniden denemeye itmez, çift-çekim yok). Herhangi bir üst-katman
        // hatası sızsa bile sahte-onayı SINIRDA durduran son savunma; kasa senkron yanıta güvense de tutar.
        if (success && !(result.ApprovedAmountMinor is long amt0 && amt0 > 0))
        {
            success = false;
            errorCondition = "InProgress";
            additional = additional is null ? "AUTHORIZED_AMOUNT_INVALID" : $"{additional};AUTHORIZED_AMOUNT_INVALID";
        }

        var response = new JsonObject { ["Result"] = success ? "Success" : "Failure" };
        response["ErrorCondition"] = errorCondition;   // Success'te null
        if (additional is not null) response["AdditionalResponse"] = additional;

        var paymentResult = new JsonObject();
        if (success && result.ApprovedAmountMinor is long amt)
        {
            paymentResult["AmountsResp"] = new JsonObject
            {
                // AuthorizedAmount ondalık; çarpan exponent'ten (sabit değil).
                ["AuthorizedAmount"] = JsonValue.Create(Money.ToWire(amt, exponent)),
            };
        }

        var acquirer = new JsonObject();
        if (result.ApprovalCode is not null) acquirer["ApprovalCode"] = result.ApprovalCode;
        if (result.Rrn is not null) acquirer["AcquirerTransactionID"] = new JsonObject { ["TransactionID"] = result.Rrn };
        // NOT: AcquirerID ve POIData.POITransactionID(stan) TransportResult'ta YOK → gönderilmiyor.
        if (acquirer.Count > 0) paymentResult["PaymentAcquirerData"] = acquirer;

        if (result.CardLast4 is not null || result.Scheme is not null)
        {
            var card = new JsonObject();
            if (result.CardLast4 is not null) card["MaskedPan"] = result.CardLast4;   // ≤4 hane, kurala uyar
            if (result.Scheme is not null) card["PaymentBrand"] = result.Scheme;
            paymentResult["PaymentInstrumentData"] = new JsonObject { ["CardData"] = card };
        }

        var govde = new JsonObject
        {
            ["MessageHeader"] = Header(req, "Response"),
            ["PaymentResponse"] = new JsonObject
                {
                    ["SaleData"] = new JsonObject
                    {
                        ["SaleTransactionID"] = new JsonObject
                        {
                            ["TransactionID"] = req.PaymentId,
                            ["TimeStamp"] = Iso(now),
                        },
                    },
                ["PaymentResult"] = paymentResult,
                ["Response"] = response,
            },
        };
        var ek = Ek(result.PaymentInvoked, result.Reason, result.Info);
        // Cihazın gördüğü tutarlar YALNIZ kalan/toplam ayrışmasında taşınır (aşağıdaki iki sebep).
        // Her gövdeye koymak, alanı "bazen doğru bazen bayat" bir veriye çevirirdi.
        // Fiş durumu: platform satırları fiş KAPANINCA tek seferde yazacak; kasa da "bu ödeme
        // deftere geçti mi" sorusunu bununla cevaplıyor.
        if (result.TicketState is not null) ek["ticketState"] = result.TicketState;
        // Fiş kimliği: `ticketState` tek başına yetmez. Aynı masa oturumunda arka arkaya İKİ fiş
        // kapanabilir (kısmi öde → kapan → aynı masaya yeni sipariş); platform hangi ödemenin
        // hangi fişe ait olduğunu ancak bununla bilir. Bağ yoksa alan KONMAZ — uydurulmaz.
        if (result.TicketId is not null) ek["ticketId"] = result.TicketId;
        // Kullanılan banka — YALNIZ ölçüldüyse. Alanın YOKLUĞU "banka bilinmiyor" demektir;
        // 0 ya da tahmini bir değer koymak paneli yanlış bankaya inandırırdı.
        if (result.UsedBankBkmId is int kb) ek["bankBkmId"] = kb;
        if (result.DeviceTicketTotalMinor is long dt) ek["deviceTicketTotalMinor"] = dt;
        if (result.DeviceRemainingMinor is long dk) ek["deviceRemainingMinor"] = dk;
        govde["Restomenum"] = ek;
        return new JsonObject { ["SaleToPOIResponse"] = govde }.ToJsonString();
    }

    /// <summary>
    /// Terminale HİÇ gitmeden üretilen başarısızlık gövdesi (GET reddi / PRODUCT_UNMAPPED gibi).
    /// Para hareket etmedi; <paramref name="errorCondition"/> platformun sınıflandırmasını belirler
    /// (kesin-ret listesi → declined, gerisi → unknown). <paramref name="additionalResponse"/> ASCII
    /// makine kodu (Türkçe/serbest metin YASAK — tüm sonucu reddettirir).
    /// </summary>
    public static string BuildFailure(SaleToPoiRequest req, string errorCondition, string? additionalResponse,
        DateTimeOffset now, string? reason = null)
    {
        var response = new JsonObject { ["Result"] = "Failure", ["ErrorCondition"] = errorCondition };
        if (additionalResponse is not null) response["AdditionalResponse"] = additionalResponse;
        return new JsonObject
        {
            ["SaleToPOIResponse"] = new JsonObject
            {
                ["MessageHeader"] = Header(req, "Response"),
                ["PaymentResponse"] = new JsonObject
                {
                    ["SaleData"] = new JsonObject
                    {
                        ["SaleTransactionID"] = new JsonObject
                        {
                            ["TransactionID"] = req.PaymentId,
                            ["TimeStamp"] = Iso(now),
                        },
                    },
                    ["PaymentResult"] = new JsonObject(),
                    ["Response"] = response,
                },
                // Bu üretici YALNIZ terminale gidilmeden üretilen retlerde çağrılıyor → ödeme
                // fonksiyonu kesinlikle çalışmadı.
                ["Restomenum"] = Ek(paymentInvoked: false, reason),
            },
        }.ToJsonString();
    }

    /// <summary>
    /// İlerleme bildirimi — <b>top-level <c>EventNotification</c></b> (yerel sözleşme, K-21).
    /// <b>YALNIZ platforma gider</b>; kasaya senkron dönen şey DAİMA <c>PaymentResponse</c>'tur.
    ///
    /// <para><see cref="ProgressEvent"/> platformun tanımladığı sapma enum'udur (nexo resmî değil) —
    /// bu yüzden enum'la sınırlı; tanınmayan olay sunucuda sessizce düşer.</para>
    /// </summary>
    public static string BuildProgress(SaleToPoiRequest req, ProgressEvent evt, DateTimeOffset now) =>
        new JsonObject
        {
            ["EventNotification"] = new JsonObject
            {
                ["SaleData"] = new JsonObject
                {
                    ["SaleTransactionID"] = new JsonObject { ["TransactionID"] = req.PaymentId },
                },
                ["EventToNotify"] = evt.ToString(),
                ["TimeStamp"] = Iso(now),
            },
        }.ToJsonString();

    /// <summary>
    /// Terminal sonucu (<see cref="TransportOutcome"/>) → (Result Success mı, ErrorCondition).
    ///
    /// <para><b>Yalnız yedek.</b> Asıl koşul <see cref="TransportResult.ErrorCondition"/>'dadır
    /// (kaynağı <see cref="GmpErrorMap"/>). Buradaki değerler, koşulu yazılmamış bir sonuç geldiğinde
    /// güvenli tarafa düşmek içindir.</para>
    /// </summary>
    public static (bool Success, string? ErrorCondition) MapOutcome(TransportOutcome outcome) => outcome switch
    {
        TransportOutcome.Approved => (true, null),
        // Kova bazında **kesin-ret üretilmez.** `Declined` "para hareket etmedi" der ama NEDEN'ini
        // bilmez; `Refusal` ise kasiyere "kart reddedildi, başka kart isteyin" dedirtir. İkisi aynı
        // şey değil: terminale hiç gidilmemiş bir ret (eşlenmemiş ürün, kasiyer girişi yok) bu
        // mesajla raporlanırsa kasiyer olmayan bir kart sorununu kovalar. Gerçek koşulu
        // `TransportResult.ErrorCondition` taşır; buraya düşülüyorsa koşul yazılmamıştır ve
        // GÜVENLİ taraf belirsizdir.
        TransportOutcome.Declined => (false, "InProgress"),
        // Para HAREKET ETMİŞ OLABİLİR (saha: RECV_BUSY başarılı ödemeden SONRA geldi) → 'unknown'.
        TransportOutcome.Busy => (false, "Busy"),
        // Belirsiz (timeout/kopma) → 'unknown'.
        TransportOutcome.Unknown => (false, "InProgress"),
        // Açık fiş: tutarsız durum, güvenli taraf = belirsiz (kesin-ret DEĞİL).
        TransportOutcome.TicketAlreadyOpen => (false, "InProgress"),
        _ => (false, "InProgress"),
    };

    /// <summary>
    /// nexo-DIŞI ek blok (§22.8: standart dışı alanlar ayrı ad alanında). Kasaya "kart çekildi mi"
    /// sorusunun cevabını taşır — <c>ErrorCondition</c> bunu taşıyamaz, çünkü nexo'da adım kavramı yok.
    ///
    /// <para><c>paymentInvoked</c> ASLA atlanmaz: alanın yokluğu "bilmiyorum" ile "hayır" arasında
    /// yeni bir belirsizlik üretirdi ve W1'de kapattığımız kapı tam da buydu.</para>
    /// </summary>
    /// <summary>
    /// İptal sonucu (<c>ReversalResponse</c>). Ayrı üretici, çünkü iptalin cevabı ödemeninkinden
    /// farklı bir soruya cevap veriyor: "para geri gitti mi / fiş kapandı mı".
    ///
    /// <para><b>Yarım kalma en tehlikeli sonuç:</b> `Failure` + <c>VOID_INCOMPLETE</c>. Kasiyere
    /// "iptal olmadı, tekrar dene" dedirtmek YASAK — yarım kalmış bir ters işlemin üstüne ikincisini
    /// bindirmek, geri alınmış bir ödemeyi ikinci kez geri almaya çalışmaktır.</para>
    /// </summary>
    public static string BuildReversalResult(
        ReversalRequest req, TransportResult result, int exponent, DateTimeOffset now)
    {
        var basarili = result.Outcome == TransportOutcome.Approved;
        var response = new JsonObject { ["Result"] = basarili ? "Success" : "Failure" };
        if (!basarili) response["ErrorCondition"] = result.ErrorCondition ?? "InProgress";
        if (result.ProviderResultCode is not null) response["AdditionalResponse"] = result.ProviderResultCode;

        var govde = new JsonObject { ["Response"] = response };
        // Tutar YALNIZ gerçekten geri alınan para varsa. Ödemesiz bir fişin iptalinde tutar
        // bildirmek, olmayan bir iadeyi deftere yazdırırdı.
        if (basarili && result.ApprovedAmountMinor is long geri && geri > 0)
            govde["ReversedAmount"] = JsonValue.Create(Money.ToWire(geri, exponent));

        return new JsonObject
        {
            ["SaleToPOIResponse"] = new JsonObject
            {
                ["MessageHeader"] = new JsonObject
                {
                    ["ProtocolVersion"] = "3.0",
                    ["MessageClass"] = "Service",
                    ["MessageCategory"] = "Reversal",
                    ["MessageType"] = "Response",
                    ["ServiceID"] = req.ServiceId,
                    ["SaleID"] = req.SaleId,
                    ["POIID"] = req.PoiId,
                },
                ["ReversalResponse"] = govde,
                // İptalde `FP3_Payment` çağrılmaz; alan yine de ATLANMAZ (sözleşme: hep var).
                ["Restomenum"] = IptalEk(req, result),
            },
        }.ToJsonString();
    }

    /// <summary>
    /// FİŞ bazlı iptalin sonucu (<c>scope: "ticket"</c>) — kasiyerin "Fiş İptal" düğmesi.
    ///
    /// <para><b><c>cancelledSaleSessionId</c> neden şart:</b> kasiyer cihazın başında ve gördüğü
    /// fişi iptal ediyor; o fiş BAŞKA bir oturuma ait olabilir. Platform hangi oturumun satırlarını
    /// düşüreceğini isteğe göre değil, GERÇEKTEN iptal edilen fişe göre bilmeli. Bağ yoksa
    /// <c>null</c> gider — "bilmiyorum" da bir cevaptır ve uydurmaktan iyidir.</para>
    ///
    /// <para><b>Açık fiş yoksa başarısızlık DEĞİL:</b> <c>Result: Success</c> + sayaçlar 0 +
    /// <c>info: TICKET_NOT_OPEN</c>. Kasiyer düğmeye bastı, iptal edilecek bir şey yoktu.</para>
    /// </summary>
    public static string BuildTicketReversalResult(
        ReversalRequest req, TicketVoidResult sonuc, int exponent, DateTimeOffset now)
    {
        var basarili = sonuc.Outcome == TransportOutcome.Approved;
        var response = new JsonObject { ["Result"] = basarili ? "Success" : "Failure" };
        if (!basarili) response["ErrorCondition"] = sonuc.ErrorCondition ?? "InProgress";
        if (sonuc.ProviderResultCode is not null) response["AdditionalResponse"] = sonuc.ProviderResultCode;

        var govde = new JsonObject { ["Response"] = response };
        // Tutar YALNIZ gerçekten geri alınan para varsa — ödemesiz fişin iptalinde olmayan bir
        // iadeyi deftere yazdırmayalım.
        if (basarili && sonuc.VoidedAmountMinor > 0)
            govde["ReversedAmount"] = JsonValue.Create(Money.ToWire(sonuc.VoidedAmountMinor, exponent));

        var ek = new JsonObject
        {
            ["v"] = 1,
            // İptal yolunda `FP3_Payment` çağrılmaz; alan yine de ATLANMAZ.
            ["paymentInvoked"] = false,
            ["scope"] = "ticket",
            ["voidedPaymentCount"] = sonuc.VoidedPaymentCount,
            ["voidedAmountMinor"] = sonuc.VoidedAmountMinor,
            // Bağ yoksa AÇIKÇA null: platform "bilinmiyor" ile "yok"u ayırt edebilsin.
            ["cancelledSaleSessionId"] = sonuc.CancelledSaleSessionId is null
                ? null : JsonValue.Create(sonuc.CancelledSaleSessionId),
        };
        if (req.SaleSessionId is not null) ek["saleSessionId"] = req.SaleSessionId;
        // Komut kimliği AYNEN döner — platform açtığı iptali bununla eşleştiriyor; uyuşmazsa
        // 409 `voidNotRequested` üretip alarm veriyor. Üretmiyoruz, yansıtıyoruz.
        if (req.TicketCancelId is not null) ek["ticketCancelId"] = req.TicketCancelId;
        if (sonuc.Reason is not null) ek["reason"] = sonuc.Reason;
        ek["info"] = basarili
            ? (sonuc.TicketWasOpen ? RestomenumReasons.TicketCancelled : RestomenumReasons.TicketNotOpen)
            : null;
        if (ek["info"] is null) ek.Remove("info");

        return new JsonObject
        {
            ["SaleToPOIResponse"] = new JsonObject
            {
                ["MessageHeader"] = new JsonObject
                {
                    ["ProtocolVersion"] = "3.0",
                    ["MessageClass"] = "Service",
                    ["MessageCategory"] = "Reversal",
                    ["MessageType"] = "Response",
                    ["ServiceID"] = req.ServiceId,
                    ["SaleID"] = req.SaleId,
                    ["POIID"] = req.PoiId,
                },
                ["ReversalResponse"] = govde,
                ["Restomenum"] = ek,
            },
        }.ToJsonString();
    }

    /// <summary>
    /// <b>ONAY GERİ ÇEKME</b> — daha önce <c>Success</c> diye bildirilmiş bir sonucun düzeltmesi.
    ///
    /// <para>Gövde satış sonucu biçimindedir (<c>PaymentResponse</c>) çünkü düzelttiği şey bir
    /// satış sonucudur. <c>Result: Failure</c> + <c>InProgress</c>: "artık onaylı demiyorum, ama
    /// yerine kesin bir şey de diyemiyorum" — geri çekmek, tersini iddia etmek değildir.</para>
    ///
    /// <para><c>AuthorizedAmount</c> GÖNDERİLMEZ: geri çekilen zaten o tutarın iddiasıydı.</para>
    /// </summary>
    public static string BuildLandedRetraction(
        string serviceId, string saleId, string poiId, string paymentId, DateTimeOffset now) =>
        new JsonObject
        {
            ["SaleToPOIResponse"] = new JsonObject
            {
                ["MessageHeader"] = new JsonObject
                {
                    ["ProtocolVersion"] = "3.0",
                    ["MessageClass"] = "Service",
                    ["MessageCategory"] = "Payment",
                    ["MessageType"] = "Response",
                    ["ServiceID"] = serviceId,
                    ["SaleID"] = saleId,
                    ["POIID"] = poiId,
                },
                ["PaymentResponse"] = new JsonObject
                {
                    ["SaleData"] = new JsonObject
                    {
                        ["SaleTransactionID"] = new JsonObject
                        {
                            ["TransactionID"] = paymentId,
                            ["TimeStamp"] = Iso(now),
                        },
                    },
                    ["PaymentResult"] = new JsonObject(),
                    ["Response"] = new JsonObject
                    {
                        ["Result"] = "Failure",
                        ["ErrorCondition"] = "InProgress",
                        ["AdditionalResponse"] = RestomenumReasons.LandedRetracted,
                    },
                },
                // `paymentInvoked: true` — FP3_Payment gerçekten çağrılmıştı. Geri çekilen,
                // "para hareket etti" iddiası; "çağırdık mı" sorusu değişmedi.
                ["Restomenum"] = Ek(paymentInvoked: true, reason: null,
                    info: RestomenumReasons.LandedRetracted),
            },
        }.ToJsonString();

    /// <summary>
    /// <b>FİŞ KAPANDI</b> bildirimi (K-26/P27) — <c>POST /plugin-api/payments/ticket-closed</c>.
    ///
    /// <para><b>Neden var:</b> kullanıcı kararı, ödemeler deftere fiş TAMAMEN kapandıktan sonra
    /// yazılıyor. Kısmi ödemede satır yazmak, sonradan iptal edilen bir fişin satırlarını geri
    /// almayı gerektirirdi.</para>
    ///
    /// <para><b>Ödemeler BİZİM defterimizden gelir</b>, cihazın listesinden değil: cihaz bizim
    /// <c>paymentId</c>'mizi bilmez ve BAŞARISIZ denemeleri de fişte kayıt olarak tutar (ölçüldü
    /// 2026-09-07: banka hattı yokken kart bacağı <c>payAmount=0</c> ile fişte duruyor). Sıraya
    /// bakıp eşleştirmek sessizce kayardı.</para>
    ///
    /// <para><b><c>paidMinor</c> CİHAZDAN gelir, listemizin toplamı DEĞİL.</b> İkisi ayrı ayrı
    /// gittiği için platform "listede olmayan bir tahsilat var mı" sorusunu kendi başına
    /// cevaplayabilir; tek sayı gönderseydik eksik liste sessizce tutarlı görünürdü.</para>
    ///
    /// <para>Zarf <c>/ticket-cancel/result</c> ile bilerek aynı şekilde; <c>MessageCategory</c>
    /// <c>"Event"</c>. nexo'nun <c>EventNotification</c> şemasına uyduğu İDDİA EDİLMİYOR — tüm
    /// alanlar <c>Restomenum</c> bloğunda.</para>
    /// </summary>
    public static string BuildTicketClosed(
        string terminalId, string ticketId, string? saleSessionId,
        IReadOnlyList<TicketPaymentRow> payments, long? totalMinor, long? paidMinor)
    {
        var satirlar = new JsonArray();
        foreach (var o in payments)
        {
            var satir = new JsonObject
            {
                ["paymentId"] = o.PaymentId,
                ["amountMinor"] = o.AmountMinor,
            };
            // Tip adı YALNIZ bildiğimiz üç değerde. Bilmediğimiz bir tipe isim uydurmak, defterde
            // yanlış ödeme türü demektir; alanı koymamak "bilmiyorum" der ve tutar yine gider.
            var ad = o.MethodType switch
            {
                GmpPaymentTypes.Cash => "cash",
                GmpPaymentTypes.Card => "card",
                GmpPaymentTypes.Mobile => "qr",
                _ => null,
            };
            if (ad is not null) satir["methodType"] = ad;
            if (o.BankBkmId is int bkm) satir["bankBkmId"] = bkm;
            satirlar.Add(satir);
        }

        var ek = new JsonObject
        {
            ["v"] = 1,
            ["scope"] = "ticket",
            ["ticketId"] = ticketId,
            ["ticketState"] = "CLOSED",
            ["payments"] = satirlar,
        };
        if (saleSessionId is not null) ek["saleSessionId"] = saleSessionId;
        if (totalMinor is long t) ek["totalMinor"] = t;
        if (paidMinor is long pd) ek["paidMinor"] = pd;

        return new JsonObject
        {
            ["SaleToPOIResponse"] = new JsonObject
            {
                // Sözleşmede yazan İKİ alan. Fazlasını koymuyoruz: doğrulamadığımız bir alana
                // beklenmedik bir değer yazmak, bildirimi 400'e düşürüp fişi deftersiz bırakırdı.
                ["MessageHeader"] = new JsonObject
                {
                    ["MessageCategory"] = "Event",
                    ["POIID"] = terminalId,
                },
                ["Restomenum"] = ek,
            },
        }.ToJsonString();
    }

    /// <summary>
    /// ÖDEME bazlı iptalin <c>Restomenum</c> bloğu — fiş bazlı iptalinkiyle AYNI alanlar.
    ///
    /// <para><b>Neden aynı:</b> kasa 17:44'te iptalin sonucunu gördü ama "ne iptal edildi"i
    /// göremedi: sayaçlar yalnız fiş kapsamında üretiliyordu (ölçüldü 2026-09-07). Aynı olayın
    /// kasaya ve deftere iki farklı zenginlikte gitmesi, kasada "2 ödeme / 2,00 ₺ iptal edildi"
    /// diyememek ve BAŞKA bir oturumun fişinin iptal edildiğini fark edememek demekti.</para>
    ///
    /// <para>Sayaçlar yalnız ÖLÇÜLDÜYSE konur; iptal yarım kaldıysa (<c>VOID_INCOMPLETE</c>)
    /// taşıma katmanı zaten doldurmaz — "kaç ödeme geri gitti" o durumda bilinmiyor demektir.</para>
    /// </summary>
    private static JsonObject IptalEk(ReversalRequest req, TransportResult result)
    {
        var ek = Ek(paymentInvoked: false, result.Reason, result.Info);
        if (result.VoidedPaymentCount is int vs) ek["voidedPaymentCount"] = vs;
        if (result.VoidedAmountMinor is long vt) ek["voidedAmountMinor"] = vt;
        // Bağ yoksa AÇIKÇA null: platform "bilinmiyor" ile "yok"u ayırt edebilsin (fiş bazlı
        // iptalde de böyle davranıyoruz).
        if (result.VoidedPaymentCount is not null)
            ek["cancelledSaleSessionId"] = result.CancelledSaleSessionId is null
                ? null : JsonValue.Create(result.CancelledSaleSessionId);
        if (req.SaleSessionId is not null) ek["saleSessionId"] = req.SaleSessionId;
        return ek;
    }

    private static JsonObject Ek(bool paymentInvoked, string? reason, string? info = null)
    {
        var o = new JsonObject { ["v"] = 1, ["paymentInvoked"] = paymentInvoked };
        if (reason is not null) o["reason"] = reason;
        if (info is not null) o["info"] = info;
        return o;
    }

    private static JsonObject Header(SaleToPoiRequest req, string messageType) => new()
    {
        ["ProtocolVersion"] = "3.0",
        ["MessageClass"] = "Service",
        ["MessageCategory"] = "Payment",
        ["MessageType"] = messageType,
        ["ServiceID"] = req.ServiceId,
        ["SaleID"] = req.SaleId,
        ["POIID"] = req.PoiId,
    };

    private static string Iso(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
