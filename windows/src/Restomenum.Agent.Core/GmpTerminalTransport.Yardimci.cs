namespace Restomenum.Agent.Core;

/// <summary>
/// <b>YARDIMCILAR</b> — <see cref="GmpTerminalTransport"/>'un parçası (W43 bölmesi).
///
/// Saf dönüştürücüler ve hata kurucuları — cihaz çağrısı YOK, durum YOK. Buraya konmalarının
/// sebebi tek: aynı dönüşümün iki yerde ayrı ayrı yazılması, iki ayrı hata kaynağı olurdu.
///
/// <para><b>Neden `partial`, ayrı sınıf değil:</b> bu metotlar `_handle` (cihaz tanıtıcısı)
/// ve onu koruyan `_gate` kilidi üzerinde ORTAK ÇALIŞIYOR. Ayrı sınıflara bölmek o değişken
/// durumu sınıflar arasında paylaştırmak demekti — bölmenin amacı okunabilirlikti, yeni bir
/// paylaşım yüzeyi açmak değil. `partial` ile derlenen tür AYNI kalıyor: davranış değişmez.</para>
/// </summary>
public sealed partial class GmpTerminalTransport
{
    /// <summary>
    /// Cihazın bildirdiği toplam/ödenen çifti kendi içinde tutarlı mı? (W38)
    ///
    /// <para>Tutarsızsa hiçbir rakam bildirilmez: kasa cihazın sayısını otorite kabul edecek,
    /// dolayısıyla yanlış bir sayı kendi hesabından daha kötüdür. Üç durum reddedilir —
    /// toplam sıfır/negatif, ödenen negatif, ödenen toplamı aşıyor (kalan negatif çıkardı).</para>
    /// </summary>
    private static bool CihazTutarlariGecerli(GmpTicket t) =>
        t.TotalAmountMinor > 0 && t.PaidAmountMinor >= 0 && t.PaidAmountMinor <= t.TotalAmountMinor;

    /// <summary>
    /// Anlık görüntüden BU YANA eklenen ödeme satırları. <c>null</c> = okunamadı (liste eksik ya da
    /// yok) — "satır yok" ile KARIŞTIRILMAMALI, ilki bilgisizlik ikincisi beyandır.
    /// </summary>
    private static IReadOnlyList<GmpPaymentLine>? YeniOdemeSatirlari(TicketState simdi, int oncekiSayi)
    {
        if (!simdi.PaymentsAreComplete || simdi.Payments is not { } hepsi) return null;
        // Liste fişin TAMAMI değilse hangi satırın yeni olduğu söylenemez.
        if (hepsi.Count != simdi.PaymentCount) return null;
        if (oncekiSayi < 0 || oncekiSayi > hepsi.Count) return null;
        var yeni = hepsi.Skip(oncekiSayi).ToList();
        return yeni.Count == 0 ? null : yeni;
    }

    /// <summary>
    /// Banka uygulamasının kodu GERÇEK bir sonuç taşıyor mu? Ölçüldü (2026-09-07): açık rette
    /// <c>"2202"</c> geliyor, cevapsızlıkta <c>"0000"</c> + <c>"(00000000)-DEFAULT"</c>. Yani
    /// <c>"0000"</c> "hata yok" değil, "kullanılabilir kod yok" demek.
    ///
    /// <para><b>Dayanak iki ölçüm.</b> Tanımadığımız bir kod gelirse açık ret sayılır — bu yön
    /// güvenli olan: açık ret "tekrar güvenli" der ve yanılıyorsak yalnız gereksiz bir tekrar
    /// olur; tersi yönde yanılmak yetim provizyonu görünmez kılardı.</para>
    /// </summary>
    private static bool KullanilabilirKod(string? appErrorCode) =>
        !string.IsNullOrWhiteSpace(appErrorCode)
        && appErrorCode.Trim().Trim('0').Length > 0;

    // ── yardımcılar ─────────────────────────────────────────────────────────

    private static TicketState Cevir(GmpTicket t, bool acik) => new(
        HasOpenTicket: acik, TotalAmountMinor: t.TotalAmountMinor, PaidAmountMinor: t.PaidAmountMinor,
        Rrn: t.Rrn, CardLast4: t.CardLast4, PaymentCount: t.PaymentCount,
        LastPaymentErrorCode: t.LastPaymentErrorCode, LastPaymentErrorText: t.LastPaymentErrorText,
        LastPaymentAppErrorCode: t.LastPaymentAppErrorCode,
        LastPaymentAppErrorText: t.LastPaymentAppErrorText,
        Payments: t.Payments, PaymentsAreComplete: t.PaymentsAreComplete);

    /// <summary>
    /// Terminale <b>hiç gidilmeden</b> üretilen ret. <c>FP3_Payment</c> çağrılmadığı için para
    /// hareket edemez — ama <c>Refusal</c> DEĞİL: banka hiç devrede olmadığından "kart reddedildi"
    /// mesajı yanlış olurdu (W1 kuralı: kesin-ret yalnız host'un açık reddiyle).
    /// </summary>
    /// <summary>
    /// Cihazın gördüğü tutarları da taşıyan ret. YALNIZ kalan/toplam ayrışmasından doğan iki
    /// sebepte kullanılır — kasiyer panelin değil CİHAZIN sayısını görmeli, çünkü ödemeyi
    /// kabul edecek olan cihaz.
    /// </summary>
    private static TransportResult HataCihazTutarli(string kod, string reason, GmpTicket fis) =>
        new(TransportOutcome.Declined, ProviderResultCode: kod, ErrorCondition: "PaymentRestriction",
            PaymentInvoked: false, Reason: reason,
            DeviceTicketTotalMinor: fis.TotalAmountMinor,
            DeviceRemainingMinor: fis.RemainingMinor);

    private static TransportResult Hata(TransportOutcome o, string kod, string errorCondition, string reason) =>
        new(o, ProviderResultCode: kod, ErrorCondition: errorCondition,
            PaymentInvoked: false, Reason: reason);

    /// <summary>
    /// Ham GMP-3 kodu → taşıma sonucu. <b>Karar burada verilmez</b>; tek eşleme tablosu
    /// <see cref="GmpErrorMap"/>'tedir. Bu metot yalnız adımı ve iz kaydını ekler.
    ///
    /// <para><b>Eski hâli para riskiydi:</b> son satır <c>_ =&gt; Declined</c> idi, yani
    /// <i>kapsanmayan her kod</i> "kesin ret" sayılıyordu. Canlı terminal (banka hattı yokken)
    /// <c>FP3_Payment</c>'tan <b>2085</b> döndürdü, hiçbir dal yakalamadı ve deneme kasaya
    /// <c>Refusal</c> = "kart reddedildi, başka kart isteyin" diye kapandı. Doğru cevap "bilmiyorum,
    /// terminale soralım"dı.</para>
    /// </summary>
    private static TransportResult Cevir(GmpResult r, string adim, GmpStep asama)
    {
        var e = GmpErrorMap.Map(r.Code, asama);
        return new TransportResult(e.Outcome,
            ProviderResultCode: $"{adim}:{r}", ErrorCondition: e.ErrorCondition,
            PaymentInvoked: asama != GmpStep.BeforePayment, Reason: e.Reason);
    }

    private TransportResult TemizleVeCevir(ulong handle, GmpResult r, string adim, GmpStep asama)
    {
        // Yarım açılmış fiş bırakılmaz: bir sonraki `StartTicket` onu SESSİZCE iptal eder ve
        // o sırada üzerinde ödeme varsa para kaybolur.
        _gmp.VoidAll(handle, out _);
        _gmp.Close(handle);
        lock (_gate) _handle = 0;
        return Cevir(r, adim, asama);
    }
}
