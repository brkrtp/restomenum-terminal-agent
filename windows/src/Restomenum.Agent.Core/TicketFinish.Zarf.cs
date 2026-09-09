using System.Text.Json;
using System.Text.Json.Nodes;

namespace Restomenum.Agent.Core;

/// <summary>
/// Kasadan gelen <b>ELLE FİŞ TAMAMLAMA</b> isteği (<c>MessageCategory: "TicketFinish"</c>).
///
/// <para><b>Neden var:</b> otomatik süpürme (W105) sekiz denemeden sonra susuyor ve insanı
/// bekliyor — kâğıt gerçekten bittiyse cihazı süresiz meşgul etmek kasiyeri de kilitlerdi
/// (W108). Kâğıdı değiştiren kişi <b>doğru anı bilir</b>, süpürme bilmez. Bu zarf o kişiye
/// tetiği veriyor; bugüne kadar tek yol ajanı durdurup <c>--finish-ticket</c> çalıştırmaktı.</para>
///
/// <para><b><c>VoidAll</c>/<c>VoidPayment</c> bu yolda YOK.</b> Kararı
/// <see cref="TicketFinish.Planla"/> veriyor; ödemesi tamamlanmamış fişe dokunulmaz.</para>
/// </summary>
/// <param name="TicketFinishId">
/// <b>Tekrar anahtarı.</b> Kasa ağ hatasında aynı zarfı yeniden POST edebilir; ikinci istek
/// cihaza GİTMEZ, ilkinin sonucu replay edilir.
///
/// <para><b>Neden ZORUNLU:</b> anahtarsız bir tamamlama isteğinin ikinci kez gelmesi, YENİ bir
/// fişin baskısını yarıda kesebilir ya da zaten kapanmış bir fiş için cihaza boş yere gidip
/// yoklama fişi açtırırdı (<c>FP3_Start</c> mali numara tüketir).</para>
/// </param>
public sealed record TicketFinishRequest(
    string ServiceId, string SaleId, string PoiId, string TicketFinishId, DateTimeOffset TimeStamp);

/// <summary><c>MessageCategory: "TicketFinish"</c> zarfının ayrıştırılması.</summary>
public static class TicketFinishParser
{
    public const string Category = "TicketFinish";

    public static TicketFinishRequest? Parse(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("SaleToPOIRequest", out var env) || env.ValueKind != JsonValueKind.Object
                || !env.TryGetProperty("MessageHeader", out var hdr) || hdr.ValueKind != JsonValueKind.Object)
                return null;

            if (Str(hdr, "MessageCategory") != Category) return null;

            var serviceId = Str(hdr, "ServiceID");
            var poiId = Str(hdr, "POIID");
            if (string.IsNullOrEmpty(serviceId) || string.IsNullOrEmpty(poiId)) return null;

            // Tekrar anahtarı ZORUNLU. Yoksa istek REDDEDİLİR — "anahtarsız da çalıştıralım"
            // demek, tekrar gelen bir isteğin cihaza ikinci kez gitmesine izin vermektir.
            string? finishId = null;
            if (env.TryGetProperty("Restomenum", out var ek) && ek.ValueKind == JsonValueKind.Object)
                finishId = Str(ek, "ticketFinishId");
            if (string.IsNullOrWhiteSpace(finishId) || finishId!.Length > 64) return null;

            return new TicketFinishRequest(serviceId!, Str(hdr, "SaleID") ?? "", poiId!, finishId,
                DateTimeOffset.UtcNow);
        }
    }

    private static string? Str(JsonElement o, string ad) =>
        o.TryGetProperty(ad, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Yanıt gövdesi. <b>Ham <c>TransactionFlags</c> her zaman taşınır</b> — kasa onu yorumlamaz
    /// ama sözleşmeye/olay kaydına geçirir; bu alan cihaz izinde GÖRÜNMÜYOR, yalnız yanıtta geliyor.
    /// </summary>
    public static string Govde(TicketFinishRequest istek, StuckTicketResult s, string? not = null)
    {
        var ek = new JsonObject
        {
            ["ticketFinishId"] = istek.TicketFinishId,
            ["ticketState"] = s.Kapandi ? "CLOSED" : s.FisAcikDegil ? "NOT_OPEN" : "OPEN",
            // Ham, yorumlanmadan. Onaltılık: 6+ ardışık rakam maskesine takılmasın.
            ["transactionFlags"] = $"0x{s.Bayraklar:X8}",
            ["recoveryStep"] = s.Plan.UreticiAdimi,
        };
        if (s.TicketId is not null) ek["ticketId"] = s.TicketId;
        if (s.ToplamMinor is long tp) ek["deviceTicketTotalMinor"] = tp;
        if (s.TahsilMinor is long od) ek["devicePaidMinor"] = od;
        if (s.BasarisizAdim is not null) ek["printFailedStep"] = s.BasarisizAdim;
        if (s.Kod is not null) ek["printFailedCode"] = s.Kod;
        if (not is not null) ek["reason"] = not;

        // ⚠️ Fiş KAPANMADIYSA sonuç `Failure`: kasiyer "tamam" görüp masayı kapatmamalı.
        // Ama fiş zaten AÇIK DEĞİLSE bu bir başarısızlık DEĞİL — yapılacak iş yoktu.
        var basarili = s.Kapandi || s.FisAcikDegil;
        var response = new JsonObject
        {
            ["Result"] = basarili ? "Success" : "Failure",
            ["ErrorCondition"] = basarili ? null : "PaymentRestriction",
        };

        return new JsonObject
        {
            ["SaleToPOIResponse"] = new JsonObject
            {
                ["MessageHeader"] = new JsonObject
                {
                    ["ProtocolVersion"] = "3.0",
                    ["MessageClass"] = "Service",
                    ["MessageCategory"] = Category,
                    ["MessageType"] = "Response",
                    ["ServiceID"] = istek.ServiceId,
                    ["SaleID"] = istek.SaleId,
                    ["POIID"] = istek.PoiId,
                },
                ["Response"] = response,
                ["Restomenum"] = ek,
            },
        }.ToJsonString();
    }
}
