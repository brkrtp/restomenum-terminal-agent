using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <b>W106 — SALT-OKUNUR fiş durumu sorgusu.</b>
///
/// <para>Var olma sebebi (2026-09-10, kasa ölçtü): yarım kalmış fiş kapatıldıktan ve masa
/// kapandıktan SONRA köprünün kendi kaydı bayat kaldı ve kasiyerin karşısına <b>yanlış</b> bir
/// uyarı çıktı: <i>"Cihazda kapanmamış fiş var; bu kasadan alınan 1 ödeme henüz deftere
/// işlenmedi."</i> Fiş kapanmıştı, ödeme deftere işlenmişti. "İptal"e basan bir kasiyer masayı
/// boş yere açık bırakırdı.</para>
/// </summary>
public class TicketQueryTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"tq_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;

    public TicketQueryTests() => _store = CommandStore.Open(_db);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    private static TicketQueryRequest Istek(string? fis = null) =>
        new("svc12345", "kasa-1", "t1", fis, DateTimeOffset.UtcNow);

    private static string Zarf(string? fis) =>
        fis is null
            ? """{"SaleToPOIRequest":{"MessageHeader":{"ProtocolVersion":"3.0","MessageClass":"Service","MessageCategory":"TicketQuery","MessageType":"Request","ServiceID":"svc12345","SaleID":"kasa-1","POIID":"t1"}}}"""
            : Zarf(null).Replace("\"POIID\":\"t1\"}",
                "\"POIID\":\"t1\"},\"Restomenum\":{\"ticketId\":\"" + fis + "\"}");

    // ── AYRIŞTIRMA ──────────────────────────────────────────────────────────────

    [Fact]
    public void Zarf_ayristirilir_ticketId_istege_bagli()
    {
        var bos = TicketQueryParser.Parse(Zarf(null));
        Assert.NotNull(bos);
        Assert.Equal("svc12345", bos!.ServiceId);
        Assert.Equal("t1", bos.PoiId);
        Assert.Null(bos.TicketId);   // "şu an açık fiş var mı" sorusu

        var dolu = TicketQueryParser.Parse(Zarf("tkt_1"));
        Assert.Equal("tkt_1", dolu!.TicketId);
    }

    [Fact]
    public void Baska_kategori_bu_ayristiriciya_AIT_DEGIL()
    {
        // ← ÇİVİ: kategori bakışı yolu seçiyor. Bu ayrıştırıcı bir Reversal'ı kabul etseydi,
        // iptal isteği sessizce SORGUYA dönüşür ve kasiyerin iptali HİÇ yapılmazdı.
        var iptal = Zarf(null).Replace("TicketQuery", "Reversal");
        Assert.Null(TicketQueryParser.Parse(iptal));
    }

    [Fact]
    public void Bozuk_govde_HATA_ATMAZ_null_doner()
    {
        Assert.Null(TicketQueryParser.Parse("{bozuk"));
        Assert.Null(TicketQueryParser.Parse("{}"));
        // POIID olmadan hangi terminalin sorulduğu bilinemez — tahmin edilmez.
        Assert.Null(TicketQueryParser.Parse(Zarf(null).Replace("\"POIID\":\"t1\"", "\"POIID\":\"\"")));
    }

    // ── CEVAP: DEFTERDEN ────────────────────────────────────────────────────────

    [Fact]
    public void Acik_bag_varsa_OPEN_doner()
    {
        var fis = _store.BindOpenTicket("t1", "oturum-A");

        var s = TicketQueryHandler.Cevapla(_store, Istek());

        Assert.Equal("OPEN", s.Durum);
        Assert.Equal(fis, s.TicketId);
        Assert.Equal("oturum-A", s.SaleSessionId);
    }

    [Fact]
    public void KAPANMIS_ve_BILDIRILMIS_fis_kasanin_yanlis_uyarisini_susturur()
    {
        // 2026-09-10'un birebir hâli: fiş kapandı, 990/990, bildirim outbox'a devredildi.
        _store.MarkTicketClosed("tkt_1", "t1", "oturum-A", 990, 990);
        _store.ConfirmTicketClosed("tkt_1");

        var s = TicketQueryHandler.Cevapla(_store, Istek("tkt_1"));

        Assert.Equal("CLOSED", s.Durum);
        // ← ÇİVİ: kasanın "ödeme deftere işlendi mi" sorusunun TEK doğru cevabı bu alan.
        // Olmasaydı kasa yine çıkarım yapardı — yanlış uyarı tam olarak bir çıkarımdı.
        Assert.True(s.LedgerWritten);
        Assert.Equal(990, s.PaidMinor);
    }

    [Fact]
    public void Kapandi_ama_HENUZ_bildirilmedi_AYRI_bir_cevaptir()
    {
        _store.MarkTicketClosed("tkt_1", "t1", "oturum-A", 990, 990);

        var s = TicketQueryHandler.Cevapla(_store, Istek("tkt_1"));

        // ← ÇİVİ: "kapandı" ile "deftere işlendi" AYRI olgular. Tek alana katlasaydık kasa
        // kapanmış ama bildirilmemiş bir fişi işlenmiş sanardı — gerçek bir kayıp penceresi.
        Assert.Equal("CLOSED", s.Durum);
        Assert.False(s.LedgerWritten);
    }

    [Fact]
    public void Defterde_iz_YOKSA_UNKNOWN_doner_ACIK_DEMEZ()
    {
        var s = TicketQueryHandler.Cevapla(_store, Istek("tkt_bilinmeyen"));

        // ← ÇİVİ: iz yokluğu "açık" DEĞİLDİR. Kapanıp saklama süresi dolmuş olabilir ya da bu
        // ajanın hiç görmediği bir fiş olabilir. "Açık" demek, kasiyere olmayan bir fiş
        // gösterip masayı kilitlemek olurdu.
        Assert.Equal("UNKNOWN", s.Durum);
        Assert.Null(s.LedgerWritten);
    }

    [Fact]
    public void Tamamlanmayi_BEKLEYEN_fis_denemesiyle_birlikte_bildirilir()
    {
        var fis = _store.BindOpenTicket("t1", "oturum-A");
        _store.MarkTicketNeedsFinish(fis, "t1", "oturum-A", "PrintBeforeMF", "0xF003");
        _store.MarkFinishAttempt(fis, "PrintBeforeMF", "0xF003");

        var s = TicketQueryHandler.Cevapla(_store, Istek());

        // ← ÇİVİ: kasa "arıza sürüyor ama üzerinde ÇALIŞILIYOR" diyebilmeli. Bunu göremezse
        // ya sessizce bekler ya da kasiyeri gereksiz yere paniğe sokar.
        Assert.Equal(1, s.PendingFinishAttempts);
        Assert.Equal("PrintBeforeMF", s.PendingFinishStep);
        Assert.Equal("0xF003", s.PendingFinishCode);
    }

    // ── GÖVDE ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Govde_olculmeyen_alani_KOYMAZ()
    {
        var s = new TicketQueryResult("UNKNOWN", null, null, null, null, null, null, null, null);
        var ek = JsonDocument.Parse(TicketQueryHandler.Govde(Istek(), s, DateTimeOffset.UtcNow))
            .RootElement.GetProperty("SaleToPOIResponse").GetProperty("Restomenum");

        Assert.Equal("UNKNOWN", ek.GetProperty("ticketState").GetString());
        // ← ÇİVİ: alanın YOKLUĞU "bilinmiyor" demektir. `false`/`0` koymak kasaya olmayan bir
        // kesinlik satmak olurdu ve bu tam da düzeltmeye çalıştığımız hata.
        Assert.False(ek.TryGetProperty("ledgerWritten", out _));
        Assert.False(ek.TryGetProperty("pendingFinish", out _));
        Assert.False(ek.TryGetProperty("devicePaidMinor", out _));
    }

    [Fact]
    public void Govde_sorgu_basarili_sayilir_fis_durumu_ne_olursa_olsun()
    {
        var s = new TicketQueryResult("OPEN", "tkt_1", "oturum-A", null, null, null, 3, "PrintMF", "0xF003");
        var yanit = JsonDocument.Parse(TicketQueryHandler.Govde(Istek(), s, DateTimeOffset.UtcNow))
            .RootElement.GetProperty("SaleToPOIResponse");

        // ← ÇİVİ: sorgu hiçbir şey DENEMİYOR; başarısı "cevap verebildim"dir. Cihaz durumunu
        // `Result:"Failure"`a çevirmek, sorgunun kendisi başarısızmış gibi görünürdü.
        Assert.Equal("Success", yanit.GetProperty("Response").GetProperty("Result").GetString());
        Assert.Equal("TicketQuery", yanit.GetProperty("MessageHeader").GetProperty("MessageCategory").GetString());
        Assert.Equal("svc12345", yanit.GetProperty("MessageHeader").GetProperty("ServiceID").GetString());

        var ek = yanit.GetProperty("Restomenum");
        Assert.Equal("OPEN", ek.GetProperty("ticketState").GetString());
        Assert.True(ek.GetProperty("pendingFinish").GetBoolean());
        Assert.Equal(3, ek.GetProperty("pendingFinishAttempts").GetInt32());
    }
}
