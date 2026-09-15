namespace Restomenum.Agent.Core;

/// <summary>
/// <b>YARIM KALMIŞ FİŞİ TAMAMLAMA</b> — <see cref="GmpTerminalTransport"/>'un parçası (W105).
///
/// <para>Baskı zinciri patladığında fiş cihazda ÜZERİNDE PARAYLA açık kalır. Bugüne kadar bunu
/// tekrar deneyecek hiçbir yol yoktu: ajan çalışmaya devam ediyordu, kasa masayı kapatamıyordu
/// ve arıza insan müdahalesine kadar sürüyordu (2026-09-09 23:07 → 00:30, <b>83 dakika</b>).</para>
///
/// <para><b>Bu dosyada <c>VoidAll</c> ve <c>VoidPayment</c> GEÇMEZ.</b> Kararı
/// <see cref="TicketFinish.Planla"/> veriyor ve orada da geçmiyor; tanım gereği bu fişlerde
/// müşteriden alınmış para var.</para>
/// </summary>
public sealed partial class GmpTerminalTransport : IStuckTicketFinisher
{
    /// <summary>
    /// Cihazdaki açık fişi üreticinin kurtarma akışıyla tamamlar ve kapandıysa deftere yazar.
    ///
    /// <para><b>Eşzamanlılık çağıranın işi:</b> bu metot kendi kilidini almaz. Süpürme turu
    /// cihaz kilidini <c>Wait(0)</c> ile alır (satış her zaman öncelikli); elle çağrıda
    /// (<c>--finish-ticket</c>) ajan zaten durmuştur.</para>
    /// </summary>
    public StuckTicketResult FinishStuckTicket(bool uygula, BaskiButcesi? butce = null)
    {
        var r = _gmp.Start(out var handle);

        if (r.Ok)
        {
            // Açık fiş YOKTU — probe YENİ fiş açtı. Açık bırakmak sonraki satışı bozar.
            _gmp.Close(handle);
            lock (_gate) _handle = 0;
            return new StuckTicketResult(false, FisAcikDegil: true, Meshgul: false,
                new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "TICKET_NOT_OPEN", 0), 0);
        }

        if (r.Code != GmpCodes.AlreadyDone)
        {
            // Meşgul / cevapsız. Fiş hakkında HİÇBİR ŞEY öğrenilmedi; "başarısız" demek yanlış
            // olurdu — denenmedi bile. Çağıran bunu alarm yükseltmeden erteler.
            _log("[tamamla] cihaz cevap vermedi — fişe dokunulmadı", new { code = r.ToString() });
            return new StuckTicketResult(false, FisAcikDegil: false, Meshgul: true,
                new FisBitirmePlani(FisBitirmeAdimi.Dokunma, "DEVICE_BUSY", 0), 0,
                "Start", r.ToString());
        }

        var s = TicketFinishRunner.Calistir(_gmp, handle, uygula, _log, butce);

        // ── W122: KURTARMA UYARIYI **DÜŞÜRMEZ** ─────────────────────────────────
        // ⚠️ W120'de "uyarıyı yalnız başarılı bir baskı düşürür" demiş ve kurtarmayı da o
        // sayıya katmıştım. Kural kendi içinde tutarlıydı ama SONUCU YANLIŞTI ve saha
        // gösterdi (2026-09-10 04:20):
        //     04:20:03  baskı patladı (0x0020) → uyarı AÇILDI
        //     04:20:09  kurtarma başarıyla bastı → uyarı KAPANDI
        //     toplam ömür: **5,8 saniye**. Panel görmedi, kullanıcı görmedi.
        // Uyarıyı DOĞURAN olayı çözen baskının o uyarıyı SİLMESİ saçma: aynı olayın içinde
        // doğup ölüyor ve insana hiç ulaşmıyor.
        //
        // ⚠️ Üstelik "başarılı baskı = kâğıt var" da DOĞRU DEĞİL: rulo çıkıkken `PrintBeforeMF`
        // 4 saniye sonra başarılı oldu (aynı ölçüm). Yani kurtarmanın başarısı kâğıdı
        // KANITLAMIYOR bile.
        //
        // Yeni kural: kurtarma uyarıyı yalnız AÇAR, hiç kapatmaz. Kapatan tek şey
        // **bir sonraki OLAĞAN satışın tam zinciri** (`GmpTerminalTransport.Sale`) — yani
        // normal işleyişin geri döndüğünün kanıtı. Uyarı o zamana kadar insanı bekler.
        // ⚠️ Kuru prova (`uygula: false`) buraya zararsız düşer ve AYRI bir koruma gerektirmez:
        // o yolda hiçbir baskı çağrılmaz, dolayısıyla `Kapandi` false ve `Kod` null olur —
        // yani ne düşürme ne açma dalı çalışır. (Mutasyon testi bunu gösterdi: `uygula &&`
        // eklemek davranışı DEĞİŞTİRMİYORDU, yani bir kapı değil gürültüydü.)
        //
        // ⚠️ TERMİNAL KİMLİĞİ `_terminalId`'DEN ALINMAZ — o alan YALNIZ bu süreçte bir satış
        // geçtiyse dolu. Test bunu yakaladı: ajan yeniden başladıktan sonra süpürme çalışırsa
        // `_terminalId` BOŞ olur ve kâğıt uyarısı başarılı kurtarmaya rağmen HİÇ DÜŞMEZ —
        // rozet sonsuza kadar açık kalırdı. Kimlik defterden okunur (bağ ya da tek terminal).
        var kagitTerminali = _terminalId.Length > 0 ? _terminalId : TekTerminal();
        if (_snapshots is { } d && kagitTerminali is not null)
        {
            if (s.Kod == $"0x{GmpCodes.NoPaper:X4}")
                d.SetPaperWarning(kagitTerminali, true, s.BasarisizAdim);
        }

        if (!s.Kapandi)
            return new StuckTicketResult(false, false, Meshgul: false, s.Plan, s.Bayraklar,
                s.BasarisizAdim, s.Kod, s.ToplamMinor, s.TahsilMinor);

        lock (_gate) _handle = 0;

        // ── CİHAZDA KAPANDI ≠ İŞ BİTTİ ────────────────────────────────────────────
        // Platform bir fişin ödemelerini ancak "kapandı" bildirimini alınca deftere yazar.
        // Bildirim gitmezse tahsilat deftersiz kalır ve kasa masayı kapatamaz — yani
        // kullanıcının gördüğü arıza AYNEN sürer.
        var fisId = TekBag(out var terminalId, out var oturum);
        if (fisId is null)
            return new StuckTicketResult(true, false, false, s.Plan, s.Bayraklar,
                s.BasarisizAdim, s.Kod, s.ToplamMinor, s.TahsilMinor);

        if (_snapshots is { } defter)
            TicketFinishLedger.Yaz(defter, fisId, terminalId!, oturum, s.ToplamMinor, s.TahsilMinor);

        // ══ W148 — KURTARMANIN KAPATTIĞI FİŞİN DE MALİ NUMARASI YAZILSIN ═══════════
        //
        // W144/W145 numarayı yalnız OLAĞAN satış yoluna koymuştu; süpürmenin ya da elle
        // `TicketFinish`'in kapattığı fişler numarasız kalıyordu. Oysa denetimde ya da bir
        // itirazda aranacak fişler tam olarak onlar: arıza yaşamış, kurtarılmış olanlar.
        //
        // ⚠️ SIRA, W145'in dersi: kalıcı defter işi (`TicketFinishLedger.Yaz`) ÖNCE. Bu okuma
        // bir ZENGİNLEŞTİRME; patlarsa kurtarmanın kendisi etkilenmemeli — çünkü buraya
        // geldiysek fiş cihazda KAPANMIŞ ve para çoktan alınmıştır.
        //
        // ⚠️ try/catch: `FP3_Echo` yanıtı JSON'dan çözülüyor; beklenmedik bir değer
        // ayrıştırıcıyı fırlatabilir ve tamamlanmış bir kurtarmayı patlatamaz.
        FiscalReceiptId? maliFis = null;
        try
        {
            if (_gmp.Echo(out var sayac).Ok && sayac.FisNo > 0)
            {
                // FisNo SONRAKİ numaradır; W151: üçlü birlikte (numara Z'de sıfırlanır).
                maliFis = new FiscalReceiptId(sayac.FisNo - 1, sayac.ZNo, sayac.EkuNo);
                _snapshots?.SetFiscalReceipt(fisId, maliFis);
                _log("[bitir] mali fiş numarası", new
                {
                    fisNo = maliFis.ReceiptNo, okunanSonraki = sayac.FisNo, zNo = sayac.ZNo, ekuNo = sayac.EkuNo, fisId,
                });
            }
            else
            {
                _log("[bitir] mali fiş numarası OKUNAMADI — alan boş bırakıldı", new { fisId });
            }
        }
        catch (Exception e)
        {
            _log("[bitir] mali fiş numarası okunurken HATA — kurtarma etkilenmedi", new
            {
                fisId, hata = e.GetType().Name,
            });
        }

        return new StuckTicketResult(true, false, false, s.Plan, s.Bayraklar,
            s.BasarisizAdim, s.Kod, s.ToplamMinor, s.TahsilMinor, fisId, maliFis);
    }

    /// <summary>
    /// Kâğıt uyarısının yazılacağı terminal — <c>_terminalId</c> boşken (bu süreçte henüz satış
    /// geçmemiş) defterdeki açık bağdan okunur. Birden çok bağ varsa <c>null</c>: yanlış
    /// terminale sağlık bilgisi yazmaktansa hiç yazmamak doğru.
    /// </summary>
    private string? TekTerminal()
    {
        if (_snapshots is not { } defter) return null;
        var baglar = defter.AllOpenTicketBindings();
        return baglar.Count == 1 ? baglar[0].TerminalId : null;
    }

    /// <summary>
    /// Kapanan fişin defterdeki karşılığı. <c>null</c> = güvenilir biçimde belirlenemedi.
    ///
    /// <para><b>Neden defterden okunuyor, terminal kimliği tahmin edilmiyor:</b> cihaz BİZİM fiş
    /// kimliğimizi bilmez, dolayısıyla "az önce kapanan fiş hangisiydi" sorusunun tek kaynağı
    /// açık fiş bağıdır. Birden çok bağ varsa hangisinin kapandığı BELİRSİZDİR ve yanlış fişin
    /// ödemelerini deftere yazmak, hiç yazmamaktan kötüdür — başka bir masanın tahsilatı bu
    /// masaya geçerdi.</para>
    /// </summary>
    private string? TekBag(out string? terminalId, out string? oturum)
    {
        terminalId = null;
        oturum = null;
        if (_snapshots is not { } defter) return null;

        var baglar = defter.AllOpenTicketBindings();
        if (baglar.Count == 0)
        {
            _log("[tamamla] fiş kapandı ama defterde AÇIK BAĞ YOK — kapanış bildirilemedi", null);
            return null;
        }
        if (baglar.Count > 1)
        {
            _log("[tamamla] fiş kapandı ama defterde BİRDEN ÇOK bağ var — hiçbiri bildirilmedi",
                new { sayi = baglar.Count, bag = string.Join(", ", baglar.Select(x => x.TerminalId)) });
            return null;
        }

        var (t, o, f) = baglar[0];
        terminalId = t;
        oturum = o;
        return f;
    }
}
