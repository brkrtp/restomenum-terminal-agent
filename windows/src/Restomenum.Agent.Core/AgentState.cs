namespace Restomenum.Agent.Core;

/// <summary>
/// Agent tarafındaki komut durumları (§12.2).
///
/// Platformun <c>stateMachine.js</c> sözlüğüyle **aynı değildir ve olmamalıdır**: platform bir ödeme
/// DENEMESİNİ izler (birden çok agent/terminal görebilir), agent ise kendi elindeki KOMUTU. İkisini
/// tek enum'a sıkıştırmak, agent'ın bilemeyeceği durumları (<c>UNRESOLVED</c>, <c>REVERSED</c>)
/// agent'ın sorumluluğuymuş gibi gösterirdi.
///
/// Android karşılığı: <c>android/core/.../AgentState.kt</c>. İkisi
/// <c>conformance/state-transitions.json</c> vektörlerinden geçmek ZORUNDA.
/// </summary>
public enum CommandState
{
    /// <summary>Komut alındı ve dayanıklı store'a YAZILDI. §12.2/1: yazılmadan ACK gönderilmez.</summary>
    RECEIVED,

    /// <summary>Terminale gönderildi. §12.2/6: bu noktadan sonra otomatik SALE retry YOKTUR.</summary>
    SENT_TO_TERMINAL,

    /// <summary>Terminal yanıt verdi, sonuç kesin.</summary>
    COMPLETED,

    /// <summary>
    /// Sonuç belirsiz — crash, timeout veya bağlantı kopması. Kart çekilmiş OLABİLİR.
    /// Yeniden denemek çift tahsilat riskidir; çözüm SORGUDAN gelir (§12.2/6).
    /// </summary>
    UNKNOWN,

    /// <summary>Süresi geçtiği için terminale hiç gönderilmedi (§12.2/5).</summary>
    EXPIRED,

    /// <summary>Terminale hiç ulaşmadan reddedildi (ör. CLOCK_UNSYNCED, terminal meşgul).</summary>
    REJECTED,
}

public static class AgentStateRules
{
    /// <summary>
    /// İzinli geçişler. **Geri kenar YOKTUR:** terminale gönderilmiş bir komut asla
    /// <see cref="CommandState.RECEIVED"/>'a dönemez — dönebilseydi bir yeniden bağlanma, kartı
    /// çekilmiş bir işlemi "hiç başlamamış" gösterir ve ikinci kez gönderilmesine kapı açardı.
    /// </summary>
    private static readonly IReadOnlyDictionary<CommandState, HashSet<CommandState>> Allowed =
        new Dictionary<CommandState, HashSet<CommandState>>
        {
            [CommandState.RECEIVED] = new()
            {
                CommandState.SENT_TO_TERMINAL, CommandState.EXPIRED,
                CommandState.REJECTED, CommandState.UNKNOWN,
            },
            [CommandState.SENT_TO_TERMINAL] = new() { CommandState.COMPLETED, CommandState.UNKNOWN },
            // UNKNOWN yalnız SORGU ile çözülür; SALE tekrarıyla değil.
            [CommandState.UNKNOWN] = new() { CommandState.COMPLETED, CommandState.REJECTED },
            [CommandState.COMPLETED] = new(),
            [CommandState.EXPIRED] = new(),
            [CommandState.REJECTED] = new(),
        };

    public static bool CanTransition(CommandState from, CommandState to) =>
        Allowed.TryGetValue(from, out var set) && set.Contains(to);

    /// <summary>Kesin sonuç mu? Kesin durumlar bir daha değişmez.</summary>
    public static bool IsFinal(this CommandState s) =>
        s is CommandState.COMPLETED or CommandState.EXPIRED or CommandState.REJECTED;

    /// <summary>Terminale ulaşmış olabilecek durumlar — bunlardan SALE tekrarı YASAK.</summary>
    public static bool MayHaveReachedTerminal(this CommandState s) =>
        s is CommandState.SENT_TO_TERMINAL or CommandState.UNKNOWN or CommandState.COMPLETED;
}
