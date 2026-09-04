package com.restomenum.agent

import java.nio.file.Files
import java.util.concurrent.Callable
import java.util.concurrent.Executors
import java.sql.DriverManager
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * §12.2 değişmezlerinin doğrulaması. Her test bir değişmeze karşılık gelir ve ihlali gerçek bir
 * para hatasıdır — bu yüzden isimlerde hangi değişmez olduğu yazıyor.
 */
class AgentCoreTest {

    private fun gecici(): String =
        Files.createTempDirectory("agent-test").resolve("agent.db").toString()

    // ── §12.2/2: dedupe ATOMİK ──────────────────────────────────────────────

    @Test
    fun `12_2_2 ayni commandId iki kez kaydedilemez`() {
        CommandStore.ac(gecici()).use { store ->
            val a = store.kaydet("cmd-1", "pay-1", "t1", System.currentTimeMillis() + 60_000)
            val b = store.kaydet("cmd-1", "pay-1", "t1", System.currentTimeMillis() + 60_000)
            assertTrue(a is KayitSonucu.Yeni, "ilk kayıt yeni olmalı")
            assertTrue(b is KayitSonucu.Tekrar, "ikinci kayıt TEKRAR olmalı — terminal çağrılmaz")
        }
    }

    @Test
    fun `12_2_2 ESZAMANLI kayitta TAM OLARAK BIRI yeni doner`() {
        // Oturum devri anında iki soket aynı komutu alabilir. "Oku-sonra-yaz" olsaydı ikisi de
        // "yok" görür, ikisi de yazar ve komut İKİ KEZ terminale giderdi — çift tahsilat.
        val yol = gecici()
        CommandStore.ac(yol).use { it }   // şemayı kur
        val havuz = Executors.newFixedThreadPool(8)
        try {
            val isler = (1..8).map {
                Callable {
                    // Her iş parçacığı KENDİ bağlantısını açar — gerçek eşzamanlılık.
                    val c = DriverManager.getConnection("jdbc:sqlite:$yol")
                    c.createStatement().use { st -> st.execute("PRAGMA busy_timeout=5000") }
                    CommandStore(c).use { s ->
                        s.kaydet("cmd-yaris", "pay-1", "t1", System.currentTimeMillis() + 60_000)
                    }
                }
            }
            val sonuclar = havuz.invokeAll(isler).map { it.get() }
            val yeniSayisi = sonuclar.count { it is KayitSonucu.Yeni }
            assertEquals(1, yeniSayisi, "8 eşzamanlı kayıttan TAM OLARAK BİRİ yeni olmalı")
        } finally { havuz.shutdown() }
    }

    // ── §12.2/3: tekrar gelen komut terminale GİTMEZ, önceki durum replay edilir ──

    @Test
    fun `12_2_3 tekrar onceki durumu replay eder`() {
        CommandStore.ac(gecici()).use { store ->
            store.kaydet("cmd-2", "pay-2", "t1", System.currentTimeMillis() + 60_000)
            store.ilerlet("cmd-2", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL)
            store.ilerlet("cmd-2", CommandState.SENT_TO_TERMINAL, CommandState.COMPLETED, resultJson = """{"rrn":"418512345678"}""")

            val tekrar = store.kaydet("cmd-2", "pay-2", "t1", System.currentTimeMillis() + 60_000)
            assertTrue(tekrar is KayitSonucu.Tekrar)
            // Sonuç KORUNUYOR: replay, terminale yeniden gitmek değil saklanan sonucu döndürmektir.
            assertEquals(CommandState.COMPLETED, (tekrar as KayitSonucu.Tekrar).komut.state)
            assertNotNull(tekrar.komut.resultJson)
        }
    }

    // ── §12.2/6: SENT_TO_TERMINAL sonrası SALE retry YOK ─────────────────────

    @Test
    fun `12_2_6 terminale gitmis komut RECEIVED'a DONEMEZ`() {
        // Dönebilseydi bir yeniden bağlanma, kart çekilmiş bir işlemi "hiç başlamamış" gösterir
        // ve ikinci kez gönderilmesine kapı açardı.
        assertFalse(canTransition(CommandState.SENT_TO_TERMINAL, CommandState.RECEIVED))
        assertFalse(canTransition(CommandState.UNKNOWN, CommandState.RECEIVED))
        assertFalse(canTransition(CommandState.UNKNOWN, CommandState.SENT_TO_TERMINAL))
        assertFalse(canTransition(CommandState.COMPLETED, CommandState.SENT_TO_TERMINAL))
    }

    @Test
    fun `komut TERMINALE UGRAMADAN tamamlanamaz`() {
        // Doğrudan RECEIVED → COMPLETED, "terminale hiç gitmeden başarılı oldu" demek olurdu.
        // (Bu kural bu testin ilk sürümündeki hatayı yakaladı.)
        assertFalse(canTransition(CommandState.RECEIVED, CommandState.COMPLETED))
    }

    @Test
    fun `12_2_6 UNKNOWN yalniz SORGU ile cozulur`() {
        assertTrue(canTransition(CommandState.SENT_TO_TERMINAL, CommandState.UNKNOWN))
        assertTrue(canTransition(CommandState.UNKNOWN, CommandState.COMPLETED))
        assertTrue(canTransition(CommandState.UNKNOWN, CommandState.REJECTED))
        assertTrue(CommandState.UNKNOWN.mayHaveReachedTerminal(), "UNKNOWN'da kart çekilmiş OLABİLİR")
    }

    @Test
    fun `ilerlet yanlis mevcut durumdan yazmaz`() {
        CommandStore.ac(gecici()).use { store ->
            store.kaydet("cmd-3", "pay-3", "t1", System.currentTimeMillis() + 60_000)
            store.ilerlet("cmd-3", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL)
            // Beklenen durum artık RECEIVED değil → yazma REDDEDİLİR (yarış koruması).
            assertFalse(store.ilerlet("cmd-3", CommandState.RECEIVED, CommandState.EXPIRED))
            assertEquals(CommandState.SENT_TO_TERMINAL, store.oku("cmd-3")!!.state)
        }
    }

    // ── §12.2/5 + §5.3: saat offseti ────────────────────────────────────────

    @Test
    fun `5_3 offset bilinmiyorsa karar VERILMEZ`() {
        val saat = ClockOffset()
        assertFalse(saat.senkronMu())
        // Tahmin YOK: null "bilmiyorum" demek, çağıran CLOCK_UNSYNCED döner.
        assertNull(saat.suresiGectiMi(System.currentTimeMillis() + 60_000))
    }

    @Test
    fun `5_3 duvar saati YANLISSA bile sunucu zamani dogru`() {
        // Kasa saati 2 saat geride. Cihaz saatine güvenilseydi geçerli komutlar "süresi geçti"
        // diye reddedilirdi.
        val yanlisDuvar = System.currentTimeMillis() - 2 * 3600_000
        var mono = 1000L
        val saat = ClockOffset(wallClock = { yanlisDuvar }, monotonic = { mono })
        val gercekSunucu = System.currentTimeMillis()
        saat.senkronla(gercekSunucu, mono)
        assertTrue(saat.senkronMu())
        val sapma = Math.abs(saat.sunucuSimdi() - gercekSunucu)
        assertTrue(sapma < 1000, "sunucu zamanı ±1 sn içinde olmalı, sapma=$sapma")
        // Ve süre kararı artık doğru veriliyor.
        assertEquals(false, saat.suresiGectiMi(gercekSunucu + 60_000))
        assertEquals(true, saat.suresiGectiMi(gercekSunucu - 1))
    }

    @Test
    fun `5_3 offset BAYATLARSA tekrar senkron gerekir`() {
        var mono = 1000L
        val saat = ClockOffset(wallClock = { System.currentTimeMillis() }, monotonic = { mono })
        saat.senkronla(System.currentTimeMillis(), mono)
        assertTrue(saat.senkronMu())
        mono += 11 * 60_000   // 11 dk geçti
        assertFalse(saat.senkronMu(), "bayat offsete güvenilmemeli")
        assertNull(saat.suresiGectiMi(System.currentTimeMillis() + 60_000))
    }

    // ── §5.2: yeniden bağlanma jitter'ı ─────────────────────────────────────

    @Test
    fun `5_2 backoff artar ve tavanda kalir`() {
        val b = Backoff(rastgele = { it / 2 })   // jitter'ı sabitle (delta = 0)
        val g = (1..8).map { b.sonraki() }
        assertEquals(listOf(500L, 1000L, 2000L, 5000L, 10000L, 30000L, 30000L, 30000L), g)
        b.sifirla()
        assertEquals(500L, b.sonraki(), "başarılı bağlantı sonrası en kısa gecikmeden başlamalı")
    }

    @Test
    fun `5_2 jitter dagitiyor - hepsi ayni anda donmuyor`() {
        // Sabit gecikme, 10.000 cihazda aynı saniyede ~33 bağlantı/sn üretirdi (fırtına).
        val gecikmeler = (1..200).map { Backoff().sonraki() }.toSet()
        assertTrue(gecikmeler.size > 20, "jitter yeterince dağıtmalı, farklı değer: ${gecikmeler.size}")
        gecikmeler.forEach { assertTrue(it in 400..600, "±%20 bandı dışında: $it") }
    }

    // ── §12.3: retention ────────────────────────────────────────────────────

    @Test
    fun `12_3 retention ucustaki komutu ASLA silmez`() {
        CommandStore.ac(gecici()).use { store ->
            val eski = System.currentTimeMillis() - 40L * 86_400_000
            store.kaydet("cmd-bitmis", "p", "t1", 0, now = eski)
            // RECEIVED → COMPLETED YASAK (komut terminale uğramadan tamamlanamaz); yasal yol:
            store.ilerlet("cmd-bitmis", CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL, now = eski)
            store.ilerlet("cmd-bitmis", CommandState.SENT_TO_TERMINAL, CommandState.COMPLETED, now = eski)
            store.kaydet("cmd-ucusta", "p", "t1", 0, now = eski)
            store.ilerlet("cmd-ucusta", CommandState.RECEIVED, CommandState.UNKNOWN, now = eski)

            val silinen = store.temizle(System.currentTimeMillis() - 30L * 86_400_000)
            assertEquals(1, silinen)
            assertNull(store.oku("cmd-bitmis"))
            // UNKNOWN = sonuç belirsiz. Silmek, çözülmemiş bir tahsilatı kaybetmek olurdu.
            assertNotNull(store.oku("cmd-ucusta"), "UNKNOWN komut SİLİNMEMELİ")
        }
    }
}
