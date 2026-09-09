using System.Text.Json;
using Microsoft.Data.Sqlite;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W69 — kalıcı reddedilmiş bir fiş iptali bildiriminin yeniden gönderimi.
///
/// <para><b>Neden (sahada, 2026-09-09 14:42):</b> kullanıcı kasadan fiş iptali yaptı, cihaz fişi
/// iptal etti, platform bildirimi <c>400 invalidResult</c> ile reddetti ve ajan kaydı SİLDİ. Kök
/// sebep platformdaydı ve düzeltildi — ama o iptal artık kendiliğinden gidemezdi: gövde hiçbir
/// yerde durmuyordu.</para>
/// </summary>
public class TicketCancelReplayTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rep_{Guid.NewGuid():N}.db");
    private readonly Outbox _outbox;
    private static readonly DateTimeOffset Simdi = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    private const string Oturum = "5d0cbd41-5443-4a88-929c-49a7a950c193";
    private const string Fis = "tkt_036c4ea435574ddb96cf0f839a387251";
    private const string Servis = "d3bf8de4e5";
    private const string Poi = "windows-gmp-test";
    private const string IptalId = Oturum + "-" + Poi + "-ticketcancel-2";

    public TicketCancelReplayTests() => _outbox = Outbox.Open(_db);

    public void Dispose()
    {
        _outbox.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    private TicketCancelReplayPlan? Hazirla(string? iptalId = IptalId) =>
        TicketCancelReplay.Hazirla(_outbox, Oturum, Fis, Servis, iptalId, Poi, Simdi, out _);

    [Fact]
    public void OUTBOXTAKI_orijinal_govde_TERCIH_EDILIR()
    {
        // ← ÇİVİ: yeniden kurulan gövde, orijinalin "en iyi tahmini"dir — orijinalin kendisi değil.
        // Elimizde gerçeği varken tahmini göndermek, kaydı düzeltirken ikinci bir yanlış üretme
        // riskidir. W68'den sonra normal hâl budur.
        var eid = TicketCancelReplay.EventIdFor(IptalId);
        _outbox.Enqueue(eid, "", OutboxKinds.TicketCancel, $$"""{"orijinal":true,"t":"{{Fis}}"}""", "");
        _outbox.MarkPermanentlyRejected(eid);

        var p = Assert.IsType<TicketCancelReplayPlan>(Hazirla());

        Assert.True(p.FromOutbox);
        Assert.Contains("\"orijinal\":true", p.Body);   // ← ÇİVİ: yeniden kurulmadı
        Assert.Equal(eid, p.EventId);
    }

    [Fact]
    public void Kayit_YOKSA_govde_AYNI_URETICIYLE_kurulur()
    {
        // ← ÇİVİ: elle JSON yazmak "tek üretici" kuralını kırardı; gövdeyi üreten tek yer
        // `SaleToPoiResponseBuilder`. Bu test onun çıktısının şeklini sabitliyor.
        var p = Assert.IsType<TicketCancelReplayPlan>(Hazirla());
        Assert.False(p.FromOutbox);

        var kok = JsonDocument.Parse(p.Body).RootElement.GetProperty("SaleToPOIResponse");
        Assert.Equal("Success", kok.GetProperty("ReversalResponse").GetProperty("Response")
            .GetProperty("Result").GetString());
        Assert.Equal(Servis, kok.GetProperty("MessageHeader").GetProperty("ServiceID").GetString());
        Assert.Equal(Poi, kok.GetProperty("MessageHeader").GetProperty("POIID").GetString());

        var ek = kok.GetProperty("Restomenum");
        Assert.Equal(Fis, ek.GetProperty("ticketId").GetString());
        Assert.Equal(Oturum, ek.GetProperty("cancelledSaleSessionId").GetString());
        Assert.Equal(IptalId, ek.GetProperty("ticketCancelId").GetString());
        Assert.Equal(0, ek.GetProperty("voidedPaymentCount").GetInt32());
        Assert.Equal(0, ek.GetProperty("voidedAmountMinor").GetInt32());
        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
        Assert.Equal("TICKET_CANCELLED", ek.GetProperty("reason").GetString());
    }

    [Fact]
    public void ODEMESIZ_iptalde_ReversedAmount_HIC_KONMAZ()
    {
        // ← ÇİVİ: fişte hiç tahsilat yoktu (`delta 0`, üç ayrı ölçümde). Tutar alanı koymak,
        // olmayan bir iadeyi deftere yazdırmak olurdu — ve platformun 400'ü zaten bu sınıfın
        // ne kadar hassas olduğunu gösterdi.
        var p = Assert.IsType<TicketCancelReplayPlan>(Hazirla());
        var yanit = JsonDocument.Parse(p.Body).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("ReversalResponse");

        Assert.False(yanit.TryGetProperty("ReversedAmount", out _));
    }

    [Fact]
    public void ticketCancelId_YOKSA_ve_kayit_YOKSA_REDDEDER_türetmez()
    {
        // ← ÇİVİ: `{oturum}-{poi}-ticketcancel-{n}` deseni GÖZLENDİ ama o kimliği PLATFORM üretiyor.
        // Desenden türetmek onu uydurmaktır; yanlışı 409 `voidNotRequested` ile döner ve iptal yine
        // deftere geçmez. Bilmediğimizi söylemek, tahmin etmekten iyidir.
        var p = TicketCancelReplay.Hazirla(_outbox, Oturum, Fis, Servis,
            ticketCancelId: null, Poi, Simdi, out var hata);

        Assert.Null(p);
        Assert.NotNull(hata);
        Assert.Contains("ticketCancelId", hata);
    }

    [Fact]
    public void ticketCancelId_YOKSA_ama_KAYIT_VARSA_calisir()
    {
        // Kimlik zaten gövdenin içinde; operatörden istemeye gerek yok.
        var eid = TicketCancelReplay.EventIdFor(IptalId);
        _outbox.Enqueue(eid, "", OutboxKinds.TicketCancel, $$"""{"t":"{{Fis}}"}""", "");
        _outbox.MarkPermanentlyRejected(eid);

        var p = Assert.IsType<TicketCancelReplayPlan>(Hazirla(iptalId: null));
        Assert.True(p.FromOutbox);
    }

    [Fact]
    public void DAMGASIZ_kayit_kaynak_SAYILMAZ()
    {
        // ← ÇİVİ: damgasız kayıt hâlâ kuyrukta ve arka plan onu zaten gönderecek. Onu buradan da
        // göndermek çift gönderim olurdu; üstelik "kalıcı reddedilmiş" olmayan bir şeyi kurtarmaya
        // çalışmak, gerçek sorunu gizler.
        var eid = TicketCancelReplay.EventIdFor(IptalId);
        _outbox.Enqueue(eid, "", OutboxKinds.TicketCancel, $$"""{"t":"{{Fis}}"}""", "");
        // damgalanmadı

        var p = Assert.IsType<TicketCancelReplayPlan>(Hazirla());
        Assert.False(p.FromOutbox);   // yeniden kurdu, kuyruktakine dokunmadı
    }

    [Fact]
    public void BASKA_fisin_damgali_kaydi_KARISTIRILMAZ()
    {
        // ← ÇİVİ: eşleşme fiş kimliğine göre. Yanlış kaydı göndermek, iptal edilmemiş bir fişi
        // "iptal edildi" diye deftere yazdırırdı.
        var baska = TicketCancelReplay.EventIdFor("baska-ticketcancel-1");
        _outbox.Enqueue(baska, "", OutboxKinds.TicketCancel, """{"t":"tkt_BASKA"}""", "");
        _outbox.MarkPermanentlyRejected(baska);

        var p = Assert.IsType<TicketCancelReplayPlan>(Hazirla());
        Assert.False(p.FromOutbox);
    }

    [Fact]
    public void EventId_URETIMDEKI_kuralla_AYNI()
    {
        // ← ÇİVİ: anahtar üretimdekinden farklı olsaydı `INSERT OR IGNORE` çalışmaz, aynı olay
        // outbox'a İKİ kez girerdi.
        Assert.Equal(IptalId + ":ticket-cancel", TicketCancelReplay.EventIdFor(IptalId));
    }
}
