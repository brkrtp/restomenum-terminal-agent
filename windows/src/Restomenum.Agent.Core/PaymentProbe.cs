namespace Restomenum.Agent.Core;

/// <summary>
/// Bir ödemenin akıbeti sorgusunun sonucu.
///
/// <para><see cref="RemainingMinor"/> <b>arıza göstergesi değildir</b>: artımlı modelde sıfırdan
/// büyük kalması beklenen durumdur (kasiyer kalanı ekleyecek). Tek-seferlik modelde ise sıfırdan
/// büyük kalması incelenmesi gereken bir durumdur.</para>
/// </summary>
public sealed record PaymentProbe(
    ProbeVerdict Verdict,
    long? ApprovedAmountMinor = null,
    long RemainingMinor = 0,
    string? Rrn = null,
    string? CardLast4 = null,
    string? Note = null,

    /// <summary>
    /// Karar <b>fişin fiilen okunmasına</b> mı dayanıyor? (ödeme sayacı görüldü mü)
    ///
    /// <para>Bu ayrım <see cref="ProbeVerdict.NotLanded"/> için kritik: sayaç okunup "bizim ödememiz
    /// yok" görüldüyse bu bir <b>kanıttır</b> ve kasaya kesin cevap verilebilir. Ama "açık fiş yok +
    /// elimizde anlık görüntü de yok" gibi bir ÇIKARIMLA aynı sonuca varıldıysa kanıt yoktur —
    /// fiş ödeme tamamlandığı için de kapanmış olabilir. İkisini aynı saymak, "bilmiyorum"u
    /// "kesin hayır"a çevirmenin bir başka kılığı olurdu.</para>
    /// </summary>
    bool CounterRead = false,

    /// <summary>
    /// Yoklamanın ürettiği nexo koşulu — <c>null</c> ise çağıran kendi varsayılanını kullanır.
    ///
    /// <para><b>Neden yoklamadan geliyor:</b> "fişe para yazılmadı" sonucunun ARDINDAKİ sebep
    /// yalnız cihazın ödeme satırında var (banka açıkça mı reddetti, yoksa cevap mı gelmedi) ve o
    /// satırı okuyan tek yer burası. Çağırana tek bir "işlenmedi" bayrağı verip sebebi düşürmek,
    /// iki farklı gerçeği tek cümleye sıkıştırmak olurdu.</para>
    /// </summary>
    string? ErrorCondition = null,

    /// <summary>
    /// Yoklamanın ürettiği <c>Restomenum.reason</c> — <b>cihazın hata sözlüğünden</b>
    /// (<c>NO_RESPONSE</c> / <c>BANK_DECLINED</c>). <c>null</c> ise çağıran belirler.
    /// </summary>
    string? Reason = null,

    /// <summary>
    /// Yoklamanın ürettiği <c>Restomenum.info</c> — durum etiketi (<c>NOT_LANDED</c>).
    ///
    /// <para><b>Neden <c>reason</c>'dan AYRI:</b> platform <c>reason:NOT_LANDED</c>'i
    /// <c>notStarted</c> ("kart hiç çekilmedi, tekrar dene") diye okuyor. Fişe yazılmamış ama
    /// bankadan cevap da gelmemiş bir denemeyi o kutuya koymak çift tahsilat üretirdi. Durum
    /// <c>info</c>'da, SEBEP <c>reason</c>'da.</para>
    /// </summary>
    string? Info = null,

    /// <summary>Cihazın ham hata izi (<c>AdditionalResponse</c>) — teşhis metni, dallanma için DEĞİL.</summary>
    string? ProviderResultCode = null,

    /// <summary>RM-0396: bankanın onay kodu (bkz. <see cref="GmpTicket.ApprovalCode"/>); günlüğe yazılmaz.</summary>
    string? ApprovalCode = null);
