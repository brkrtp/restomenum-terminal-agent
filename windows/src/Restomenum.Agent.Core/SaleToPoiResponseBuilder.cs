using System.Globalization;
using System.Text.Json.Nodes;

namespace Restomenum.Agent.Core;

/// <summary>
/// Terminal sonucundan kanonik <c>SaleToPOIResponse</c> üretir (yerel sözleşme, K-21).
/// <b>TEK gövde:</b> hem kasaya senkron döner hem platforma bildirim olarak gider — iki şekil olsaydı
/// kasiyerin gördüğü ile deftere yazılan ıraksardı.
///
/// <para><b>Güvenlik değişmezi:</b> para HAREKET ETMİŞ OLABİLECEK bir sonucu ASLA kesin-ret
/// (<c>Refusal</c>) diye bildirmeyiz — yoksa kasiyer yeniden dener ve ilk işlem geçmişse ikinci çekim
/// olur. Yalnız <see cref="TransportOutcome.Declined"/> (para hareket etmedi, kesin) kesin-ret olur;
/// <see cref="TransportOutcome.Busy"/>/<see cref="TransportOutcome.Unknown"/>/açık-fiş → belirsiz
/// (<c>ErrorCondition</c> platformda <c>unknown</c>'a düşer).</para>
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
        var (success, errorCondition) = MapOutcome(result.Outcome);

        var response = new JsonObject { ["Result"] = success ? "Success" : "Failure" };
        response["ErrorCondition"] = errorCondition;   // Success'te null
        if (result.ProviderResultCode is not null) response["AdditionalResponse"] = result.ProviderResultCode;

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

        var envelope = new JsonObject
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
                    ["PaymentResult"] = paymentResult,
                    ["Response"] = response,
                },
            },
        };
        return envelope.ToJsonString();
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
    /// <para>Eşleme platformun ayrıştırıcısıyla hizalı: kesin-ret {Refusal,…} → <c>declined</c>;
    /// gerisi → <c>unknown</c>. Bilinçli olarak SADECE <c>Declined</c>'ı kesin-ret yapıyoruz.</para>
    /// </summary>
    public static (bool Success, string? ErrorCondition) MapOutcome(TransportOutcome outcome) => outcome switch
    {
        TransportOutcome.Approved => (true, null),
        // Para HAREKET ETMEDİ, kesin → platform 'declined'.
        TransportOutcome.Declined => (false, "Refusal"),
        // Para HAREKET ETMİŞ OLABİLİR (saha: RECV_BUSY başarılı ödemeden SONRA geldi) → 'unknown'.
        TransportOutcome.Busy => (false, "Busy"),
        // Belirsiz (timeout/kopma) → 'unknown'.
        TransportOutcome.Unknown => (false, "InProgress"),
        // Açık fiş: tutarsız durum, güvenli taraf = belirsiz (kesin-ret DEĞİL).
        TransportOutcome.TicketAlreadyOpen => (false, "InProgress"),
        _ => (false, "InProgress"),
    };

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
