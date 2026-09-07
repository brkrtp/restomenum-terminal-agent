using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// GMP-3 kodu → nexo <c>ErrorCondition</c> eşlemesinin çivileri (W1).
///
/// <para><b>Ölçülmüş zarar:</b> 2026-09-06 21:33 UTC, banka hattı olmayan test terminalinde bir kart
/// denemesi. <c>FP3_Payment</c> <b>2085</b> döndürdü (GMPDLL_2026_09_07_003334.TXT, satır 1233);
/// taşıma katmanının son satırı <c>_ =&gt; Declined</c> olduğu için deneme kasaya <c>Refusal</c> =
/// "kart reddedildi, başka kart isteyin" diye kapandı. Müşterinin kartında sorun yoktu — hat yoktu.
/// Aynı kodun ardında "istek gitti, cevap gelmedi" vakası da varsa ilk çekim bankada geçmiş olabilir
/// ve kasiyer ikinci kez tahsilat yapardı.</para>
///
/// <para><b>Değişmez:</b> "bilmiyorum" ASLA "kesin hayır"a çevrilmez.</para>
/// </summary>
public class GmpErrorMapTests
{
    // ── 2086: BANKA HATTINA ULAŞILAMADI, "REDDEDİLDİ" DEĞİL ──────────────────────

    [Fact]
    public void Kod2086_UnreachableHost_Refusal_DEGIL()
    {
        // 2086 = "ödeme başarısız VE banka uygulamasından özel mesaj var". Canlı terminalde banka
        // hattı yokken gelen mesajlar: "BAĞLANTI HATASI", "NO RESPONSE". Yani kod, host'a
        // ulaşılamamasıyla issuer reddini AYNI değerin altında topluyor.
        // nexo standardı bunları ayırır: 07 = UnreachableHost, 08 = Refusal.
        // ← ÇİVİ: ayırt edemiyorsak ulaşılamamış sayarız; "reddetti" demek uydurmaktır.
        var e = GmpErrorMap.Map(GmpCodes.PaymentFailedWithBankCode, GmpStep.Payment);

        Assert.Equal("UnreachableHost", e.ErrorCondition);
        Assert.NotEqual("Refusal", e.ErrorCondition);
        Assert.Equal(TransportOutcome.Unknown, e.Outcome);
        Assert.Equal(GmpErrorMap.MoneyRisk.Unknown, e.Money);
    }

    [Fact]
    public void Kod2085_BELIRSIZ_kesin_ret_DEGIL()
    {
        // 2085 = "ödeme başarısız VE banka uygulamasından mesaj YOK". Mesaj yoksa issuer yanıtı da
        // yoktur → kesin-ret için gereken kanıt hiç oluşmamıştır. Üretici belgesi de 2085 için
        // "cihazda ödeme oluşmadı" DEMİYOR, yalnız "başarısız" diyor.
        // ← ÇİVİ: 2026-09-06'da kasaya "kart reddedildi" dedirten kod tam olarak buydu.
        var e = GmpErrorMap.Map(GmpCodes.PaymentFailed, GmpStep.Payment);

        Assert.NotEqual("Refusal", e.ErrorCondition);
        Assert.Equal(TransportOutcome.Unknown, e.Outcome);
        Assert.Equal(GmpErrorMap.MoneyRisk.Unknown, e.Money);
    }

    // ── HİÇBİR KOD KESİN-RET ÜRETMEZ ────────────────────────────────────────────

    [Fact]
    public void HicbirKod_HicbirAdimda_Refusal_URETMEZ()
    {
        // Kesin-ret yalnız banka host'unun AÇIK ret yanıtıyla doğrulanabilir (issuer yanıt kodu).
        // Agent bugün o kanalı okumuyor: ST_PaymentErrMessage.ErrorCode sarmalayıcıda yüzeye
        // çıkarılmadı ve canlıda hiç ölçülmedi. Dolayısıyla Refusal üretebilecek TEK meşru kaynak
        // yok; tabloda da hiçbir koddan doğmamalı.
        // ← ÇİVİ: yeni bir kod eklerken "bu kesin ret" diye kestirme atılırsa bu test kırılır.
        var kodlar = new List<uint>();
        for (uint c = 0; c <= 0x0FFF; c++) kodlar.Add(c);
        for (uint c = 0xF000; c <= 0xF0FF; c++) kodlar.Add(c);

        foreach (var adim in new[] { GmpStep.BeforePayment, GmpStep.Payment, GmpStep.AfterPayment })
            foreach (var kod in kodlar)
                Assert.NotEqual("Refusal", GmpErrorMap.Map(kod, adim).ErrorCondition);
    }

    [Fact]
    public void OdemeAdiminda_HICBIR_KOD_paranin_hareket_etmedigini_SOYLEMEZ()
    {
        // FP3_Payment çağrıldıktan sonra hiçbir dönüş kodu "cihazda ödeme oluşmadı" garantisi
        // vermez. Üretici belgesi bunu 2022'yi anlatırken açıkça yazıyor: "When an external
        // application call FP3_Payment function, it can be successful on Ingenico device, even
        // though you don't get result properly."
        // ← ÇİVİ: ödeme adımında MoneyRisk.No görürsek biri belgeyi aşan bir varsayım eklemiştir.
        for (uint kod = 1; kod <= 0x0FFF; kod++)
            Assert.NotEqual(GmpErrorMap.MoneyRisk.No, GmpErrorMap.Map(kod, GmpStep.Payment).Money);
    }

    [Fact]
    public void KapsanmayanKod_BELIRSIZ_varsayilir()
    {
        // Üretici belgesinin açık talimatı: "If these errors and other errors you don't cover are
        // returned, you must check current situation with FP3_GetTicket and act accordingly."
        // Eski kodda son satır _ => Declined idi — yani tam tersi.
        var e = GmpErrorMap.Map(0x0BAD, GmpStep.BeforePayment);

        Assert.Equal(TransportOutcome.Unknown, e.Outcome);
        Assert.Equal("InProgress", e.ErrorCondition);
        Assert.Equal(GmpErrorMap.MoneyRisk.Unknown, e.Money);
    }

    [Fact]
    public void OdemeOncesiHatalar_ParaHareketETMEZ_ama_Refusal_da_DEGIL()
    {
        // Bu kesinlik GMP-3 belgesinden değil, kendi çağrı sıramızdan gelir: FP3_Payment henüz
        // çağrılmadı. Yine de kesin-ret DEĞİL — banka hiç devrede olmadığından "kart reddedildi"
        // mesajı kasiyeri olmayan bir kart sorununa yönlendirirdi.
        foreach (var kod in new[] { GmpCodes.CashierEntryRequired, GmpCodes.ZRequired, GmpCodes.PortNotOpen })
        {
            var e = GmpErrorMap.Map(kod, GmpStep.BeforePayment);
            Assert.Equal(GmpErrorMap.MoneyRisk.No, e.Money);
            Assert.Equal(TransportOutcome.Declined, e.Outcome);
            Assert.NotEqual("Refusal", e.ErrorCondition);
        }
    }

    [Fact]
    public void AcikFis_2080_ANINDA_kesin_cevap_belirsiz_DEGIL()
    {
        // `FP3_Start` 2080 ile reddedildi → BU denemede ödeme hiç başlamadı. Açık fişin içindeki
        // para ÖNCEKİ denemenin sorusudur, bu denemenin değil.
        // ⚠️ Belirsiz demek pahalıya mal oldu: 2026-09-06 gecesi ve 2026-09-07 14:30'da her yeni
        // deneme 6 tur boşa yoklama yapıp çözümsüz kaldı; kasiyer art arda "operatöre danışın"
        // gördü, defterde denemeler asılı kaldı.
        // ← ÇİVİ: anında ve kesin — PaymentRestriction + TICKET_ALREADY_OPEN.
        var e = GmpErrorMap.Map(GmpCodes.AlreadyDone, GmpStep.BeforePayment);

        Assert.Equal(TransportOutcome.Declined, e.Outcome);
        Assert.Equal("PaymentRestriction", e.ErrorCondition);
        Assert.Equal(RestomenumReasons.TicketAlreadyOpen, e.Reason);
        Assert.Equal(GmpErrorMap.MoneyRisk.No, e.Money);
        Assert.NotEqual("Refusal", e.ErrorCondition);
    }

    // ── SINIR: KASAYA/PLATFORMA GİDEN GÖVDE ─────────────────────────────────────

    [Fact]
    public void Govde_2086_UnreachableHost_tasir()
    {
        // ← ÇİVİ: eşleme doğru olup gövdede kaybolursa platform yine yanlış sınıflandırır.
        // Deftere yazılan alan budur (result.providerResultCode).
        var sonuc = new TransportResult(TransportOutcome.Unknown,
            ProviderResultCode: "Payment:0x0826", ErrorCondition: "UnreachableHost");

        Assert.Equal("UnreachableHost", Kosul(SaleToPoiResponseBuilder.BuildResult(Istek(), sonuc, 2, Simdi)));
    }

    [Fact]
    public void Govde_kosul_yazilmamissa_belirsize_duser_Refusal_DEGIL()
    {
        // Koşulu olmayan bir Declined programlama eksiğidir; güvenli taraf belirsizdir.
        var sonuc = new TransportResult(TransportOutcome.Declined, ProviderResultCode: "X");

        Assert.Equal("InProgress", Kosul(SaleToPoiResponseBuilder.BuildResult(Istek(), sonuc, 2, Simdi)));
    }

    // ── KURTARMA YOKLAMASI SONRASI KOŞUL TAŞINIYOR MU ───────────────────────────

    [Fact]
    public async Task Yoklama_KANITSIZ_islenmemis_derse_2086_kosulu_KORUNUR()
    {
        // Peer kuralı: 2086 → UnreachableHost; yalnız yoklama BELİRSİZ kalırsa InProgress.
        // Yoklama "işlenmedi" diyorsa sebep hâlâ ilk koddur — hattın olmaması.
        var db = Path.Combine(Path.GetTempPath(), $"em_{Guid.NewGuid():N}.db");
        using var store = CommandStore.Open(db);
        var clock = new ClockOffset();
        clock.Sync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var sim = new SimulatorTransport()
            .Expect(new TransportResult(TransportOutcome.Unknown,
                ProviderResultCode: "Payment:0x0826", ErrorCondition: "UnreachableHost"));
        // CounterRead: false → "açık fiş yok, anlık görüntü de yok" ÇIKARIMI. Kanıt değil.
        sim.ProbeResult = new PaymentProbe(ProbeVerdict.NotLanded);

        var orch = new AgentOrchestrator(store, sim, clock, RecoveryPolicy.Immediate);
        var sonuc = await orch.HandleAsync(
            new SaleRequest("c-em", "p-em", "t1", 990, "TRY", 2, "prov"),
            clock.ServerNow() + 60_000);

        Assert.Equal(AgentDecision.RetryLater, sonuc.Decision);
        Assert.Equal("UnreachableHost", sonuc.Result?.ErrorCondition);
    }

    // ── yardımcılar ─────────────────────────────────────────────────────────────

    private static DateTimeOffset Simdi => DateTimeOffset.UnixEpoch;

    private static SaleToPoiRequest Istek() => new("srv1", "sale1", "poi1", "pay1", "ref1", Simdi);

    private static string? Kosul(string govde) =>
        JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("PaymentResponse")
            .GetProperty("Response").GetProperty("ErrorCondition").GetString();

    // ── Restomenum EK BLOĞU: "kart çekildi mi" sorusunun cevabı ─────────────────

    [Fact]
    public void Ek_blok_paymentInvoked_ASLA_atlanmaz()
    {
        // Alanın yokluğu "bilmiyorum" ile "hayır" arasında YENİ bir belirsizlik üretirdi — W1'de
        // kapattığımız kapının aynısı. Bu yüzden her gövdede var.
        foreach (var govde in new[]
        {
            SaleToPoiResponseBuilder.BuildResult(Istek(),
                new TransportResult(TransportOutcome.Approved, ApprovedAmountMinor: 990), 2, Simdi),
            SaleToPoiResponseBuilder.BuildResult(Istek(),
                new TransportResult(TransportOutcome.Unknown, ErrorCondition: "UnreachableHost"), 2, Simdi),
            SaleToPoiResponseBuilder.BuildFailure(Istek(), "PaymentRestriction", "X", Simdi),
        })
        {
            var ek = Ek(govde);
            Assert.Equal(1, ek.GetProperty("v").GetInt32());
            Assert.True(ek.TryGetProperty("paymentInvoked", out _));
        }
    }

    [Fact]
    public void Odeme_CAGRILDIYSA_paymentInvoked_false_URETILMEZ()
    {
        // ← ÇİVİ (peer kriteri): FP3_Payment çağrılmış bir yolda `paymentInvoked:false` üretmek,
        // kasaya "kart kesinlikle çekilmedi" demektir. Para çekilmişse bu yalandır ve kasiyeri
        // ikinci denemeye iter.
        foreach (var kod in new uint[] { GmpCodes.PaymentFailed, GmpCodes.PaymentFailedWithBankCode,
                                         GmpCodes.Timeout, GmpCodes.RecvBusy, 0x0BAD })
        {
            var e = GmpErrorMap.Map(kod, GmpStep.Payment);
            var sonuc = new TransportResult(e.Outcome, ErrorCondition: e.ErrorCondition,
                PaymentInvoked: true, Reason: e.Reason);
            var ek = Ek(SaleToPoiResponseBuilder.BuildResult(Istek(), sonuc, 2, Simdi));
            Assert.True(ek.GetProperty("paymentInvoked").GetBoolean());
        }
    }

    [Fact]
    public void Terminale_GIDILMEDIYSE_paymentInvoked_false_ve_sebep_yazili()
    {
        var ek = Ek(SaleToPoiResponseBuilder.BuildFailure(Istek(), "PaymentRestriction",
            "PRODUCT_UNMAPPED:x", Simdi, RestomenumReasons.ProductUnmapped));

        Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
        Assert.Equal("PRODUCT_UNMAPPED", ek.GetProperty("reason").GetString());
    }

    [Fact]
    public void AcikFis_2080_govdede_TICKET_ALREADY_OPEN_tasir()
    {
        var e = GmpErrorMap.Map(GmpCodes.AlreadyDone, GmpStep.BeforePayment);
        var sonuc = new TransportResult(e.Outcome, ErrorCondition: e.ErrorCondition,
            PaymentInvoked: false, Reason: e.Reason);
        var govde = SaleToPoiResponseBuilder.BuildResult(Istek(), sonuc, 2, Simdi);

        Assert.Equal("PaymentRestriction", Kosul(govde));
        Assert.False(Ek(govde).GetProperty("paymentInvoked").GetBoolean());
        Assert.Equal("TICKET_ALREADY_OPEN", Ek(govde).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Yoklama_KANITLI_islenmemis_derse_NOT_LANDED_kesin_cevap()
    {
        // W7: fiş FİİLEN okundu ve ödeme sayacı kıpırdamadı → cihazın kendi defteri "bu komut için
        // işlem yok" diyor. Bu bir ÇIKARIM değil KANIT; kasaya kesin cevap verilebilir, deneme
        // 24 saat çözüm döngüsünde asılı kalmaz.
        // ← ÇİVİ: `paymentInvoked` YİNE `true` — FP3_Payment çağrılmıştı. "Çağırdık mı" ile
        // "cihazda oluştu mu" farklı sorular; alanın anlamı kayarsa çift-çekim analizi kör kalır.
        var db = Path.Combine(Path.GetTempPath(), $"nl_{Guid.NewGuid():N}.db");
        using var store = CommandStore.Open(db);
        var clock = new ClockOffset();
        clock.Sync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var sim = new SimulatorTransport()
            .Expect(new TransportResult(TransportOutcome.Unknown,
                ProviderResultCode: "Payment:0x0826", ErrorCondition: "UnreachableHost"));
        sim.ProbeResult = new PaymentProbe(ProbeVerdict.NotLanded, CounterRead: true);

        var orch = new AgentOrchestrator(store, sim, clock, RecoveryPolicy.Immediate);
        var sonuc = await orch.HandleAsync(
            new SaleRequest("c-nl", "p-nl", "t1", 990, "TRY", 2, "prov"),
            clock.ServerNow() + 60_000);

        Assert.Equal(AgentDecision.RetryLater, sonuc.Decision);
        Assert.Equal("PaymentRestriction", sonuc.Result?.ErrorCondition);
        Assert.Equal(RestomenumReasons.NotLanded, sonuc.Result?.Reason);
        Assert.True(sonuc.Result?.PaymentInvoked);
    }

    [Fact]
    public void Odeme_oncesi_sebeplerde_paymentInvoked_MUTLAKA_false()
    {
        // Platform tutarlılık kontrolü (P16): ödeme-öncesi bir sebeple birlikte
        // `paymentInvoked:true` gelirse sonuç REDDEDİLİR + alarm. Bu test o sözleşmeyi bizim
        // tarafımızda çivileyerek alarmın hiç çalmamasını sağlar.
        var oncesi = new[]
        {
            RestomenumReasons.FiscalLinesRequired, RestomenumReasons.ProductUnmapped,
            RestomenumReasons.AlreadyFiscalized, RestomenumReasons.TicketAlreadyOpen,
            RestomenumReasons.PaymentMethodUnmapped, RestomenumReasons.ProviderConfigIncomplete,
            RestomenumReasons.AmountFetchFailed,
        };
        foreach (var sebep in oncesi)
        {
            var ek = Ek(SaleToPoiResponseBuilder.BuildFailure(Istek(), "PaymentRestriction", "X", Simdi, sebep));
            Assert.False(ek.GetProperty("paymentInvoked").GetBoolean());
            Assert.Equal(sebep, ek.GetProperty("reason").GetString());
        }
    }

    // ── ONAY GERİ ÇEKME ─────────────────────────────────────────────────────────

    [Fact]
    public void Geri_cekme_govdesi_ONAYI_KALDIRIR_ama_tersini_IDDIA_ETMEZ()
    {
        // 2026-09-07: bir komut kurtarma turunda BAŞKA bir satışın ödemesini sahiplenip kendini
        // onaylı ilan etti; deftere olmayan bir tahsilat yazıldı. Onay bir kez deftere girince
        // sessizce düzeltilemez — platformun çelişki olarak işleyip operatöre çıkarması gerekir.
        // ← ÇİVİ: `Failure` + `InProgress`. Geri çekmek, "kesinlikle olmadı" demek DEĞİL:
        // "artık onaylı demiyorum, yerine kesin bir şey de diyemiyorum".
        var govde = SaleToPoiResponseBuilder.BuildLandedRetraction(
            "svc1", "kasa-1", "term-01", "pay_9e6374de37dcbfe3eca3e34143bd6c85e2e7e053", Simdi);
        var resp = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("PaymentResponse");

        Assert.Equal("Failure", resp.GetProperty("Response").GetProperty("Result").GetString());
        Assert.Equal("InProgress", resp.GetProperty("Response").GetProperty("ErrorCondition").GetString());
        // Geri çekilen ZATEN tutar iddiasıydı; yeniden göndermek çelişki olurdu.
        Assert.False(resp.GetProperty("PaymentResult").TryGetProperty("AmountsResp", out _));

        var ek = JsonDocument.Parse(govde).RootElement
            .GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
        Assert.Equal(RestomenumReasons.LandedRetracted, ek.GetProperty("info").GetString());
        // ← ÇİVİ: `FP3_Payment` gerçekten çağrılmıştı. Geri çekilen "para hareket etti" iddiası;
        // "çağırdık mı" sorusunun cevabı değişmedi.
        Assert.True(ek.GetProperty("paymentInvoked").GetBoolean());
    }

    private static JsonElement Ek(string govde) =>
        JsonDocument.Parse(govde).RootElement.GetProperty("SaleToPOIResponse").GetProperty("Restomenum");
}
