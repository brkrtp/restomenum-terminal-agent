using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Restomenum.Agent.Core;

/// <summary>
/// <c>MessageCategory: "Reversal"</c> zarfının ayrıştırılması.
///
/// <para><b>Neden ödeme ayrıştırıcısından ayrı:</b> ödeme yolu paranın geçtiği yol ve bugün
/// çalışıyor. İptali oraya eklemek, tek bir <c>if</c> hatasının satışı da bozması demekti.
/// Ayrı ayrıştırıcı + zarf başında kategori bakışı, iki yolu birbirinden yalıtır.</para>
///
/// <para><b>İleri-uyum (§6.6):</b> bilinmeyen alanlar sessizce yok sayılır.</para>
/// </summary>
public static partial class ReversalRequestParser
{
    /// <summary>
    /// Zarfın <c>MessageCategory</c>'sini okur — hangi ayrıştırıcıya gideceğini seçmek için.
    /// Bozuk/kategorisiz gövdede <c>null</c>; hata üretmez, kararı çağırana bırakır.
    /// </summary>
    public static string? PeekCategory(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("SaleToPOIRequest", out var env)
                && env.ValueKind == JsonValueKind.Object
                && env.TryGetProperty("MessageHeader", out var hdr)
                && hdr.ValueKind == JsonValueKind.Object
                && hdr.TryGetProperty("MessageCategory", out var c)
                && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null;
        }
        catch (JsonException) { return null; }
    }

    public static ReversalParseResult Parse(string body)
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

            if (Str(hdr, "MessageCategory") != "Reversal")
                return Red(SaleToPoiRejectReason.UnsupportedCategory, "kategori Reversal değil");
            if (Str(hdr, "MessageClass") != "Service" || Str(hdr, "MessageType") != "Request")
                return Red(SaleToPoiRejectReason.Malformed, "MessageClass/MessageType beklenmedik");

            var serviceId = Str(hdr, "ServiceID");
            if (string.IsNullOrEmpty(serviceId) || serviceId.Length > 10)
                return Red(SaleToPoiRejectReason.InvalidServiceId, "ServiceID boş ya da >10 karakter");

            var poiId = Str(hdr, "POIID");
            if (string.IsNullOrEmpty(poiId))
                return Red(SaleToPoiRejectReason.Malformed, "POIID yok");

            var saleId = Str(hdr, "SaleID") ?? "";
            if (saleId.Length > 32)
                return Red(SaleToPoiRejectReason.Malformed, "SaleID >32 karakter");

            if (!env.TryGetProperty("ReversalRequest", out var rr) || rr.ValueKind != JsonValueKind.Object)
                return Red(SaleToPoiRejectReason.Malformed, "ReversalRequest yok");

            // Referans 1 — tamamlanmış ödeme.
            string? poiTxId = null;
            if (rr.TryGetProperty("OriginalPOITransaction", out var opt) && opt.ValueKind == JsonValueKind.Object)
                poiTxId = Str(opt, "POITransactionID");

            // Referans 2 — açık fiş. `MessageReference` nexo'da hem zarf gövdesinde hem başlıkta
            // görülebiliyor; ALICI OLARAK GENİŞ davranıyoruz (ikisine de bakarız), ÜRETİCİ olarak
            // değil. Kasanın hangisini gönderdiğine bağlı bir ret, sahada teşhisi zor bir arıza olurdu.
            string? origServiceId = null;
            if (rr.TryGetProperty("MessageReference", out var mr) && mr.ValueKind == JsonValueKind.Object)
                origServiceId = Str(mr, "ServiceID");
            if (origServiceId is null
                && hdr.TryGetProperty("MessageReference", out var mrh) && mrh.ValueKind == JsonValueKind.Object)
                origServiceId = Str(mrh, "ServiceID");

            // FAIL-CLOSED: referanssız iptal, "cihazda ne varsa iptal et" demektir. Yanlış fişi
            // iptal etmek geri alınamaz; referans yoksa reddedilir.
            if (string.IsNullOrEmpty(poiTxId) && string.IsNullOrEmpty(origServiceId))
                return Red(SaleToPoiRejectReason.Malformed,
                    "referans yok: OriginalPOITransaction.POITransactionID ya da MessageReference.ServiceID gerekli");

            if (!rr.TryGetProperty("SaleData", out var sd) || sd.ValueKind != JsonValueKind.Object)
                return Red(SaleToPoiRejectReason.Malformed, "SaleData yok");
            if (!sd.TryGetProperty("SaleTransactionID", out var stx) || stx.ValueKind != JsonValueKind.Object)
                return Red(SaleToPoiRejectReason.Malformed, "SaleTransactionID yok");

            var paymentId = Str(stx, "TransactionID");
            if (paymentId is null || !PaymentIdRegex().IsMatch(paymentId))
                return Red(SaleToPoiRejectReason.InvalidPaymentId, "TransactionID pay_+40hex değil");

            // TimeStamp: iptalde ZORUNLU DEĞİL (kasa göndermeyebiliyor); yoksa "şimdi" sayılır.
            // Ödemede zorunlu, çünkü orada süre penceresi kararı var; iptalde öyle bir karar yok.
            var tsRaw = Str(stx, "TimeStamp");
            var ts = tsRaw is not null
                && DateTimeOffset.TryParse(tsRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var p)
                    ? p : DateTimeOffset.UtcNow;

            return new ReversalParseResult.Ok(new ReversalRequest(
                ServiceId: serviceId, SaleId: saleId, PoiId: poiId, PaymentId: paymentId,
                OriginalPoiTransactionId: string.IsNullOrEmpty(poiTxId) ? null : poiTxId,
                OriginalServiceId: string.IsNullOrEmpty(origServiceId) ? null : origServiceId,
                ReversalReason: Str(rr, "ReversalReason"),
                TimeStamp: ts));
        }
    }

    private static ReversalParseResult Red(SaleToPoiRejectReason r, string detail) =>
        new ReversalParseResult.Invalid(r, detail);

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex("^pay_[0-9a-fA-F]{40}$")]
    private static partial Regex PaymentIdRegex();
}
