namespace Restomenum.Agent.Core;

/// <summary>
/// <b>SATIŞ ÇAĞRISININ TOPLAM SÜRE BÜTÇESİ</b> (W109).
///
/// <para><b>Ölçülmüş sorun (2026-09-10):</b> kasanın POST zaman aşımı <b>180 sn</b>
/// (<c>LOCAL_TIMEOUT_MS</c>). Ajanın adım adım zaman aşımları ise toplamı kimse tutmadan
/// yan yana duruyordu:</para>
/// <code>
///   Payment                  90 000 ms
///   PrintTotalsAndPayments  100 000 ms
///   PrintBeforeMF           100 000 ms
///   PrintMF                 100 000 ms × ÜÇ DENEME
///   Close                    10 000 ms
///   ------------------------------------
///   en kötü toplam          600 000 ms  = 600 sn
/// </code>
/// <para>Yani kasa ÖNCE pes eder: <c>transportError</c> görür, deneme belirsiz kalır ve
/// uzlaştırma turuna düşer — biz ise devam edip başarırız. Sonuç: <b>kasa "bilinmiyor", cihazda
/// para alınmış.</b> Bu, düzeltmeye çalıştığımız arızadan daha kötü.</para>
///
/// <para><b>Bu kapı W104'ün açtığı kapı DEĞİL.</b> W104 öncesi de toplam 420 sn'ydi; en büyük tek
/// kalem <c>PrintMF</c>'in üç denemesi (300 sn). W104 kapıyı genişletti, açmadı.</para>
///
/// <para><b>Neden ÖDEME bütçeye alınmıyor:</b> <c>FP3_Payment</c> insanla etkileşimdir — kart,
/// PIN, banka. Onu kısaltmak müşteri kart okuturken işlemi kesmek demektir ve <b>belirsizlik
/// üretir</b> (para gitti mi bilinmez). Bütçe yalnız BASKI zincirini kısıtlar; baskı kesilirse
/// para çoktan alınmıştır ve fiş açık kalır — belirsizlik DEĞİL, bilinen bir aradurum, üstelik
/// kurtarması var (W105).</para>
///
/// <para><b>Neden <see cref="Environment.TickCount64"/>:</b> bu bir SÜRE ölçümü, saat değil.
/// Duvar saati NTP ile geri atlayabilir; monotonik sayaç atlamaz. Bütçeyi duvar saatine
/// bağlamak, saat düzeltmesinde bütçeyi anında tüketebilirdi.</para>
/// </summary>
public sealed class BaskiButcesi
{
    /// <summary>
    /// Satış çağrısının TOPLAM bütçesi.
    ///
    /// <para><b>Neden 165 ve 170 değil:</b> ilk yazımda 170 sn seçmiştim ve aritmetik çivi
    /// (<c>BaskiButcesiTests.EN_KOTU_TOPLAM_...</c>) bunu KIRMIZIYA çevirdi: <c>Close</c> bütçeden
    /// muaf ve kendi tavanı 10 sn, yani 170 + 10 = <b>tam 180</b> — kasanın kapısında sıfır pay.
    /// HTTP, serileştirme ve JSON ayrıştırma da o pencerede. 165 + 10 = 175 sn, geriye 5 sn kalır.</para>
    ///
    /// <para>Ödeme en kötü hâlde 90 sn alsa bile baskıya <b>75 sn</b> kalır; sahada ölçülen baskı
    /// süreleri saniyenin altında (13 kapanış üzerinden), yani normal satışta bütçe hiç hissedilmez.</para>
    /// </summary>
    public static readonly TimeSpan Varsayilan = TimeSpan.FromSeconds(165);

    private readonly Func<long> _tik;
    private readonly long _bitis;

    /// <summary>
    /// Sarmalayıcının bir çağrıya uygulayacağı süre: <c>min(çağıranın istediği, kendi tavanı)</c>.
    ///
    /// <para><b>Neden Core'da, sarmalayıcıda değil:</b> <c>Restomenum.Agent.Gmp</c> Windows'a bağlı
    /// P/Invoke katmanı ve <b>test projesi YOK</b>. Bu karar orada özel bir yardımcıyken mutasyon
    /// testi onu YEŞİL geçti — yani "geçirilen süreyi yoksay" hatası hiçbir testte görünmüyordu.
    /// Kararı Core'a almak, davranışı değiştirmeden test edilebilir kılıyor.</para>
    ///
    /// <para>İki taraf da meşru: <b>tavan</b> cihazın davranışına dair ölçümümüz, <b>istenen</b>
    /// kasanın kapısına dair. İkisinin KÜÇÜĞÜ doğru olan. <c>null</c>/sıfır/negatif istenen =
    /// "bütçe yok, tavanı kullan".</para>
    /// </summary>
    public static int Kirp(int? istenen, int tavan) =>
        istenen is int x && x > 0 ? Math.Min(x, tavan) : tavan;

    /// <param name="tik">Monotonik ms sayacı. Testte sabitlenebilir.</param>
    public BaskiButcesi(TimeSpan toplam, Func<long>? tik = null)
    {
        _tik = tik ?? (() => Environment.TickCount64);
        _bitis = _tik() + (long)toplam.TotalMilliseconds;
    }

    /// <summary>Kalan süre (ms). Asla negatif değil.</summary>
    public long KalanMs => Math.Max(0, _bitis - _tik());

    /// <summary>
    /// Bu adıma verilecek zaman aşımı: <c>min(tavan, kalan)</c>.
    ///
    /// <para><c>null</c> = <b>BÜTÇE BİTTİ, ÇAĞIRMA.</b> Sıfır zaman aşımıyla çağırmak yerine hiç
    /// çağırmamak şart: sıfır/negatif zaman aşımının DLL'de ne yaptığı ÖLÇÜLMEDİ ve para taşıyan
    /// bir cihazda tanımsız davranışa girmek kabul edilemez.</para>
    ///
    /// <para><paramref name="asgari"/> altına düşen kalan da <c>null</c> sayılır: 200 ms'lik bir
    /// zaman aşımıyla yazıcı çağırmak, çağırmamaktan farksız ama cihazı yarım bir işe sokar.</para>
    /// </summary>
    public int? Sure(int tavan, int asgari = 1000)
    {
        var kalan = KalanMs;
        return kalan < asgari ? null : (int)Math.Min(tavan, kalan);
    }
}

/// <summary>
/// <b>W116 — periyodik tur sıklığı.</b> Sabit Core'da çünkü <c>AgentWorker</c> Host projesinde
/// ve <b>test projesi yok</b>; oradayken mutasyon ("turu tekrar 30 sn yap") YEŞİL geçti.
/// </summary>
public static class AjanTuru
{
    /// <summary>
    /// Drain/süpürme turunun sıklığı. ⚠️ Turun KENDİSİ ucuz: boşta üç küçük yerel SQLite
    /// sorgusu. Sıklığı artırmak ağ trafiğini artırmaz — hem outbox kayıtlarının hem yarım
    /// fişlerin KENDİ <c>next_attempt_at</c>'i var; tur yalnız sırası GELMİŞ işi görür.
    /// </summary>
    public static readonly TimeSpan DrainAraligi = TimeSpan.FromSeconds(10);
}
