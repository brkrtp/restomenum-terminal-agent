package com.restomenum.agent

/**
 * Agent tarafındaki komut durumları ve geçiş kuralları (§12.2).
 *
 * Platformdaki `stateMachine.js` ile **aynı sözlüğü kullanmaz ve kullanmamalıdır**: platform bir
 * ödeme DENEMESİNİ izler (birden çok agent/terminal görebilir), agent ise kendi elindeki KOMUTU.
 * İkisini tek enum'a sıkıştırmak, agent'ın bilemeyeceği durumları (`UNRESOLVED`, `REVERSED`)
 * agent'ın sorumluluğuymuş gibi gösterirdi.
 */
enum class CommandState {
    /** Komut alındı ve dayanıklı store'a YAZILDI. §12.2/1: yazılmadan `COMMAND_RECEIVED` gönderilmez. */
    RECEIVED,

    /** Terminale gönderildi. §12.2/6: bu noktadan sonra otomatik SALE retry YOKTUR. */
    SENT_TO_TERMINAL,

    /** Terminal yanıt verdi, sonuç kesin. */
    COMPLETED,

    /**
     * Sonuç belirsiz — crash, timeout veya bağlantı kopması. Kart çekilmiş OLABİLİR.
     * Yeniden denemek çift tahsilat riskidir; çözüm sorgudan gelir (§12.2/6).
     */
    UNKNOWN,

    /** Süresi geçtiği için terminale hiç gönderilmedi (§12.2/5). */
    EXPIRED,

    /** Terminale hiç ulaşmadan reddedildi (ör. `CLOCK_UNSYNCED`, terminal meşgul). */
    REJECTED,
}

/** Kesin sonuç mu? Kesin durumlar bir daha değişmez. */
fun CommandState.isFinal(): Boolean =
    this == CommandState.COMPLETED || this == CommandState.EXPIRED || this == CommandState.REJECTED

/**
 * İzinli geçişler. **Geri kenar YOKTUR:** terminale gönderilmiş bir komut asla `RECEIVED`'a dönemez;
 * dönebilseydi bir yeniden bağlanma, kart çekilmiş bir işlemi "hiç başlamamış" gösterir ve ikinci
 * kez gönderilmesine kapı açardı.
 */
private val ALLOWED: Map<CommandState, Set<CommandState>> = mapOf(
    CommandState.RECEIVED to setOf(
        CommandState.SENT_TO_TERMINAL, CommandState.EXPIRED, CommandState.REJECTED, CommandState.UNKNOWN,
    ),
    // COMPLETED'a doğrudan gidebilir (terminal senkron cevap verdi) ya da UNKNOWN'a düşebilir.
    CommandState.SENT_TO_TERMINAL to setOf(CommandState.COMPLETED, CommandState.UNKNOWN),
    // UNKNOWN yalnız SORGU ile çözülür; SALE tekrarıyla değil.
    CommandState.UNKNOWN to setOf(CommandState.COMPLETED, CommandState.REJECTED),
    CommandState.COMPLETED to emptySet(),
    CommandState.EXPIRED to emptySet(),
    CommandState.REJECTED to emptySet(),
)

fun canTransition(from: CommandState, to: CommandState): Boolean =
    ALLOWED[from]?.contains(to) == true

/** Terminale ulaşmış olabilecek durumlar — bunlardan SALE tekrarı YASAK. */
fun CommandState.mayHaveReachedTerminal(): Boolean =
    this == CommandState.SENT_TO_TERMINAL || this == CommandState.UNKNOWN || this == CommandState.COMPLETED
