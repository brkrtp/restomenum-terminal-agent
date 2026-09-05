using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Restomenum.Agent.Core;

/// <summary>
/// <c>SaleToPOIRequest</c> ayrıştırma + doğrulama (yerel sözleşme, K-21). Kurallar backend'in
/// (üretici) GARANTİSİDİR — tahmin değil. <b>Bilinmeyen alanlar YOK SAYILIR</b> (ileri-uyum §6.6):
/// yalnız bilinen alanlar okunur, fazlası sessizce geçilir.
/// </summary>
public static partial class SaleToPoiRequestParser
{
    public static SaleToPoiParseResult Parse(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return Red(SaleToPoiRejectReason.Malformed, "JSON değil"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("SaleToPOIRequest", out var env) || env.ValueKind != JsonValueKind.Object)
                return Red(SaleToPoiRejectReason.Malformed, "SaleToPOIRequest zarfı yok");
            if (!env.TryGetProperty("MessageHeader", out var hdr) || hdr.ValueKind != JsonValueKind.Object)
                return Red(SaleToPoiRejectReason.Malformed, "MessageHeader yok");

            // Kategori ÖNCE: tanınmayan kategori ayrı sebep (CAPABILITY_NOT_SUPPORTED yanıtına eşlenir).
            var category = Str(hdr, "MessageCategory");
            if (category is null) return Red(SaleToPoiRejectReason.Malformed, "MessageCategory yok");
            if (category != "Payment")
                return Red(SaleToPoiRejectReason.UnsupportedCategory, $"kategori '{category}' bu turda yok");

            if (Str(hdr, "MessageClass") != "Service" || Str(hdr, "MessageType") != "Request")
                return Red(SaleToPoiRejectReason.Malformed, "MessageClass/MessageType beklenmedik");

            var serviceId = Str(hdr, "ServiceID");
            if (string.IsNullOrEmpty(serviceId) || serviceId.Length > 10)
                return Red(SaleToPoiRejectReason.InvalidServiceId, "ServiceID boş ya da >10 karakter");

            var poiId = Str(hdr, "POIID");
            if (string.IsNullOrEmpty(poiId))
                return Red(SaleToPoiRejectReason.Malformed, "POIID yok");

            var saleId = Str(hdr, "SaleID") ?? "";   // boş olabilir (kasa göndermezse)
            if (saleId.Length > 32)
                return Red(SaleToPoiRejectReason.Malformed, "SaleID >32 karakter");

            if (!env.TryGetProperty("PaymentRequest", out var pr) || pr.ValueKind != JsonValueKind.Object)
                return Red(SaleToPoiRejectReason.Malformed, "PaymentRequest yok");

            // Kural 1: TUTAR (RequestedAmount) taşınmaz — ajan tutarı platformdan çeker. Kasanın GERÇEK
            // gövdesi currency-only bir PaymentTransaction (AmountsReq.Currency, RequestedAmount YOK)
            // yolluyor; o MEŞRU. Yalnız RequestedAmount VARSA reddet (bozuk/sahte).
            if (pr.TryGetProperty("PaymentTransaction", out var ptx) && ptx.ValueKind == JsonValueKind.Object
                && ptx.TryGetProperty("AmountsReq", out var amt) && amt.ValueKind == JsonValueKind.Object
                && amt.TryGetProperty("RequestedAmount", out _))
                return Red(SaleToPoiRejectReason.AmountNotAllowed, "RequestedAmount bu zarfta olamaz — tutar platformdan çekilir");

            if (!pr.TryGetProperty("SaleData", out var sd) || sd.ValueKind != JsonValueKind.Object)
                return Red(SaleToPoiRejectReason.Malformed, "SaleData yok");
            if (!sd.TryGetProperty("SaleTransactionID", out var stx) || stx.ValueKind != JsonValueKind.Object)
                return Red(SaleToPoiRejectReason.Malformed, "SaleTransactionID yok");

            var paymentId = Str(stx, "TransactionID");
            if (paymentId is null || !PaymentIdRegex().IsMatch(paymentId))
                return Red(SaleToPoiRejectReason.InvalidPaymentId, "TransactionID pay_+40hex değil");

            var tsRaw = Str(stx, "TimeStamp");
            if (tsRaw is null || !DateTimeOffset.TryParse(tsRaw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var ts))
                return Red(SaleToPoiRejectReason.Malformed, "TimeStamp ISO-8601 değil");

            var saleRef = Str(sd, "SaleReferenceID");
            if (saleRef is null)
                return Red(SaleToPoiRejectReason.Malformed, "SaleReferenceID yok");

            return new SaleToPoiParseResult.Ok(new SaleToPoiRequest(
                ServiceId: serviceId, SaleId: saleId, PoiId: poiId,
                PaymentId: paymentId, SaleReferenceId: saleRef, TimeStamp: ts));
        }
    }

    private static SaleToPoiParseResult Red(SaleToPoiRejectReason r, string detail) =>
        new SaleToPoiParseResult.Invalid(r, detail);

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex("^pay_[0-9a-fA-F]{40}$")]
    private static partial Regex PaymentIdRegex();
}
