using System.Text.Json;
using System.Text.Json.Nodes;

namespace Restomenum.Agent.Core;

/// <summary>
/// Kasadan gelen <b>SALT-OKUNUR fiş durumu sorgusu</b> (<c>MessageCategory: "TicketQuery"</c>).
///
/// <para><b>Neden var (2026-09-10, kasa ölçtü):</b> W104 yarım kalmış fişi kapattıktan sonra
/// masa kapandı, ama köprünün KENDİ kaydı bayat kaldı (<c>ticketState "open"</c>,
/// <c>approvedOpenCount 1</c>). Sonuç: kasiyerin karşısına <b>YANLIŞ</b> bir uyarı çıktı —
/// <i>"Cihazda kapanmamış fiş var; bu kasadan alınan 1 ödeme henüz deftere işlenmedi."</i> —
/// oysa fiş kapanmış, ödeme deftere işlenmişti (990/990, <c>COMPLETED</c>). "İptal"e basan bir
/// kasiyer masayı boş yere açık bırakırdı.</para>
///
/// <para><b>Neden CİHAZA GİTMEZ — ve bu bir tercih değil:</b> "cihazı oku" görünüşte doğru cevap
/// ama <c>FP3_Start</c> açık fiş YOKKEN <b>YENİ FİŞ AÇAR</b>; kapatılsa bile mali numarayı
/// tüketir. Her masa kapanışında bir sorgu, her sorguda bir mali numara demekti. Üstelik cihaza
/// gitmek terminal kilidini alır ve satışı geciktirir. Bu yüzden cevap TAMAMEN DEFTERDEN
/// veriliyor: cihaz I/O yok, kilit yok, yan etki yok, anında.</para>
///
/// <para><b>Defter bu soruya cevap vermeye YETKİLİDİR:</b> "fiş kapandı mı" ve "ödeme deftere
/// işlendi mi" sorularının kaynağı zaten burası — kapanış kaydını yazan da, bildirimi outbox'a
/// devreden de bu defter.</para>
/// </summary>
/// <param name="TicketId">
/// Sorulan fiş. <c>null</c> = "bu terminalde ŞU AN açık fiş var mı" sorusu.
/// Dolu = "BU fişe ne oldu" sorusu — kasa bayat kaydını bununla tazeler.
/// </param>
public sealed record TicketQueryRequest(
    string ServiceId, string SaleId, string PoiId, string? TicketId, DateTimeOffset TimeStamp);

/// <summary>
/// Defterin fiş hakkında bildiği her şey. <b>Her alan "bilmiyorum"u ayrı taşır</b> — bu katmanda
/// boş ile bilinmeyen karıştırılırsa kasa yanlış uyarı üretir, ki zaten olan tam olarak buydu.
/// </summary>
/// <param name="Durum">
/// <c>"OPEN"</c> (defterde açık bağ), <c>"CLOSED"</c> (kapanış kaydı var),
/// <c>"UNKNOWN"</c> (defterde iz yok — kapanıp saklama süresi dolmuş ya da bu ajanın hiç
/// görmediği bir fiş olabilir). <b>UNKNOWN "açık" DEĞİLDİR</b> ve öyle yorumlanmamalı.
/// </param>
/// <param name="LedgerWritten">
/// Kapanış bildirimi outbox'a DEVREDİLDİ mi (<c>notified_at</c> dolu). Kasanın "ödeme deftere
/// işlendi mi" sorusunun tek doğru cevabı bu. <c>null</c> = fiş kapanmamış ya da bilinmiyor.
/// </param>
/// <param name="PendingFinishAttempts">
/// Fiş baskı hatası yüzünden TAMAMLANMAYI BEKLİYORSA kaçıncı denemede. <c>null</c> = beklemiyor.
/// Kasa buradan "arıza sürüyor ama üzerinde çalışılıyor" diyebilir — sessiz bir bekleme değil.
/// </param>
public sealed record TicketQueryResult(
    string Durum, string? TicketId, string? SaleSessionId,
    long? TotalMinor, long? PaidMinor, bool? LedgerWritten,
    int? PendingFinishAttempts, string? PendingFinishStep, string? PendingFinishCode);

/// <summary><c>MessageCategory: "TicketQuery"</c> zarfının ayrıştırılması.</summary>
public static class TicketQueryParser
{
    public const string Category = "TicketQuery";

    public static TicketQueryRequest? Parse(string body)
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

            // `ticketId` isteğe bağlı ve `Restomenum` uzantısında taşınır — zarfın nexo alanlarına
            // bizim kimliğimizi sokmuyoruz (uzantı alanları için ayrılmış yer orası).
            string? ticketId = null;
            if (env.TryGetProperty("Restomenum", out var ek) && ek.ValueKind == JsonValueKind.Object)
                ticketId = Str(ek, "ticketId");

            return new TicketQueryRequest(serviceId!, Str(hdr, "SaleID") ?? "", poiId!, ticketId,
                DateTimeOffset.UtcNow);
        }
    }

    private static string? Str(JsonElement o, string ad) =>
        o.TryGetProperty(ad, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Sorguyu DEFTERDEN cevaplar. <b>Cihaza dokunmaz, kilit almaz.</b></summary>
public static class TicketQueryHandler
{
    public static TicketQueryResult Cevapla(CommandStore defter, TicketQueryRequest istek)
    {
        // ── 1. BU TERMİNALDE AÇIK BAĞ VAR MI ──────────────────────────────────────
        var acikFis = defter.ReadOpenTicketId(istek.PoiId);
        var acikOturum = defter.ReadOpenTicketBinding(istek.PoiId);

        // Fiş belirtilmediyse soru "şu an açık fiş var mı".
        var hedef = istek.TicketId ?? acikFis;
        if (hedef is null)
            return new TicketQueryResult("UNKNOWN", null, null, null, null, null, null, null, null);

        var bekleyen = defter.PendingFinishes(long.MaxValue).FirstOrDefault(x => x.TicketId == hedef);
        int? denemeler = bekleyen.TicketId == hedef ? bekleyen.Attempts : null;

        // ── 2. AÇIK MI ────────────────────────────────────────────────────────────
        if (hedef == acikFis)
            return new TicketQueryResult("OPEN", hedef, acikOturum, null, null, null,
                denemeler, bekleyen.LastStep, bekleyen.LastCode);

        // ── 3. KAPANDI MI ─────────────────────────────────────────────────────────
        var kapanis = defter.ReadClosedTicket(hedef);
        if (kapanis is { } k)
            return new TicketQueryResult("CLOSED", hedef, k.Row.SaleSessionId,
                k.Row.TotalMinor, k.Row.PaidMinor, k.Notified,
                denemeler, bekleyen.LastStep, bekleyen.LastCode);

        // ── 4. DEFTERDE İZ YOK ────────────────────────────────────────────────────
        // ⚠️ Bu "AÇIK" DEĞİLDİR. Kapanmış ve saklama süresi (7 gün) dolmuş olabilir, ya da bu
        // ajanın hiç görmediği bir fiş olabilir. "Bilmiyorum" demek, yanlış bir kesinlik
        // yaymaktan iyidir — kasanın yanlış uyarısı tam olarak öyle bir kesinlikten doğdu.
        return new TicketQueryResult("UNKNOWN", hedef, null, null, null, null,
            denemeler, bekleyen.LastStep, bekleyen.LastCode);
    }

    /// <summary>Yanıt gövdesi — <c>SaleToPOIResponse</c> zarfı, veriler <c>Restomenum</c> altında.</summary>
    public static string Govde(TicketQueryRequest istek, TicketQueryResult sonuc, DateTimeOffset now)
    {
        var ek = new JsonObject { ["ticketState"] = sonuc.Durum };
        // Ölçülmeyen alan KONMAZ. Alanın yokluğu "bilinmiyor" demektir; 0 ya da false koymak
        // kasaya olmayan bir kesinlik satmak olurdu.
        if (sonuc.TicketId is not null) ek["ticketId"] = sonuc.TicketId;
        if (sonuc.SaleSessionId is not null) ek["saleSessionId"] = sonuc.SaleSessionId;
        if (sonuc.TotalMinor is long tp) ek["deviceTicketTotalMinor"] = tp;
        if (sonuc.PaidMinor is long od) ek["devicePaidMinor"] = od;
        if (sonuc.LedgerWritten is bool d) ek["ledgerWritten"] = d;
        if (sonuc.PendingFinishAttempts is int n)
        {
            ek["pendingFinish"] = true;
            ek["pendingFinishAttempts"] = n;
            if (sonuc.PendingFinishStep is not null) ek["pendingFinishStep"] = sonuc.PendingFinishStep;
            if (sonuc.PendingFinishCode is not null) ek["pendingFinishCode"] = sonuc.PendingFinishCode;
        }

        return new JsonObject
        {
            ["SaleToPOIResponse"] = new JsonObject
            {
                ["MessageHeader"] = new JsonObject
                {
                    ["ProtocolVersion"] = "3.0",
                    ["MessageClass"] = "Service",
                    ["MessageCategory"] = TicketQueryParser.Category,
                    ["MessageType"] = "Response",
                    ["ServiceID"] = istek.ServiceId,
                    ["SaleID"] = istek.SaleId,
                    ["POIID"] = istek.PoiId,
                },
                // Sorgu hiçbir şey DENEMEZ; başarısı "cevap verebildim"dir. Cihaz durumu kötü
                // olsa bile bu çağrı başarılıdır — durum `ticketState`'te taşınır.
                ["Response"] = new JsonObject
                {
                    ["Result"] = "Success",
                    ["ErrorCondition"] = null,
                },
                ["Restomenum"] = ek,
            },
        }.ToJsonString();
    }
}
