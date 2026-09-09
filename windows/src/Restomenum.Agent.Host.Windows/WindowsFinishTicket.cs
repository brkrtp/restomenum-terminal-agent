using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>YARIM KALMIŞ FİŞİ TAMAMLA</b> (<c>--finish-ticket</c>) — baskı adımı patladığı için cihazda
/// AÇIK kalmış, üzerinde GERÇEK TAHSİLAT olan bir fişi üreticinin kurtarma akışıyla kapatır.
///
/// <para><b>Neden ayrı bir komut, <c>--void</c>'e ek dal değil:</b> <c>--void</c>'in sert tabanı
/// "tahsilat varsa ASLA dokunma"dır ve o kural doğrudur — orada yapılacak iş <c>VoidAll</c>, yani
/// parayı geri vermek. Buradaki iş TAM TERSİ: parayı korumak, fişi TAMAMLAMAK. İki karşıt niyeti
/// aynı bayrağın altına koymak, birinin koruması gevşetilirken diğerinin de gevşemesi demekti.</para>
///
/// <para><b><c>--onayla</c> olmadan CİHAZA BASKI YAPILMAZ.</b> Onaysız çağrı fişi OKUR ve ne
/// yapacağını yazar (kuru prova). Okuma fişi değiştirmez.</para>
///
/// <para><b><c>VoidAll</c> / <c>VoidPayment</c> bu yolda YOK.</b> Fişte tanım gereği tahsilat
/// vardır; ters işlem müşterinin parasını geri vermek olurdu.</para>
/// </summary>
public static class WindowsFinishTicket
{
    public static bool Run(IServiceProvider services, bool operatorOnayi)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("FinishTicket");
        var gmp = services.GetRequiredService<IGmpWrapper>();
        var defter = services.GetRequiredService<ITicketSnapshotStore>();

        // Eşleşme SÜRECE bağlı (ölçüldü: süreç bitince düşer, Start → 0xF020). Önce kurulmalı.
        log.LogInformation("yarım fiş tamamlama: eşleşme kuruluyor (süreç-bağlı, önce şart)...");
        if (!WindowsPairing.Run(services))
        {
            log.LogError("eşleşme kurulamadı — fişe dokunulmadı. (Terminal ulaşılabilir mi?)");
            return false;
        }

        var startRc = gmp.Start(out var handle);

        if (startRc.Ok)
        {
            // Açık fiş YOKTU; probe YENİ fiş açtı. Açık bırakmak sonraki satışı bozar.
            gmp.Close(handle);
            log.LogInformation("terminalde açık fiş YOK — tamamlanacak bir şey yok (probe fişi kapatıldı).");
            return true;
        }

        if (startRc.Code != GmpCodes.AlreadyDone)
        {
            log.LogError("fiş durumu okunamadı (Start rc={Rc}) — terminal meşgul ya da eşleşme yok olabilir.",
                startRc.Code);
            return false;
        }

        log.LogWarning("terminalde AÇIK fiş var (handle={Handle}) — içeriği ve bayrakları okunuyor.", handle);

        var sonuc = TicketFinishRunner.Calistir(gmp, handle, uygula: operatorOnayi,
            log: (m, v) => log.LogInformation("{Mesaj} {Veri}", m, v));

        // HAM bayraklar HER ZAMAN basılır — kuru provada da, başarısızlıkta da. Bu satır olayın
        // tek kalıcı kanıtı: `TransactionFlags` cihaz izinde görünmüyor, yalnız yanıtta geliyor.
        log.LogWarning("HAM TransactionFlags = {Bayrak}", TicketFlags.Yazdirilabilir(sonuc.Bayraklar));

        if (sonuc.Plan.Adim == FisBitirmeAdimi.Dokunma)
        {
            log.LogError("DOKUNULMADI — sebep={Neden} (üretici akışı adım {Adim}). " +
                "Ödemesi tamamlanmamış bir fiş BASILIP KAPATILMAZ: kalanı önce kasadan tahsil edin " +
                "(üretici §2 adım 7), sonra bu komutu tekrar çalıştırın.",
                sonuc.Plan.Neden, sonuc.Plan.UreticiAdimi);
            return false;
        }

        if (!operatorOnayi)
        {
            log.LogWarning("KURU PROVA — cihaza baskı YAPILMADI. Plan: {Plan} (üretici akışı adım {Adim}). " +
                "Uygulamak için: --finish-ticket --onayla", sonuc.Plan.Adim, sonuc.Plan.UreticiAdimi);
            return true;
        }

        if (!sonuc.Kapandi)
        {
            log.LogError("FİŞ KAPANMADI — {Adim} adımı {Kod} döndü. Fiş AÇIK bırakıldı (tekrar denenebilir). " +
                "Yazıcıyı ve ruloyu kontrol edin. FİŞ İPTAL YAPMAYIN — para alınmış durumda.",
                sonuc.BasarisizAdim, sonuc.Kod);
            return false;
        }

        // ── CİHAZDA KAPANDI ≠ İŞ BİTTİ ────────────────────────────────────────────
        // Fişi cihazda kapatmak masayı AÇMAZ. Platform bir fişin ödemelerini ancak "kapandı"
        // bildirimini alınca deftere yazar; bildirim gitmezse tahsilat deftersiz kalır ve kasa
        // masayı kapatamaz — yani kullanıcının gördüğü arıza AYNEN sürer.
        //
        // Bildirimi bu komut GÖNDERMEZ (ağ/oturum işi ajanın worker'ında). Bunun yerine ajanın
        // ZATEN VAR OLAN kurtarma süpürmesinin (`LocalSaleHandler.KayipKapanislariTopla`)
        // beslendiği kalıcı kayıt yazılır: `closed_tickets`'a `notified_at IS NULL` satırı.
        // Ajan bir sonraki turunda gövdeyi kurup outbox'a koyar. Yeni bir bildirim yolu
        // yazmıyoruz — ikinci bir yol, iki ayrı "kapanış nasıl bildirilir" tanımı demekti.
        var bag = defter.AllOpenTicketBindings();
        if (bag.Count == 0)
        {
            log.LogWarning("✓ FİŞ KAPANDI (üretici akışı adım {Adim}) — ama defterde bu terminale ait " +
                "AÇIK FİŞ BAĞI YOK. Kapanış platforma BİLDİRİLEMEDİ: hangi fiş/oturum olduğu " +
                "bilinmiyor ve uydurulmaz. Cihaz satışa hazır, kasa masayı elle kapatmalı.",
                sonuc.Plan.UreticiAdimi);
            return true;
        }
        if (bag.Count > 1)
        {
            // Cihazda tek fiş açıktı ama defterde birden çok bağ var — hangisinin kapandığını
            // söyleyemeyiz. Yanlış fişin ödemelerini deftere yazmak, doğru fişi bildirmemekten
            // daha kötüdür (başka bir masanın tahsilatı bu masaya geçerdi).
            log.LogError("✓ FİŞ KAPANDI ama defterde {Sayi} AÇIK BAĞ var — hangisinin kapandığı " +
                "BELİRSİZ, hiçbiri bildirilmedi. Bağlar: {Bag}",
                bag.Count, string.Join(", ", bag.Select(x => $"{x.TerminalId}/{x.TicketId}")));
            return false;
        }

        var (terminalId, oturum, fisId) = bag[0];
        if (fisId is null)
        {
            log.LogWarning("✓ FİŞ KAPANDI ama bağda FİŞ KİMLİĞİ yok — kapanış bildirilemedi.");
            return true;
        }

        // Tutarlar cihazın SON okumasından; `Calistir` kapatmadan hemen önce okumuştu.
        // Defter işini TAŞIMAYLA AYNI kod yapar — iki ayrı "kapanış nasıl deftere geçer"
        // tanımı, biri düzeltilirken diğerinin eskimesi demekti.
        TicketFinishLedger.Yaz(defter, fisId, terminalId, oturum, sonuc.ToplamMinor, sonuc.TahsilMinor);

        log.LogInformation("✓ FİŞ KAPANDI (üretici akışı adım {Adim}). Deftere yazıldı: " +
            "fiş={Fis} oturum={Oturum} toplam={Toplam} tahsil={Tahsil}. Kapanış bildirimi ajanın " +
            "kurtarma süpürmesine devredildi — ajanı yeniden başlatın, masa o turda açılır.",
            sonuc.Plan.UreticiAdimi, fisId, oturum, sonuc.ToplamMinor, sonuc.TahsilMinor);
        return true;
    }
}
