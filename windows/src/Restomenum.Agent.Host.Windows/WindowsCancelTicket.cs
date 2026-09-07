using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>OPERATÖR FİŞ İPTALİ</b> — <c>--cancel-ticket [saleSessionId] --onayla</c> (W26).
///
/// <para><b><c>--void</c>'dan farkı ve neden ayrı bir araç:</b> <c>--void</c> cihaza doğrudan
/// <c>VoidAll</c> atar ve platformu HABERSİZ bırakır. Fiş cihazda gider, defterde tahsilat
/// "APPROVED" olarak kalır ve ikisi kalıcı olarak ıraksar. Bu araç ise kasanın kullandığı YOLUN
/// AYNISINDAN geçer: fiş kapsamlı <c>Reversal</c> → <c>VoidAll</c> → <b>dayanıklı bildirim</b>.
/// Platform satırları kendisi düşürür. Yani "cihazı kurtardım ama defter yanlış kaldı" hâli
/// oluşmaz.</para>
///
/// <para><b>Ne zaman gerekir:</b> kasa iptali gönderemiyorsa (uygulama çökmüş, oturum yok, kasadaki
/// bir hata komutu üretmiyor) ve cihazda fiş kilitli kaldıysa. 7 Eylül 2026'da tam olarak bu yaşandı:
/// kasa üç kez "iptal" dedi, komut hiç çıkmadı, fiş 50 dakika açık kaldı.</para>
///
/// <para><b>Bağ ŞART.</b> Yalnız <c>open_ticket</c> bağı olan, yani BU ajanın açtığı bilinen fiş
/// iptal edilir. Bağsız bir fiş (başka kasadan kalmış olabilir) burada iptal EDİLMEZ: sahibini
/// bilmediğimiz bir fişin satırlarını platformun hangi oturumdan düşüreceği bilinemez. Onun aracı
/// hâlâ <c>--void --onayla</c> ve sonucu operatörün elle mutabakatıdır.</para>
///
/// <para><b><c>--onayla</c> olmadan HİÇBİR ŞEY yapılmaz</b> — yalnız ne iptal edileceği yazdırılır.
/// Mali kayıt siliniyor; kararın insanda olması gerekiyor.</para>
/// </summary>
public static class WindowsCancelTicket
{
    public static async Task<bool> RunAsync(IServiceProvider services, string? saleSessionId, bool operatorOnayi)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("CancelTicket");
        var store = services.GetRequiredService<CommandStore>();
        var handler = services.GetRequiredService<LocalSaleHandler>();

        // ── 1. BAĞ: neyi iptal edeceğimizi cihazdan değil KENDİ defterimizden öğren ──────
        var terminalId = store.ReadBoundTerminal();
        if (terminalId is null)
        {
            log.LogError("Bağlı açık fiş YOK. Bu araç yalnız bu ajanın açtığı, sahibi bilinen fişi " +
                "iptal eder. Cihazda başka bir kasadan kalmış fiş varsa: --void --onayla.");
            return false;
        }

        var bagliOturum = store.ReadOpenTicketBinding(terminalId);
        var fisId = store.ReadOpenTicketId(terminalId);
        if (bagliOturum is null)
        {
            log.LogError("terminal {Terminal} için bağ okunamadı — iptal edilmedi.", terminalId);
            return false;
        }

        // Argüman verilmişse EŞLEŞMELİ. Operatör masa-9'u iptal ettiğini sanırken masa-1'i
        // iptal etmemeli; yanlış fişin mali kaydını silmek geri alınamaz.
        if (saleSessionId is not null
            && !string.Equals(saleSessionId, bagliOturum, StringComparison.Ordinal))
        {
            log.LogError("İSTENEN oturum ({Istenen}) cihazdaki açık fişin sahibi DEĞİL ({Gercek}) — " +
                "iptal edilmedi.", saleSessionId, bagliOturum);
            return false;
        }

        // ── 2. ONAY KAPISI: neyin silineceğini YAZ, sonra dur ────────────────────────────
        var odemeler = fisId is null ? Array.Empty<TicketPaymentRow>() : store.ReadTicketPayments(fisId);
        var toplam = odemeler.Sum(x => x.AmountMinor);
        log.LogWarning("İPTAL EDİLECEK FİŞ: terminal={Terminal} oturum={Oturum} fisId={FisId} " +
            "kayıtlı ödeme={Adet} toplam={Toplam} kuruş",
            terminalId, bagliOturum, fisId ?? "(yok)", odemeler.Count, toplam);
        foreach (var o in odemeler)
            log.LogWarning("  · {PaymentId} {Tutar} kuruş tip={Tip} banka={Banka}",
                o.PaymentId, o.AmountMinor, o.MethodType, o.BankBkmId?.ToString() ?? "-");

        if (!operatorOnayi)
        {
            log.LogWarning("ONAY YOK — hiçbir şey yapılmadı. Gerçekten iptal etmek için: " +
                "--cancel-ticket {Oturum} --onayla", bagliOturum);
            return false;
        }

        // ── 3. KASANIN YOLUNDAN GEÇ: iptal + DAYANIKLI bildirim ─────────────────────────
        // Komut kimliği operatör kaynağını taşır (`-op-`): defterde bu iptalin kasadan değil
        // buradan geldiği görülebilmeli, yoksa "kasa iptal göndermiş" diye yanlış okunur.
        var damga = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var req = new ReversalRequest(
            ServiceId: Guid.NewGuid().ToString("N")[..10],
            SaleId: "operator",
            PoiId: terminalId,
            PaymentId: null,                       // fiş iptali bir denemeye ait değil
            OriginalPoiTransactionId: null,
            OriginalServiceId: null,
            ReversalReason: "MerchantCancel",
            TimeStamp: DateTimeOffset.UtcNow,
            Scope: "ticket",
            SaleSessionId: bagliOturum,
            TicketCancelId: $"{bagliOturum}-{terminalId}-ticketcancel-op-{damga}");

        var govde = await handler.HandleReversalAsync(req);
        log.LogInformation("iptal sonucu gövdesi: {Govde}", govde);

        // Sonucu gövdeden değil, cihazın kendi defterinden teyit et: bağ silinmişse fiş gitmiştir.
        var kalanBag = store.ReadOpenTicketBinding(terminalId);
        if (kalanBag is null)
        {
            log.LogInformation("FİŞ İPTAL EDİLDİ ve bağ temizlendi. Bildirim outbox'a yazıldı; " +
                "gidemediyse arka plan replay eder.");
            return true;
        }

        log.LogError("Bağ HÂLÂ duruyor ({Bag}) — iptal tamamlanmamış olabilir. Gövdeyi okuyun; " +
            "TEKRAR DENEMEYİN, yarım kalmış bir ters işlemin üstüne ikincisini bindirmeyin.", kalanBag);
        return false;
    }
}
