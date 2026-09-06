namespace Restomenum.Agent.Core;

/// <summary>
/// Ödeme yöntemi kimliği → cihaz ödeme tipi çözümleyici (yerel mimari, K-21 / §20-I). Departman
/// çözümleyicisinin (<see cref="ILineDepartmentResolver"/>) kardeşi: eşleme <b>cihaz kurulumuna aittir</b>
/// (sağlayıcı=eklenti), platformun veri modelinde DEĞİL.
///
/// <para><b>Neden PaymentMethodId (PaymentType değil):</b> platformun kaba <c>cash/card/qr</c>'ı restoranın
/// gerçek yöntemlerine (Yemek Çeki, Sodexo, Multinet, belirli kart programları) yetmez — asıl çeşitlilik
/// yöntemde. <c>PaymentMethodId</c> kararlı kimlik (departmanlarda <c>ProductCode</c> neyse burada o) ve
/// platform onu defter bütünlüğü için zaten DİKTE ediyor. TEK EKSEN — iki eksen (tip+yöntem) sessizce
/// ıraksayabilirdi (<c>cash</c>+<c>11-credit</c>), o yüzden kaba tip kaldırıldı.</para>
///
/// <para><b>Kart↔nakit güvenliği eşleme ANINDA (eklenti UI'ı) kurulur</b> — yanlış tür işlem yaptırmak
/// (kart yöntemini nakit sürmek = kart hiç çekilmez = tahsilat kaybı) orada engellenir. Ajan runtime'da
/// yalnız <b>eşlenmemiş yöntemi</b> fail-closed reddeder; cihazda <c>cash</c> bayrağı yoktur.</para>
/// </summary>
public interface IPaymentMethodResolver
{
    /// <summary>
    /// Yöntem kimliğinden cihaz ödeme tipi (<c>GmpPaymentTypes</c> 1/4/16). Eşleme yoksa <c>null</c> →
    /// PAYMENT_METHOD_UNMAPPED (terminale gitmeden ret; tahmin edilmiş tip yanlış iptal yolu seçer).
    /// </summary>
    int? Resolve(string paymentMethodId);
}
