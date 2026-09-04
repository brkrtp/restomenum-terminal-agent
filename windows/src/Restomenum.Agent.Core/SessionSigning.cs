namespace Restomenum.Agent.Core;

/// <summary>
/// Session imzasının **kanonik dizesi** (§5.2).
///
/// Uzunluk önekli kodlama kullanılır (<c>len:deger|len:deger|len:deger</c>). Düz <c>a|b|c</c>
/// birleştirmesi BELİRSİZDİR: bir alan ayraç içerirse iki farklı üçlü AYNI dizeyi üretir ve tek
/// imza iki farklı isteği doğrular.
///
/// Örnek: <c>("conn|9","abc")</c> ile <c>("conn","9|abc")</c> düz birleştirmede aynı olurdu.
///
/// Platform karşılığı: <c>functions/api/connectors/session.js</c> → <c>imzaGovdesi()</c>.
/// Android karşılığı: <c>ConformanceTest.kt</c> → <c>kanonik()</c>.
/// Üçü <c>conformance/signing.json</c> vektörlerinden geçmek ZORUNDA.
/// </summary>
public static class SessionSigning
{
    public static string CanonicalString(string connectorId, string nonce, string timestamp)
    {
        static string P(string v) => $"{v.Length}:{v}";
        return $"{P(connectorId)}|{P(nonce)}|{P(timestamp)}";
    }
}
