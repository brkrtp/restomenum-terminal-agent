namespace Restomenum.Agent.Core;

/// <summary>
/// Ürünün KDV oranı ile departmanın oranının çelişip çelişmediği — <b>TEK KARAR NOKTASI</b>.
///
/// <para><b>Neden ayrı bir yerde:</b> aynı kuralı iki yerde yazmak (satış yolunda bir kez, kuru
/// provada bir kez) iki yerin sessizce ıraksaması demektir; o zaman prova "geçer" derken satış
/// reddedebilir ve kullanıcı neye inanacağını bilemez. Kural burada bir kez duruyor, her iki
/// çağıran da bunu çağırıyor.</para>
///
/// <para><b>Neden kural gerekli (§30.12):</b> fiş satırına KDV oranı YAZILMIYOR (<c>VatRate=0</c>),
/// oranı tamamen departman belirliyor; <c>TaxCode</c> ise terminale hiç gitmiyor. İkisi çelişirse
/// fişte bir oran, defterde başka oran olur ve bunu hiçbir kapı yakalamaz — mali kayıt geri
/// alınamaz. O yüzden çelişkide kalem terminale GÖNDERİLMEZ.</para>
/// </summary>
public static class TaxRule
{
    /// <summary>
    /// Çelişki var mı? <c>false</c> = gönderilebilir (uyuşuyor <b>ya da</b> karşılaştırılamıyor).
    ///
    /// <para><b>Karşılaştırılamıyorsa REDDETMİYORUZ</b> ve bu bilinçli: departmanın oranı
    /// bilinmiyorsa (cihaz tablosu okunamamış) ya da <c>TaxCode</c> sayı değilse elimizde bir
    /// çelişki KANITI yok. Kanıtsız reddetmek her kalemi düşürürdü — "okunamadı"yı "yanlış"
    /// saymanın bir başka kılığı. Yalnız gerçek, sayısal çelişkide ret.</para>
    /// </summary>
    /// <param name="taxCode">Platformun ürün vergi kodu — <b>yüzde string</b> ("10" = %10).</param>
    /// <param name="departmentRateBasisPoints">Cihaz departmanının oranı — <b>baz puan</b> (1000 = %10).</param>
    public static bool Conflicts(string? taxCode, int? departmentRateBasisPoints) =>
        departmentRateBasisPoints is int deptRate
        && int.TryParse(taxCode, out var taxPct)
        && taxPct * 100 != deptRate;
}
