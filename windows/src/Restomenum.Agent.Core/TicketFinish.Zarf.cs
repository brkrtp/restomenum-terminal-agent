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
    string ServiceId, string SaleId, string PoiId, string TicketFinishId, DateTimeOffset TimeStamp,

    /// <param name="SaleSessionId">
    /// <b>W138 — SAHİPLİK KANITI.</b> İsteğin ait olduğu satış oturumu. <c>null</c> = kasa
    /// göndermedi (eski sürüm ya da hesap kimliği yok).
    ///
    /// <para><b>Neden gerekli:</b> tamamlama, cihazdaki AÇIK FİŞİ tamamlar — hangi fiş olduğuna
    /// bakmadan. Kasanın tetiği kendi <c>open</c> kaydı ve o kayıt bayatlayabiliyor. S1
    /// kapatılırken cihazda S2'nin fişi açıksa, eski davranış <b>S2'nin fişini mali olarak
    /// kesinleştirir</b> ve S2'nin kasiyeri sebebini hiç öğrenmez. Mali kesinleşme geri
    /// alınamaz.</para>
    ///
    /// <para>⚠️ <b>YOKLUĞU SUÇLAMA DEĞİLDİR.</b> Alan gelmediğinde engellemeyiz: "alan gelmedi"
    /// ile "başka hesabın" ayrı şeylerdir ve yokluğu suçlama saymak, eski kasa sürümünde yarım
    /// mali belgeyi yarım bırakırdı. <see cref="TicketQueryResult.PendingFinishAttempts"/>
    /// yokluğuyla ve <c>paymentInvoked</c> üç durumluluğuyla aynı ilke.</para>
    /// </param>
    string? SaleSessionId = null);

/// <summary><c>MessageCategory: "TicketFinish"</c> zarfının ayrıştırılması.</summary>
public static class TicketFinishParser
{
    /// <inheritdoc cref="TekrarAnahtari.EnCok"/>
    public const int AnahtarEnCok = TekrarAnahtari.EnCok;

    /// <summary>
    /// <b>W129 (tarihçe) — tekrar anahtarının en fazla uzunluğu.</b> 64'tü ve <b>gerçek bir anahtarı
    /// kesiyordu</b>: kasanın deterministik şekli <c>&lt;uuid&gt;-&lt;terminalId&gt;-ticketfinish</c>
    /// = 36+1+16+13 = <b>66</b> karakter (2026-09-10 18:32'de ölçüldü, istek reddedildi).
    ///
    /// <para>64 benim keyfi seçimimdi; anahtarın kendisi iyi tasarlanmıştı. Sınır, saldırıyı
    /// değil taşmayı engellemek için var — 128 aynı işi görüyor ve makul anahtarları kesmiyor.</para>
    /// </summary>

    public const string Category = "TicketFinish";

    public static TicketFinishRequest? Parse(string body, out string? neden)
    {
        neden = null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("SaleToPOIRequest", out var env) || env.ValueKind != JsonValueKind.Object
                || !env.TryGetProperty("MessageHeader", out var hdr) || hdr.ValueKind != JsonValueKind.Object)
            { neden = "SaleToPOIRequest/MessageHeader yok ya da nesne değil"; return null; }

            if (Str(hdr, "MessageCategory") != Category)
            { neden = $"MessageCategory '{Str(hdr, "MessageCategory")}' — '{Category}' bekleniyordu"; return null; }

            var serviceId = Str(hdr, "ServiceID");
            var poiId = Str(hdr, "POIID");
            if (string.IsNullOrEmpty(serviceId) || string.IsNullOrEmpty(poiId))
            { neden = "ServiceID ya da POIID boş"; return null; }

            // Tekrar anahtarı ZORUNLU. Yoksa istek REDDEDİLİR — "anahtarsız da çalıştıralım"
            // demek, tekrar gelen bir isteğin cihaza ikinci kez gitmesine izin vermektir.
            string? finishId = null;
            if (env.TryGetProperty("Restomenum", out var ek) && ek.ValueKind == JsonValueKind.Object)
                finishId = Str(ek, "ticketFinishId");

            // ══ W129 — RED SEBEBİ AYRIŞIR ═══════════════════════════════════════════
            //
            // 2026-09-10 18:32:59'da kasa geçerli bir `TicketFinish` gönderdi ve reddettim;
            // log yalnız "ticketFinishId zorunlu" dedi. Alan YOK muydu, YANLIŞ YERDE miydi,
            // yoksa ÇOK MU UZUNDU — log'dan çıkarılamıyordu. Karşı taraf kendi tarafında
            // hata aradı, oysa kusur bendeydi.
            //
            // Aynı sınıf hata W128'de de vardı (iptalin cihaz kodu hiç loglanmıyordu).
            // Teşhis edilemeyen bir red, karşı tarafı kör arama yaptırır.
            if (TekrarAnahtari.Dogrula(finishId, "ticketFinishId") is { } red) { neden = red; return null; }
            // Doğrulama geçtiyse anahtar dolu — derleyiciye söylemek gerekiyor.

            // W138: sahiplik kanıtı — İSTEĞE BAĞLI. Boş/beyaz ise YOK sayılır.
            var oturum = env.TryGetProperty("Restomenum", out var ek2) && ek2.ValueKind == JsonValueKind.Object
                ? Str(ek2, "saleSessionId") : null;
            if (string.IsNullOrWhiteSpace(oturum)) oturum = null;

            return new TicketFinishRequest(serviceId!, Str(hdr, "SaleID") ?? "", poiId!, finishId!,
                DateTimeOffset.UtcNow, oturum);
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
        // W148: kurtarmanın kapattığı fişin mali numarası. Okunamadıysa alan HİÇ yazılmaz —
        // "yok" ile "0" ayrı şeyler ve 0 gerçek bir fiş numarası iddiası olurdu.
        if (s.FiscalReceipt is { } mf)
        {
            // W151: üçü BİRLİKTE — numara tek başına Z sınırında çakışır.
            ek["fiscalReceiptNo"] = mf.ReceiptNo;
            ek["fiscalZNo"] = mf.ZNo;
            ek["fiscalEkuNo"] = mf.EkuNo;
        }

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
