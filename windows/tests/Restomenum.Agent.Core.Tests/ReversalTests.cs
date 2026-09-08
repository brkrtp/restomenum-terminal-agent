using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// Fiş/ödeme iptali (W2) — kasadaki "Fiş İptal" düğmesinin ucu.
///
/// <para>Bu yolun var olma sebebi ölçüldü: 2026-09-07'de başarısız bir kart denemesinin bıraktığı
/// açık fiş 14 saat boyunca her satışı bloke etti ve kasiyerin onu temizleyecek hiçbir yolu yoktu —
/// terminal elle kurcalanmak zorunda kalındı.</para>
/// </summary>
public class ReversalTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rev_{Guid.NewGuid():N}.db");
    private readonly CommandStore _store;
    private readonly Outbox _outbox;
    private readonly ClockOffset _clock = new();

    public ReversalTests()
    {
        _store = CommandStore.Open(_db);
        _outbox = Outbox.Open(_db);
        _clock.Sync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Dispose()
    {
        _store.Dispose();
        _outbox.Dispose();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    private const string Pay = "pay_0123456789abcdef0123456789abcdef01234567";

    private static string Zarf(string? origServiceId = "svc-orig", string? poiTxId = null,
        string category = "Reversal", string paymentId = Pay,
        string? scope = null, string? oturum = null) =>
        $$"""
        {
          "SaleToPOIRequest": {
            "MessageHeader": {
              "MessageClass": "Service", "MessageCategory": "{{category}}", "MessageType": "Request",
              "ServiceID": "void12345", "SaleID": "kasa-1", "POIID": "term-01"
            },
            "ReversalRequest": {
              "ReversalReason": "MerchantCancel",
              {{(poiTxId is null ? "" : $"\"OriginalPOITransaction\": {{ \"POITransactionID\": \"{poiTxId}\" }},")}}
              {{(origServiceId is null ? "" : $"\"MessageReference\": {{ \"MessageCategory\": \"Payment\", \"ServiceID\": \"{origServiceId}\" }},")}}
              "SaleData": { "SaleTransactionID": { "TransactionID": "{{paymentId}}" } }
            }{{(scope is null && oturum is null ? "" : $",\n    \"Restomenum\": {{ \"v\": 1{(scope is null ? "" : $", \"scope\": \"{scope}\"")}{(oturum is null ? "" : $", \"saleSessionId\": \"{oturum}\"")} }}")}}
          }
        }
        """;

    // ── AYRIŞTIRMA ──────────────────────────────────────────────────────────────

    [Fact]
    public void Acik_fis_iptali_ayristirilir()
    {
        var r = Assert.IsType<ReversalParseResult.Ok>(ReversalRequestParser.Parse(Zarf()));
        Assert.Equal("void12345", r.Request.ServiceId);
        Assert.Equal("svc-orig", r.Request.OriginalServiceId);
        Assert.Null(r.Request.OriginalPoiTransactionId);   // ← vaka: ödemesiz açık fiş
        Assert.Equal("MerchantCancel", r.Request.ReversalReason);
    }

    [Fact]
    public void Tamamlanmis_odeme_iptali_POITransactionID_ile_ayristirilir()
    {
        var r = Assert.IsType<ReversalParseResult.Ok>(
            ReversalRequestParser.Parse(Zarf(origServiceId: null, poiTxId: "POI-77")));
        Assert.Equal("POI-77", r.Request.OriginalPoiTransactionId);
    }

    [Fact]
    public void REFERANSSIZ_iptal_REDDEDILIR()
    {
        // ← ÇİVİ: referanssız iptal "cihazda ne varsa iptal et" demektir. Yanlış fişi iptal etmek
        // geri alınamaz; başka bir kasanın açık fişini silebilirdik.
        var r = Assert.IsType<ReversalParseResult.Invalid>(
            ReversalRequestParser.Parse(Zarf(origServiceId: null)));
        Assert.Equal(SaleToPoiRejectReason.Malformed, r.Reason);
    }

    [Fact]
    public void PeekCategory_yolu_secer()
    {
        Assert.Equal("Reversal", ReversalRequestParser.PeekCategory(Zarf()));
        Assert.Null(ReversalRequestParser.PeekCategory("bu json degil"));
    }

    // ── AKIŞ ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Defterde_olmayan_referans_TERMINALE_GITMEDEN_NotFound()
    {
        // ← ÇİVİ: tanımadığımız bir referans için cihaza dokunmayız.
        var (h, sim) = Kur();
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var resp = Yanit(await h.HandleReversalAsync(req));

        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("NotFound", resp.GetProperty("ErrorCondition").GetString());
        Assert.Equal(0, sim.VoidCalls);   // terminale HİÇ gidilmedi
    }

    [Fact]
    public async Task Odemesiz_acik_fis_iptal_edilir_TICKET_CANCELLED()
    {
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var govde = await h.HandleReversalAsync(req);
        var resp = Yanit(govde);

        Assert.Equal("Success", resp.GetProperty("Result").GetString());
        Assert.Equal(1, sim.VoidCalls);
        Assert.Equal(RestomenumReasons.TicketCancelled, Ek(govde).GetProperty("info").GetString());
        // Ödemesiz fişte iade TUTARI bildirilmez — olmayan bir iadeyi deftere yazdırırdı.
        Assert.False(Gövde(govde).TryGetProperty("ReversedAmount", out _));
    }

    [Fact]
    public async Task Yarim_kalan_iptal_VOID_INCOMPLETE_ve_TEKRAR_DENE_DEMEZ()
    {
        // ← ÇİVİ: yarım kalmış bir ters işlemin üstüne ikincisini bindirmek, geri alınmış bir
        // ödemeyi ikinci kez geri almaya çalışmaktır. Sonuç "olmadı" değil "BELİRSİZ".
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.VoidResult = new TransportResult(TransportOutcome.Unknown,
            ProviderResultCode: "REVERSAL_FAILED:0x0826", ErrorCondition: "InProgress",
            Reason: RestomenumReasons.VoidIncomplete);
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var govde = await h.HandleReversalAsync(req);
        var resp = Yanit(govde);

        Assert.Equal("Failure", resp.GetProperty("Result").GetString());
        Assert.Equal("InProgress", resp.GetProperty("ErrorCondition").GetString());
        Assert.Equal(RestomenumReasons.VoidIncomplete, Ek(govde).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Iptalde_paymentInvoked_HER_ZAMAN_false()
    {
        // İptal yolunda `FP3_Payment` çağrılmaz. Alan yine de ATLANMAZ.
        var (h, _) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var ek = Ek(await h.HandleReversalAsync(req));

        Assert.Equal(1, ek.GetProperty("v").GetInt32());
        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
    }

    // ── W9: TÜR AYRIMI (kind) ───────────────────────────────────────────────────

    [Fact]
    public async Task Iptal_kaydi_ACILIS_KURTARMASINA_GIRMEZ()
    {
        // ← ÇİVİ: iptal kayıtları satışla aynı tabloda yaşıyor. Tür ayrımı olmasaydı açılış
        // kurtarması bir iptali "yarım kalmış satış" sanıp terminale "bu ödeme işlendi mi" diye
        // sorardı — olmayan bir ödemeyi kovalamak.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        await h.HandleReversalAsync(req);

        var bekleyen = _store.Pending();
        Assert.DoesNotContain(bekleyen, k => k.CommandId == "void12345");
    }

    [Fact]
    public async Task Ayni_iptal_ikinci_kez_gelirse_CIHAZA_GITMEZ()
    {
        // Kasa ağ hatasında aynı zarfı yeniden POST edebiliyor. İkinci `VoidAll` ya boşa çalışır
        // ya da ARAYA GİREN YENİ bir fişi iptal ederdi — ikincisi gerçek zarar.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var ilk = await h.HandleReversalAsync(req);
        var ikinci = await h.HandleReversalAsync(req);

        Assert.Equal(1, sim.VoidCalls);   // ← ÇİVİ
        Assert.Equal(ilk, ikinci);        // saklanan sonuç birebir replay edildi
    }

    [Fact]
    public async Task Yarim_kalan_iptal_UNKNOWNda_BIRAKILIR_tekrar_sorulabilsin()
    {
        // Belirsiz sonucu "kesin" diye saklamak, tekrar geldiğinde cihaza sormayı engellerdi.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.VoidResult = new TransportResult(TransportOutcome.Unknown,
            ProviderResultCode: "REVERSAL_FAILED", ErrorCondition: "InProgress",
            Reason: RestomenumReasons.VoidIncomplete);
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        await h.HandleReversalAsync(req);
        await h.HandleReversalAsync(req);

        Assert.Equal(2, sim.VoidCalls);   // belirsizlik sürüyor → yeniden soruldu
    }

    // ── W15: FİŞ BAZLI İPTAL (scope: ticket) ────────────────────────────────────

    [Fact]
    public void Fis_kapsaminda_REFERANS_ARANMAZ()
    {
        // Kasiyer cihazın başında, ekranda gördüğü fişi iptal ediyor; o fiş başka bir kasadan
        // kalmış olabilir ve referansı bizde olmayabilir.
        // ← ÇİVİ: ödeme kapsamında referanssız istek REDDEDİLİR, fiş kapsamında KABUL EDİLİR.
        var r = Assert.IsType<ReversalParseResult.Ok>(
            ReversalRequestParser.Parse(Zarf(origServiceId: null, scope: "ticket", oturum: "oturum-A")));
        Assert.Equal("ticket", r.Request.Scope);
        Assert.Equal("oturum-A", r.Request.SaleSessionId);

        Assert.IsType<ReversalParseResult.Invalid>(
            ReversalRequestParser.Parse(Zarf(origServiceId: null)));   // ödeme kapsamı: RET
    }

    [Fact]
    public async Task Fis_iptali_BASKA_oturumun_fisini_de_iptal_eder_ve_SAHIBINI_bildirir()
    {
        // ← ÇİVİ: platform hangi oturumun satırlarını düşüreceğini İSTEĞE göre değil, GERÇEKTEN
        // iptal edilen fişe göre bilmeli. İstek oturum-A'dan geliyor, cihazdaki fiş oturum-B'nin.
        var (h, sim) = Kur();
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 490,
            PaymentCount: 1));
        sim.TicketVoidResult = new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: true,
            VoidedPaymentCount: 1, VoidedAmountMinor: 490, CancelledSaleSessionId: "oturum-B");
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(scope: "ticket", oturum: "oturum-A"))).Request;

        var govde = await h.HandleReversalAsync(req);
        var ek = Ek(govde);

        Assert.Equal("Success", Yanit(govde).GetProperty("Result").GetString());
        Assert.Equal("ticket", ek.GetProperty("scope").GetString());
        Assert.Equal("oturum-A", ek.GetProperty("saleSessionId").GetString());
        Assert.Equal("oturum-B", ek.GetProperty("cancelledSaleSessionId").GetString());   // ← ÇİVİ
        Assert.Equal(1, ek.GetProperty("voidedPaymentCount").GetInt32());
        Assert.Equal(490, ek.GetProperty("voidedAmountMinor").GetInt64());
        Assert.Equal(RestomenumReasons.TicketCancelled, ek.GetProperty("info").GetString());
        Assert.Equal(4.9, Gövde(govde).GetProperty("ReversedAmount").GetDouble(), 3);
    }

    // ── W37: HANGİ FİŞİN iptal edildiği ─────────────────────────────────────────

    [Fact]
    public async Task Iptal_bildiriminde_ticketId_GIDER()
    {
        // ← ÇİVİ: platform iptal sonucunda hangi fişin iptal edildiğini bilmiyordu ve
        // oturum+terminal ile arıyordu; aynı oturumda önce KAPANMIŞ bir fişin tahsilatı da
        // ters kayda gidebiliyordu. K-33 ile o satırlar para taşıyor.
        var (h, sim) = Kur();
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990,
            PaidAmountMinor: 490, PaymentCount: 1));
        sim.TicketVoidResult = new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: true,
            VoidedPaymentCount: 1, VoidedAmountMinor: 490, CancelledSaleSessionId: "oturum-B",
            CancelledTicketId: "tkt_11112222333344445555666677778888");
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(scope: "ticket", oturum: "oturum-A"))).Request;

        var govde = await h.HandleReversalAsync(req);
        var ek = Ek(govde);

        Assert.Equal("tkt_11112222333344445555666677778888", ek.GetProperty("ticketId").GetString());
        // Oturum bilgisi DE duruyor — ikisi ayrı soru: hangi fiş, kimin fişi.
        Assert.Equal("oturum-B", ek.GetProperty("cancelledSaleSessionId").GetString());
    }

    [Fact]
    public async Task Acik_fis_YOKSA_ticketId_alani_HIC_KONMAZ()
    {
        // ← ÇİVİ: iptal edilen fiş yoksa "iptal edilen fişin kimliği" diye bir şey de yok.
        // Boş dize ya da null göndermek, platformda var olmayan bir fişi aratırdı.
        var (h, sim) = Kur();
        sim.WithTicket(new TicketState(HasOpenTicket: false, TotalAmountMinor: 0, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(scope: "ticket", oturum: "oturum-A"))).Request;

        var ek = Ek(await h.HandleReversalAsync(req));

        Assert.False(ek.TryGetProperty("ticketId", out _));
        Assert.Equal(RestomenumReasons.TicketNotOpen, ek.GetProperty("info").GetString());
    }

    [Fact]
    public async Task Outboxa_giren_govde_de_ticketId_TASIR()
    {
        // ← ÇİVİ: yeniden gönderimde kaybolmamalı. Kasaya giden ile kuyruğa yazılan AYNI gövde
        // olmalı — iki ayrı üretici olsaydı biri değişince diğeri sessizce eskirdi.
        var (h, sim) = Kur();
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990,
            PaidAmountMinor: 490, PaymentCount: 1));
        sim.TicketVoidResult = new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: true,
            VoidedPaymentCount: 1, VoidedAmountMinor: 490, CancelledSaleSessionId: "oturum-B",
            CancelledTicketId: "tkt_aaaabbbbccccddddeeeeffff00001111");
        // Gönderim BAŞARISIZ olsun ki kayıt kuyrukta kalsın — başarılıda `Confirm` siliyor
        // (doğru davranış). Test edilecek şey tam da "gidemediğinde kuyrukta ne duruyor".
        _notifier.TicketCancelResult = new NotifyResult(NotifyOutcome.NetworkError, null, null, 0, "ağ");
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(scope: "ticket", oturum: "oturum-A"))).Request;

        await h.HandleReversalAsync(req);

        var kuyruk = _outbox.Pending(ignoreBackoff: true)
            .Single(e => e.Status == OutboxKinds.TicketCancel);
        var kuyruktakiEk = System.Text.Json.JsonDocument.Parse(kuyruk.PayloadJson).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.Equal("tkt_aaaabbbbccccddddeeeeffff00001111",
            kuyruktakiEk.GetProperty("ticketId").GetString());
    }

    [Fact]
    public async Task Acik_fis_YOKSA_basarisizlik_DEGIL_TICKET_NOT_OPEN()
    {
        // Kasiyer düğmeye bastı, iptal edilecek bir şey yoktu. Bu bir hata değil.
        var (h, sim) = Kur();
        sim.WithTicket(new TicketState(HasOpenTicket: false, TotalAmountMinor: 0, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(scope: "ticket", oturum: "oturum-A"))).Request;

        var govde = await h.HandleReversalAsync(req);
        var ek = Ek(govde);

        Assert.Equal("Success", Yanit(govde).GetProperty("Result").GetString());
        Assert.Equal(RestomenumReasons.TicketNotOpen, ek.GetProperty("info").GetString());
        Assert.Equal(0, ek.GetProperty("voidedPaymentCount").GetInt32());
        Assert.Equal(0, ek.GetProperty("voidedAmountMinor").GetInt64());
        // Olmayan bir iade deftere yazılmaz.
        Assert.False(Gövde(govde).TryGetProperty("ReversedAmount", out _));
    }

    [Fact]
    public async Task Fis_iptali_YARIM_kalirsa_VOID_INCOMPLETE()
    {
        var (h, sim) = Kur();
        sim.TicketVoidResult = new TicketVoidResult(TransportOutcome.Unknown, TicketWasOpen: true,
            VoidedPaymentCount: 1, VoidedAmountMinor: 490, CancelledSaleSessionId: "oturum-B",
            ErrorCondition: "InProgress", Reason: RestomenumReasons.VoidIncomplete,
            ProviderResultCode: "REVERSAL_FAILED:0x0826");
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(scope: "ticket", oturum: "oturum-A"))).Request;

        var govde = await h.HandleReversalAsync(req);

        Assert.Equal("Failure", Yanit(govde).GetProperty("Result").GetString());
        Assert.Equal("InProgress", Yanit(govde).GetProperty("ErrorCondition").GetString());
        Assert.Equal(RestomenumReasons.VoidIncomplete, Ek(govde).GetProperty("reason").GetString());
        // Yarım kalmışta iade tutarı bildirilmez — geri gitmemiş olabilir.
        Assert.False(Gövde(govde).TryGetProperty("ReversedAmount", out _));
    }

    [Fact]
    public async Task Odeme_bazli_iptalin_bloguu_fis_bazliyla_AYNI_alanlari_tasir()
    {
        // ← ÇİVİ (W23): 17:44'te kasa iptalin OLDUĞUNU gördü ama NE olduğunu göremedi — sayaçlar
        // yalnız fiş kapsamında üretiliyordu. Aynı olayın kasaya ve deftere iki farklı zenginlikte
        // gitmesi, kasada "2 ödeme / 4,90 ₺ iptal edildi" diyememek demekti.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990,
            PaidAmountMinor: 490, PaymentCount: 2));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(oturum: "oturum-A"))).Request;

        var govde = await h.HandleReversalAsync(req);
        var ek = Ek(govde);

        Assert.Null(req.Scope);                                        // ÖDEME kapsamı
        Assert.Equal("Success", Yanit(govde).GetProperty("Result").GetString());
        Assert.Equal(2, ek.GetProperty("voidedPaymentCount").GetInt32());
        Assert.Equal(490, ek.GetProperty("voidedAmountMinor").GetInt64());
        Assert.Equal("oturum-A", ek.GetProperty("saleSessionId").GetString());
        // Bağ yoksa alan AÇIKÇA null olur — "bilinmiyor" ile "yok" ayrı beyanlar.
        Assert.Equal(JsonValueKind.Null, ek.GetProperty("cancelledSaleSessionId").ValueKind);
    }

    [Fact]
    public async Task Odeme_bazli_iptal_REGRESYONSUZ()
    {
        // scope yoksa eski davranış aynen sürer.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(Zarf())).Request;

        var govde = await h.HandleReversalAsync(req);

        Assert.Null(req.Scope);
        Assert.Equal(1, sim.VoidCalls);          // ödeme yolu çağrıldı
        Assert.Equal(0, sim.TicketVoidCalls);    // fiş yolu ÇAĞRILMADI
        Assert.Equal("Success", Yanit(govde).GetProperty("Result").GetString());
    }

    [Fact]
    public async Task Fis_iptali_AYRI_uca_bildirilir_ve_ticketCancelId_AYNEN_doner()
    {
        // Ödeme sonucu ucu paymentId ile adresleniyor; fiş iptali bir denemeye ait DEĞİL. Yanlış
        // uca göndermek 409/404 üretir ve defter iptali hiç görmez.
        // ← ÇİVİ: ayrı uç çağrılır ve komut kimliği DEĞİŞTİRİLMEDEN yansıtılır.
        var (h, sim) = Kur();
        sim.TicketVoidResult = new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: true,
            VoidedPaymentCount: 1, VoidedAmountMinor: 490, CancelledSaleSessionId: "oturum-B");
        var zarf = Zarf(scope: "ticket", oturum: "oturum-A").Replace(
            "\"scope\": \"ticket\"", "\"scope\": \"ticket\", \"ticketCancelId\": \"tc-123\"");
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(zarf)).Request;
        Assert.Equal("tc-123", req.TicketCancelId);

        var govde = await h.HandleReversalAsync(req);

        Assert.Equal("tc-123", Ek(govde).GetProperty("ticketCancelId").GetString());
        Assert.Single(_notifier.TicketCancelBodies);   // ← AYRI uç çağrıldı
    }

    [Fact]
    public void Fis_kapsaminda_SaleData_HIC_OLMAYABILIR()
    {
        // Fiş iptali bir DENEMEYE ait değil: cihazda başka kasadan kalmış, bu kasanın hiç
        // paymentId'si olmayan bir fiş durabilir. Platformun kendi zarfı da SaleData göndermiyor.
        // ← ÇİVİ: fiş kapsamında SaleData'sız istek KABUL, ödeme kapsamında RET.
        var zarfsiz = """
        {
          "SaleToPOIRequest": {
            "MessageHeader": {
              "MessageClass": "Service", "MessageCategory": "Reversal", "MessageType": "Request",
              "ServiceID": "void12345", "SaleID": "kasa-1", "POIID": "term-01"
            },
            "ReversalRequest": { "ReversalReason": "MerchantCancel" },
            "Restomenum": { "v": 1, "scope": "ticket", "ticketCancelId": "uuid-term-ticketcancel" }
          }
        }
        """;

        var r = Assert.IsType<ReversalParseResult.Ok>(ReversalRequestParser.Parse(zarfsiz));
        Assert.Null(r.Request.PaymentId);
        Assert.Equal("ticket", r.Request.Scope);
        Assert.Equal("uuid-term-ticketcancel", r.Request.TicketCancelId);

        // Ödeme kapsamında aynı zarf (scope yok) REDDEDİLİR.
        var odeme = zarfsiz.Replace("\"scope\": \"ticket\", ", "");
        Assert.IsType<ReversalParseResult.Invalid>(ReversalRequestParser.Parse(odeme));
    }

    [Fact]
    public async Task PaymentIdsiz_fis_iptali_ucdan_uca_calisir()
    {
        var (h, sim) = Kur();
        sim.TicketVoidResult = new TicketVoidResult(TransportOutcome.Approved, TicketWasOpen: true,
            VoidedPaymentCount: 1, VoidedAmountMinor: 490, CancelledSaleSessionId: "oturum-B");
        var zarf = """
        {
          "SaleToPOIRequest": {
            "MessageHeader": {
              "MessageClass": "Service", "MessageCategory": "Reversal", "MessageType": "Request",
              "ServiceID": "void98765", "SaleID": "kasa-1", "POIID": "term-01"
            },
            "ReversalRequest": { "ReversalReason": "MerchantCancel" },
            "Restomenum": { "v": 1, "scope": "ticket", "ticketCancelId": "tc-uuid-1" }
          }
        }
        """;
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(zarf)).Request;

        var govde = await h.HandleReversalAsync(req);

        Assert.Equal("Success", Yanit(govde).GetProperty("Result").GetString());
        Assert.Equal("tc-uuid-1", Ek(govde).GetProperty("ticketCancelId").GetString());
        Assert.Equal("oturum-B", Ek(govde).GetProperty("cancelledSaleSessionId").GetString());
        Assert.Single(_notifier.TicketCancelBodies);
    }

    // ── yardımcılar ─────────────────────────────────────────────────────────────

    private sealed class FakeAmounts : IPaymentDetailClient
    {
        public Task<PaymentDetailResult> FetchAsync(string p, CancellationToken ct = default) =>
            Task.FromResult<PaymentDetailResult>(new PaymentDetailResult.Rejected(
                PaymentRejectReason.NotFound, "yok", 404));
    }

    private sealed class FakeResolver : ILineDepartmentResolver
    {
        public DepartmentMatch? Resolve(string? productCode, string? categoryId) => new(3, null);
    }

    private sealed class FakePaymentMethods : IPaymentMethodResolver
    {
        public int? Resolve(string paymentMethodId) => GmpPaymentTypes.Cash;
    }

    private sealed class FakeNotifier : IResultNotifier
    {
        public List<string> TicketCancelBodies { get; } = new();
        public Task<NotifyResult> NotifyAsync(string p, string body, CancellationToken ct = default) =>
            Task.FromResult(new NotifyResult(NotifyOutcome.Recorded, "OK", null, 200, ""));
        /// <summary>İptal bildiriminin sonucu — testte ağ hatasına çevrilebilir.</summary>
        public NotifyResult TicketCancelResult = new(NotifyOutcome.Recorded, "OK", null, 200, "");
        /// <summary>Çağrı SIRASINDA çalışır — testte sahte saati ilerletmek için (W46 süre ölçümü).</summary>
        public Action? IptalSirasinda;
        public Task<NotifyResult> NotifyTicketCancelAsync(string body, CancellationToken ct = default)
        { TicketCancelBodies.Add(body); IptalSirasinda?.Invoke(); return Task.FromResult(TicketCancelResult); }
        public List<string> TicketClosedBodies { get; } = new();
        public Task<NotifyResult> NotifyTicketClosedAsync(string body, CancellationToken ct = default)
        { TicketClosedBodies.Add(body); return Task.FromResult(new NotifyResult(NotifyOutcome.Recorded, "OK", null, 200, "")); }
    }

    // ── W46: BİLDİRİMİN AKIBETİ HER DURUMDA GÜNLÜĞE YAZILIYOR ───────────────────

    [Fact]
    public async Task Iptal_bildirimi_BASARIDA_da_loglanir()
    {
        // ← ÇİVİ: 2026-09-08 19:13'te iptal başarıyla gitti ama günlükte tek satır yoktu; kayıt
        // `Confirm` ile silindiği için "ne zaman gitti" sorusu KALICI olarak cevapsız kaldı.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(scope: "ticket", oturum: "oturum-A"))).Request;

        await h.HandleReversalAsync(req);

        Assert.Contains(_loglar, l => l.Mesaj.Contains("fiş iptali bildirimi yazıldı"));
        Assert.Equal("Recorded", LogAlani("bildirimi yazıldı", "outcome"));
        Assert.Equal(200, LogAlani("bildirimi yazıldı", "StatusCode"));
        Assert.Equal("OK", LogAlani("bildirimi yazıldı", "state"));
    }

    [Fact]
    public async Task Iptal_bildiriminin_SURESI_gercekten_olculuyor()
    {
        // ← ÇİVİ: sabit 0 yazmak da "süre var" görüntüsü verirdi. Saat çağrı SIRASINDA ilerliyor;
        // ölçüm gerçekten çağrıyı sarmalamıyorsa bu test kırılır.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        _notifier.IptalSirasinda = () => _saatMs += 1_234;
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(scope: "ticket", oturum: "oturum-A"))).Request;

        await h.HandleReversalAsync(req);

        Assert.Equal(1_234L, LogAlani("bildirimi yazıldı", "sureMs"));
    }

    [Fact]
    public async Task Iptal_bildirimi_AG_HATASINDA_da_loglanir_ve_SESSIZ_kalmaz()
    {
        // ← ÇİVİ: `IsProblem` false + `IsFinal` false birlikte "gitmedi, kuyrukta kaldı" demek ve
        // bu dal HİÇ log yazmıyordu. Cihazda iptal gerçekleşmiş, bildirim gitmemiş, günlük sessiz —
        // "gitti" ile "hiç denenmedi" ayırt edilemez oluyordu.
        var (h, sim) = Kur();
        _store.Save("svc-orig", Pay, "term-01", _clock.ServerNow() + 60_000);
        sim.WithTicket(new TicketState(HasOpenTicket: true, TotalAmountMinor: 990, PaidAmountMinor: 0));
        _notifier.TicketCancelResult = new NotifyResult(NotifyOutcome.NetworkError, null, null, 0, "ağ yok");
        var req = ((ReversalParseResult.Ok)ReversalRequestParser.Parse(
            Zarf(scope: "ticket", oturum: "oturum-A"))).Request;

        await h.HandleReversalAsync(req);

        Assert.Contains(_loglar, l => l.Mesaj.Contains("fiş iptali bildirimi GİTMEDİ"));
        // ...ve BAŞARI metnine DÜŞMEMELİ: başlık yanlışsa günlüğü tarayan yanlış okur.
        Assert.DoesNotContain(_loglar, l => l.Mesaj.Contains("fiş iptali bildirimi yazıldı"));
        Assert.Equal("NetworkError", LogAlani("bildirimi GİTMEDİ", "outcome"));
    }

    private FakeNotifier _notifier = new();

    /// <summary>Sahte duvar saati (unix ms) — W46 süre ölçümü testlerde bununla sürülüyor.</summary>
    private long _saatMs = 1_700_000_000_000;

    /// <summary>Yakalanan log çağrıları (mesaj, veri) — W46 çivileri buradan okuyor.</summary>
    private readonly List<(string Mesaj, object? Veri)> _loglar = new();

    /// <summary>Yakalanan logdan bir alanı oku (anonim tip → yansıma).</summary>
    private object? LogAlani(string mesajParcasi, string alan)
    {
        var kayit = _loglar.LastOrDefault(l => l.Mesaj.Contains(mesajParcasi));
        return kayit.Veri?.GetType().GetProperty(alan)?.GetValue(kayit.Veri);
    }

    private (LocalSaleHandler, SimulatorTransport) Kur()
    {
        var sim = new SimulatorTransport();
        var orch = new AgentOrchestrator(_store, sim, _clock, RecoveryPolicy.Immediate);
        _notifier = new FakeNotifier();
        _loglar.Clear();
        var h = new LocalSaleHandler(new FakeAmounts(), orch, _store, new FakeResolver(),
            new FakePaymentMethods(), _notifier, _outbox, sim,
            now: () => DateTimeOffset.FromUnixTimeMilliseconds(_saatMs),
            log: (m, o) => _loglar.Add((m, o)));
        return (h, sim);
    }

    private static JsonElement Gövde(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("SaleToPOIResponse").GetProperty("ReversalResponse");

    private static JsonElement Yanit(string json) => Gövde(json).GetProperty("Response");

    private static JsonElement Ek(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
}
