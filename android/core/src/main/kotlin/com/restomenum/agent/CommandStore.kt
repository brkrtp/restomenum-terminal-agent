package com.restomenum.agent

import java.sql.Connection
import java.sql.DriverManager

/** Bir komutun store'daki hâli. */
data class StoredCommand(
    val commandId: String,
    val paymentId: String,
    val terminalId: String,
    val receivedAt: Long,
    val expiresAt: Long,
    val state: CommandState,
    val terminalReference: String?,
    val resultJson: String?,
)

/** `kaydet` sonucu — çağıran buna göre dallanır. */
sealed class KayitSonucu {
    /** İlk kez görüldü; terminale gönderilebilir. */
    data class Yeni(val komut: StoredCommand) : KayitSonucu()

    /** Aynı `commandId` daha önce görüldü. **Terminal ÇAĞRILMAZ**, önceki durum replay edilir (§12.2/3). */
    data class Tekrar(val komut: StoredCommand) : KayitSonucu()
}

/**
 * Agent'ın yerel dayanıklı komut deposu (§12.3) — SQLite WAL.
 *
 * ## §12.2/2: dedupe ATOMİK olmak ZORUNDA
 *
 * "Önce SELECT, yoksa INSERT" **yasaktır** ve sebebi somut: oturum devri (session takeover) anında
 * iki soket aynı komutu aynı anda alabilir. İkisi de SELECT'te "yok" görür, ikisi de INSERT eder ve
 * komut **iki kez** terminale gider — yani müşterinin kartından iki kez çekilir. `INSERT OR IGNORE`
 * + `changes()` bu yarışı veritabanı seviyesinde kapatır: tam olarak biri 1 satır etkiler.
 *
 * ## §12.2/1: yazılmadan onay YOK
 *
 * `COMMAND_RECEIVED` yalnız `kaydet` başarıyla döndükten sonra gönderilir. Önce onaylayıp sonra
 * yazmak, crash anında komutu hem kaybetmek hem "aldım" demiş olmak demektir.
 *
 * **Kart verisi TUTULMAZ** (§12.3): `resultJson` yalnız maskeli/referans alanları taşır.
 */
class CommandStore(private val conn: Connection) : AutoCloseable {

    companion object {
        fun ac(dosyaYolu: String): CommandStore {
            val c = DriverManager.getConnection("jdbc:sqlite:$dosyaYolu")
            c.createStatement().use { st ->
                // WAL: finansal sonuç yazımı okuma trafiğine takılmasın; crash sonrası tutarlı.
                st.execute("PRAGMA journal_mode=WAL")
                // Tam senkron: fiş öncesi commit'in gerçekten diskte olması gerekiyor (§12.3).
                st.execute("PRAGMA synchronous=FULL")
                st.execute(
                    """
                    CREATE TABLE IF NOT EXISTS commands (
                        command_id TEXT PRIMARY KEY,
                        payment_id TEXT NOT NULL,
                        terminal_id TEXT NOT NULL,
                        received_at INTEGER NOT NULL,
                        expires_at INTEGER NOT NULL,
                        state TEXT NOT NULL,
                        terminal_reference TEXT,
                        result_json TEXT,
                        updated_at INTEGER NOT NULL
                    )
                    """.trimIndent(),
                )
            }
            return CommandStore(c)
        }
    }

    /**
     * Komutu **atomik** kaydeder.
     * @return [KayitSonucu.Yeni] yalnız gerçekten ilk kez yazıldıysa; aksi hâlde [KayitSonucu.Tekrar].
     */
    fun kaydet(
        commandId: String,
        paymentId: String,
        terminalId: String,
        expiresAt: Long,
        now: Long = System.currentTimeMillis(),
    ): KayitSonucu {
        // TEK ifade: oku-sonra-yaz DEĞİL. `INSERT OR IGNORE` çakışmada sessizce 0 satır etkiler.
        val etkilenen = conn.prepareStatement(
            """
            INSERT OR IGNORE INTO commands
                (command_id, payment_id, terminal_id, received_at, expires_at, state, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """.trimIndent(),
        ).use { ps ->
            ps.setString(1, commandId); ps.setString(2, paymentId); ps.setString(3, terminalId)
            ps.setLong(4, now); ps.setLong(5, expiresAt)
            ps.setString(6, CommandState.RECEIVED.name); ps.setLong(7, now)
            ps.executeUpdate()
        }
        val komut = oku(commandId) ?: error("kayıt sonrası okunamadı: $commandId")
        return if (etkilenen == 1) KayitSonucu.Yeni(komut) else KayitSonucu.Tekrar(komut)
    }

    fun oku(commandId: String): StoredCommand? =
        conn.prepareStatement("SELECT * FROM commands WHERE command_id = ?").use { ps ->
            ps.setString(1, commandId)
            ps.executeQuery().use { rs ->
                if (!rs.next()) return null
                StoredCommand(
                    commandId = rs.getString("command_id"),
                    paymentId = rs.getString("payment_id"),
                    terminalId = rs.getString("terminal_id"),
                    receivedAt = rs.getLong("received_at"),
                    expiresAt = rs.getLong("expires_at"),
                    state = CommandState.valueOf(rs.getString("state")),
                    terminalReference = rs.getString("terminal_reference"),
                    resultJson = rs.getString("result_json"),
                )
            }
        }

    /**
     * Durumu ilerletir — **yalnız izinli geçişler** ve **yalnız beklenen mevcut durumdan**.
     *
     * `WHERE state = ?` şartı yarışı kapatır: iki iş parçacığı aynı anda ilerletmeye çalışırsa
     * tam olarak biri 1 satır etkiler. Kontrolü koda taşımak (oku, karşılaştır, yaz) aynı
     * `kaydet`'teki hatayı tekrarlamak olurdu.
     * @return true yazıldıysa
     */
    fun ilerlet(
        commandId: String,
        beklenen: CommandState,
        yeni: CommandState,
        terminalReference: String? = null,
        resultJson: String? = null,
        now: Long = System.currentTimeMillis(),
    ): Boolean {
        if (!canTransition(beklenen, yeni)) return false
        return conn.prepareStatement(
            """
            UPDATE commands
               SET state = ?, updated_at = ?,
                   terminal_reference = COALESCE(?, terminal_reference),
                   result_json = COALESCE(?, result_json)
             WHERE command_id = ? AND state = ?
            """.trimIndent(),
        ).use { ps ->
            ps.setString(1, yeni.name); ps.setLong(2, now)
            ps.setString(3, terminalReference); ps.setString(4, resultJson)
            ps.setString(5, commandId); ps.setString(6, beklenen.name)
            ps.executeUpdate() == 1
        }
    }

    /** Retention (§12.3: 7–30 gün). Kesin sonuca ulaşmış ESKİ kayıtlar silinir; uçuştakiler ASLA. */
    fun temizle(oncesi: Long): Int =
        conn.prepareStatement(
            "DELETE FROM commands WHERE updated_at < ? AND state IN ('COMPLETED','EXPIRED','REJECTED')",
        ).use { ps -> ps.setLong(1, oncesi); ps.executeUpdate() }

    override fun close() = conn.close()
}
