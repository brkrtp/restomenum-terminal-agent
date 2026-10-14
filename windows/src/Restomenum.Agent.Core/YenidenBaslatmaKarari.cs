namespace Restomenum.Agent.Core;

/// <summary>
/// <b>RM-0389 — kurulum sayfası ajanı yeniden başlatabilir mi?</b> Servis olarak çalışırken yeniden
/// başlatma, sürecin çıkıp servis yöneticisinin (SCM) kurtarma eylemiyle yeniden açmasına dayanır.
/// SCM N'inci hatada N'inci eylemi kullanır ve son eylemi tekrarlar; bu yüzden listedeki EYLEMLERİN
/// HEPSİ yeniden başlatma olmalı — <c>[RESTART, NONE]</c> ikinci yeniden başlatmada servisi kapalı
/// bırakır (denetim 2, Codex 3). Koşul sağlanmazsa yeniden başlatma gerektiren işlem DEĞİŞİKLİK
/// YAPMADAN reddedilir.
/// </summary>
public static class YenidenBaslatmaKarari
{
    /// <summary>Win32 <c>SC_ACTION_RESTART</c>.</summary>
    public const int ScActionRestart = 1;

    public const string KurulumKomutu =
        "sc.exe failure RestomenumAgent reset= 86400 actions= restart/5000/restart/5000/restart/30000";

    /// <param name="eylemTurleri">Servisin kurtarma eylemleri (<c>SC_ACTION.Type</c>); <c>null</c> = okunamadı.</param>
    /// <returns><c>null</c> = yeniden başlatılabilir; dolu = engel ve çözüm.</returns>
    public static string? Engel(bool servisModu, IReadOnlyList<int>? eylemTurleri)
    {
        if (!servisModu) return null;
        if (eylemTurleri is null)
            return "Servisin kurtarma ayarı okunamadı; ajan yeniden başlatılamayabilir. Yönetici olarak şunu çalıştırın: " + KurulumKomutu;
        if (eylemTurleri.Count == 0 || eylemTurleri.Any(t => t != ScActionRestart))
            return "Servisin 'hata olursa yeniden başlat' ayarı eksik; kurulum sayfası ajanı güvenle yeniden başlatamaz. " +
                   "Yönetici olarak şunu çalıştırın: " + KurulumKomutu;
        return null;
    }
}
