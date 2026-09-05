using System.Text.Json;

namespace Restomenum.Agent.Core;

/// <summary>
/// <c>GET /plugin-api/payments/{paymentId}</c> yanıtını ayrıştırır. HTTP'den AYRI tutulur ki gerçek
/// gövdelerle birim test edilebilsin (istemci yalnız ince kabuk).
///
/// <para><b>nexo gövdesi <c>data</c> İÇİNDE</b> — kökte yalnız <c>success</c> var (platformun tek
/// hata sözleşmesi korunsun diye). Tutarlar JSON sayısı ve <see cref="Money.ToMinor"/> ile kuruşa
/// çevrilir (decimal okunur, double DEĞİL). 200 ama beklenmedik şekil → <see cref="PaymentRejectReason.Unknown"/>
/// (sessizce sürmemek için).</para>
/// </summary>
public static class PaymentDetailParser
{
    public static PaymentDetailResult Parse(int statusCode, string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        { return new PaymentDetailResult.Rejected(PaymentRejectReason.Unknown, $"JSON değil: {Kisalt(body)}", statusCode); }

        using (doc)
        {
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            if (!success || statusCode != 200)
            {
                var msg = StrOr(root, "message", "");
                return new PaymentDetailResult.Rejected(ReasonOf(msg, statusCode), msg, statusCode);
            }
            if (!root.TryGetProperty("data", out var data))
                return new PaymentDetailResult.Rejected(PaymentRejectReason.Unknown, "yanıtta data yok", statusCode);

            try
            {
                var ptx = data.GetProperty("PaymentTransaction");
                var amounts = ptx.GetProperty("AmountsReq");
                var ext = data.GetProperty("RestomenumExt");

                var items = new List<SaleLine>();
                // SaleItem yalnız TR'de; EU'da HİÇ yok → boş liste. (null/eksik kontrolü.)
                if (ptx.TryGetProperty("SaleItem", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in arr.EnumerateArray())
                    {
                        string? catId = null, lineId = null;
                        if (it.TryGetProperty("RestomenumExt", out var iext))
                        {
                            catId = StrOrNull(iext, "CategoryId");
                            lineId = StrOrNull(iext, "LineId");
                        }
                        items.Add(new SaleLine(
                            ItemId: it.GetProperty("ItemID").GetInt32(),
                            ProductCode: StrOr(it, "ProductCode", ""),
                            ProductLabel: StrOr(it, "ProductLabel", ""),
                            Quantity: it.GetProperty("Quantity").GetInt32(),
                            // ItemAmount OTORİTE — birim fiyattan HESAPLANMAZ.
                            ItemAmountMinor: Money.ToMinor(it.GetProperty("ItemAmount").GetDecimal()),
                            TaxCode: StrOr(it, "TaxCode", ""),
                            CategoryId: catId,
                            LineId: lineId));
                    }
                }

                var detail = new PaymentDetail(
                    PaymentId: StrOr(ext, "PaymentId", ""),
                    SaleReferenceId: data.TryGetProperty("SaleData", out var sd) ? StrOr(sd, "SaleReferenceID", "") : "",
                    Currency: StrOr(amounts, "Currency", ""),
                    RequestedAmountMinor: Money.ToMinor(amounts.GetProperty("RequestedAmount").GetDecimal()),
                    SaleTotalAmountMinor: ext.TryGetProperty("SaleTotalAmount", out var st) ? Money.ToMinor(st.GetDecimal()) : 0,
                    Market: StrOr(ext, "Market", ""),
                    State: StrOr(ext, "State", ""),
                    ExpiresAtMs: ext.TryGetProperty("ExpiresAt", out var exp) ? exp.GetInt64() : 0,
                    ItemsScope: StrOr(ext, "ItemsScope", ""),
                    Items: items);

                return new PaymentDetailResult.Ok(detail);
            }
            catch (Exception e)
            {
                // 200 ama şekil beklenmedik: sürme, Unknown olarak reddet.
                return new PaymentDetailResult.Rejected(PaymentRejectReason.Unknown,
                    $"detay ayrıştırılamadı: {e.GetType().Name}: {e.Message}", statusCode);
            }
        }
    }

    // Mesaj ÖNCE (kesin), yoksa duruma göre. 404 = bilinmeyen VE sahiplik reddi (ayrım yok).
    private static PaymentRejectReason ReasonOf(string message, int status) => message switch
    {
        "plugin.connector.unauthorized" => PaymentRejectReason.Unauthorized,
        "plugin.payment.notFound" => PaymentRejectReason.NotFound,
        "plugin.payment.expired" => PaymentRejectReason.Expired,
        "plugin.payment.notActionable" => PaymentRejectReason.NotActionable,
        "plugin.payment.amountWindowClosed" => PaymentRejectReason.AmountWindowClosed,
        "plugin.payment.saleItemsUnavailable" => PaymentRejectReason.SaleItemsUnavailable,
        "plugin.rateLimited" => PaymentRejectReason.RateLimited,
        _ => status switch
        {
            401 => PaymentRejectReason.Unauthorized,
            404 => PaymentRejectReason.NotFound,
            429 => PaymentRejectReason.RateLimited,
            _ => PaymentRejectReason.Unknown,
        },
    };

    private static string StrOr(JsonElement e, string prop, string def) =>
        e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : v.GetRawText())
            : def;

    private static string? StrOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Kisalt(string s) => s.Length <= 200 ? s : s[..200];
}
