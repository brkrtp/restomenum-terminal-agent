namespace Restomenum.Agent.Core;

/// <summary>
/// Cihazda AÇIK kalmış, üzerinde GERÇEK TAHSİLAT olan fişi tamamlayabilen taşıma.
///
/// <para><b>Neden ayrı arayüz, <see cref="ITerminalTransport"/>'a üye değil:</b> bu yetenek
/// GMP-3'e özgü. <c>SimulatorTransport</c> ve gelecekteki pazarlar için anlamı yok ve onlara
/// boş gövde yazdırmak, "bu taşıma yarım fiş tamamlayabilir" iddiasını sessizce herkese
/// yaymak olurdu. Uygulamayanlar için süpürme kendiliğinden devre dışı kalır.</para>
/// </summary>
public interface IStuckTicketFinisher
{
    /// <summary>
    /// Cihazdaki açık fişi üreticinin kurtarma akışıyla tamamlar ve — kapandıysa — deftere yazar.
    /// </summary>
    /// <param name="uygula"><c>false</c> = KURU PROVA: okur, planı döner, cihazı DEĞİŞTİRMEZ.</param>
    /// <param name="butce">
    /// Süre bütçesi. <c>null</c> = sınırsız — elle kurtarmada (<c>--finish-ticket</c>) ajan
    /// durmuştur ve kimse cevap beklemez. Kasadan tetiklenen tamamlamada ZORUNLU: kurtarma
    /// zinciri de 310 sn'ye çıkabilir ve kasanın 180 sn'lik kapısını aşardı.
    /// </param>
    StuckTicketResult FinishStuckTicket(bool uygula, BaskiButcesi? butce = null);
}

/// <param name="FisAcikDegil">
/// Cihazda açık fiş YOKTU. <b>Başarısızlık değil</b>: fiş başka bir yoldan kapanmış olabilir
/// (kasiyer, önceki bir tur). Çağıran bekleyen kaydı düşürür — "kapandı" diye değil,
/// "artık ortada bir şey yok" diye.
/// </param>
/// <param name="Meshgul">
/// Cihaz meşgul / cevapsız (<c>RECV_BUSY</c>, zaman aşımı, port kapalı). Denemenin BAŞARISIZ
/// olmasından AYRI tutulur: burada fiş hakkında hiçbir şey öğrenilmedi, dolayısıyla alarm
/// yükseltmek için de bir sebep yok — yalnız daha sonra tekrar denenir.
/// </param>
public readonly record struct StuckTicketResult(
    bool Kapandi, bool FisAcikDegil, bool Meshgul,
    FisBitirmePlani Plan, uint Bayraklar,
    string? BasarisizAdim = null, string? Kod = null,
    long? ToplamMinor = null, long? TahsilMinor = null,
    string? TicketId = null,

    /// <param name="FiscalReceipt">
    /// <b>W148/W151</b> — kurtarmanın kapattığı fişin MALİ KİMLİĞİ; <c>null</c> = okunamadı.
    /// Olağan satış yolundaki <see cref="TransportResult.FiscalReceipt"/> ile aynı anlam,
    /// aynı kural: <c>FisNo − 1</c>, üçlü birlikte, ve okunamazsa UYDURULMAZ.
    /// </param>
    FiscalReceiptId? FiscalReceipt = null);

/// <summary>
/// Fiş tamamlandıktan sonraki DEFTER işi — cihazda kapatmak masayı AÇMAZ.
///
/// <para><b>Neden ayrı ve paylaşılan:</b> aynı iş iki yerden yapılıyor (elle <c>--finish-ticket</c>
/// ve otomatik süpürme). İkisine ayrı ayrı yazsaydık, biri düzeltilirken diğeri eskir ve
/// "kapanış nasıl deftere geçer" sorusunun iki farklı cevabı olurdu.</para>
/// </summary>
public static class TicketFinishLedger
{
    /// <summary>
    /// <c>closed_tickets</c>'a "kapandı, bildirilmedi" satırı yazar, bağı ve bekleyen tamamlama
    /// kaydını siler. <b>Bildirimi GÖNDERMEZ</b> — onu ajanın var olan kurtarma süpürmesi
    /// (<c>KayipKapanislariTopla</c>) yapar. İkinci bir bildirim yolu açmak, iki ayrı "kapanış
    /// nasıl bildirilir" tanımı demekti.
    /// </summary>
    public static void Yaz(ITicketSnapshotStore defter, string ticketId, string terminalId,
        string? saleSessionId, long? toplamMinor, long? tahsilMinor)
    {
        // SIRA ÖNEMLİ: önce kalıcı "kapandı" kaydı, SONRA bağın silinmesi. Ters sırada, ikisi
        // arasında süreç ölürse fişi bildirecek KİMLİK kalmaz ve tahsilat deftersiz kalırdı.
        defter.MarkTicketClosed(ticketId, terminalId, saleSessionId, toplamMinor, tahsilMinor);
        defter.ClearOpenTicketBinding(terminalId);
        defter.ClearPendingFinish(ticketId);
    }
}
