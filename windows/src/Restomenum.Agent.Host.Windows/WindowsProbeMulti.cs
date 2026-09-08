using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Restomenum.Agent.Core;
using Restomenum.Agent.Gmp;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <b>W42b adım 1 provası</b> (<c>--probe-multi</c>) — toplu komut (<c>prepare_*</c> +
/// <c>FP3_MultipleCommand</c>) cihazda çalışıyor mu, kalem başına kaç bayt, hata dönüşü kalem
/// bazlı mı? <b>Elle, açıkça tetiklenir</b>; üretim akışında ASLA çalışmaz.
///
/// <para><b>Neden ayrı komut:</b> ölçüm için üretim satış yolunu değiştirmek, ölçmek istediğimiz
/// şeyi bozmanın en kolay yolu olurdu. Bu komut satış yolunun tek satırına dokunmuyor.</para>
///
/// <para><b>Ne YAZMAZ:</b> <c>open_ticket</c>, <c>ticket_payments</c>, <c>closed_tickets</c>,
/// <c>commands</c>, <c>outbox</c> — hiçbiri. <see cref="GmpWrapper.ProbeMulti"/> zaten
/// <c>ITicketSnapshotStore</c> ALMIYOR, yani yazma yapısal olarak imkânsız.</para>
///
/// <para><b>Ödeme YAPMAZ:</b> <c>FP3_Payment</c> hiç çağrılmaz. Fişte para olmadığı için provadan
/// kalan fiş <c>VoidAll</c> ile güvenle temizlenebiliyor — mali kayıt oluşmuyor (fiş kapanmıyor).</para>
/// </summary>
public static class WindowsProbeMulti
{
    /// <param name="cihazaGit">
    /// <c>--onayla</c> verilmediyse <c>false</c>: yalnız Faz A (yerel tampon ölçümü) koşar ve
    /// cihaza TEK BİR çağrı bile gitmez. Cihaza gitmek bilinçli bir karar olmalı.
    /// </param>
    public static bool Run(IServiceProvider services, bool cihazaGit)
    {
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("ProbeMulti");
        var gmp = services.GetRequiredService<IGmpWrapper>();

        if (gmp is not GmpWrapper somut)
        {
            log.LogError("prova YALNIZ gerçek GmpWrapper ile çalışır (şu an {Tip}) — çıkılıyor",
                gmp.GetType().Name);
            return false;
        }

        // ── ÖN KOŞULLAR ──────────────────────────────────────────────────────────
        // Biri bile tutmazsa cihaza HİÇ dokunmadan çıkılır. Prova, süren bir işin üstüne
        // binmemeli: bağı olan bir fiş gerçek bir satışın fişidir ve provanın temizleme adımı
        // onu YOK EDERDİ.
        if (cihazaGit)
        {
            var store = services.GetRequiredService<CommandStore>();
            // Terminal kimliği DİSKTEN: `--cancel-ticket` ile aynı kaynak. Uydurulmuş bir kimlikle
            // bağ sorgulamak "bağ yok" der ve gerçek bir fişin üstüne binerdik.
            var terminalId = store.ReadBoundTerminal();
            if (terminalId is not null && store.ReadOpenTicketBinding(terminalId) is string bagli)
            {
                log.LogError("AÇIK FİŞ BAĞI VAR (terminal {T}, oturum {Oturum}) — prova YAPILMADI, cihaza dokunulmadı",
                    terminalId, bagli);
                return false;
            }

            var outbox = services.GetRequiredService<Outbox>();
            var bekleyen = outbox.Pending(limit: 5, ignoreBackoff: true).Count;
            if (bekleyen > 0)
            {
                log.LogError("outbox'ta {Adet} bekleyen bildirim var — prova YAPILMADI (yarım işin üstüne binmeyelim)", bekleyen);
                return false;
            }

            if (!gmp.Echo().Ok)
            {
                log.LogError("Echo BAŞARISIZ — terminale ulaşılamıyor, prova YAPILMADI");
                return false;
            }
        }

        var o = somut.ProbeMulti(cihazaGit, deptIndex: 10, log: m => log.LogInformation("{Satir}", m));

        // ── RAPOR ────────────────────────────────────────────────────────────────
        log.LogInformation("── FAZ A: tampon (cihaza dokunulmadı) ──");
        log.LogInformation("prepare_* dönüş anlamı: {Anlam}", o.PrepareDonusAnlami);
        foreach (var (kalem, donus, dolu) in o.TamponAdimlari)
        {
            var ad = kalem switch { -3 => "prepare_Start", -2 => "prepare_TicketHeader", -1 => "prepare_OptionFlags", _ => $"{kalem}. kalem" };
            log.LogInformation("  {Ad,-22} dönüş={Donus,-8} tamponDolu={Dolu}", ad, donus, dolu);
        }
        log.LogInformation("önek={Onek} bayt · kalem başına≈{Kb:F1} bayt · tampona sığan kalem={Sigan}",
            o.OnekBayt, o.KalemBasinaBayt, o.TamponaSiganKalem);

        if (!o.CihazaGidildi)
        {
            log.LogWarning("FAZ B ATLANDI ({Neden}). Cihazda denemek için: --probe-multi --onayla", o.Temizlik);
            return true;
        }

        log.LogInformation("── FAZ B: cihaz ──");
        log.LogInformation("FP3_MultipleCommand rc={Rc} süre={Ms} ms gönderilen={Bayt} bayt indexOfReturnCodes={Idx}",
            o.MultiRc, o.SureMs, o.GonderilenBayt, o.IndexOfReturnCodes);
        if (o.DonusKodlari.Count == 0) log.LogWarning("dönüş kodu dizisi BOŞ — hata granülerliği okunamıyor");
        foreach (var (sub, ret, tag, idx, len) in o.DonusKodlari)
            log.LogInformation("  altKomut={Sub} rc={Ret} tag={Tag} sıra={Idx} veriUzunluk={Len}", sub, ret, tag, idx, len);

        log.LogInformation("cihazın defteri: kalem={Kalem} toplam={Toplam} ödeme={Odeme}",
            o.FisKalemSayisi, o.FisToplamMinor, o.FisOdemeSayisi);
        log.LogInformation("temizlik: {Temizlik}", o.Temizlik);

        var basarili = o.MultiRc == 0 && o.FisKalemSayisi == 1 && o.FisToplamMinor == 100;
        if (basarili) log.LogInformation("SONUÇ: toplu komut ÇALIŞIYOR — kalem cihazın defterine girdi.");
        else log.LogWarning("SONUÇ: toplu komut beklendiği gibi çalışmadı (rc={Rc}, kalem={Kalem}, toplam={Toplam}).",
            o.MultiRc, o.FisKalemSayisi, o.FisToplamMinor);
        return basarili;
    }
}
