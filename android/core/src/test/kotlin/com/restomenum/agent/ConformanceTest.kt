package com.restomenum.agent

import com.google.gson.JsonParser
import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * **Uygunluk vektörleri** — `conformance` klasöründeki JSON dosyaları. Windows agent'ı da AYNI dosyaları okuyup aynı
 * sonucu üretmek zorundadır.
 *
 * Vektörler burada koda GÖMÜLMEZ; dosyadan okunur. Gömülen bir kopya, dosya değiştiğinde
 * güncellenmez ve iki agent yine ayrışırdı — kaçınmak istediğimiz şeyin ta kendisi.
 */
class ConformanceTest {

    private fun vektorDosyasi(ad: String): File {
        // core → android → proje kökü
        val kok = File(System.getProperty("user.dir")).let { d ->
            generateSequence(d) { it.parentFile }.first { File(it, "conformance").isDirectory }
        }
        return File(kok, "conformance/$ad")
    }

    /** Session imzasının kanonik dizesi — `session.js` `imzaGovdesi()` ile birebir aynı olmalı. */
    private fun kanonik(connectorId: String, nonce: String, timestamp: String): String {
        fun p(v: String) = "${v.length}:$v"
        return "${p(connectorId)}|${p(nonce)}|${p(timestamp)}"
    }

    @Test
    fun `imza kanonik dizesi vektorlere uyuyor`() {
        val j = JsonParser.parseString(vektorDosyasi("signing.json").readText()).asJsonObject
        var n = 0
        j.getAsJsonArray("vektorler").forEach { e ->
            val o = e.asJsonObject
            val uretilen = kanonik(
                o.get("connectorId").asString,
                o.get("nonce").asString,
                o.get("timestamp").asString,
            )
            assertEquals(o.get("expected").asString, uretilen, "vektör ${n + 1}")
            n++
        }
        assertTrue(n >= 6, "vektör dosyası boşalmış olmasın: $n")
    }

    @Test
    fun `ayrac iceren alanlar AYNI dizeyi uretmiyor`() {
        // Düz "a|b|c" birleştirmesinde bu ikisi AYNI olurdu; tek imza iki farklı isteği doğrulardı.
        val a = kanonik("conn|9", "abc", "1")
        val b = kanonik("conn", "9|abc", "1")
        assertTrue(a != b, "belirsizlik kapanmamış: $a")
    }

    @Test
    fun `durum gecisleri vektorlere uyuyor`() {
        val j = JsonParser.parseString(vektorDosyasi("state-transitions.json").readText()).asJsonObject
        var n = 0
        j.getAsJsonArray("vektorler").forEach { e ->
            val o = e.asJsonObject
            val from = CommandState.valueOf(o.get("from").asString)
            val to = CommandState.valueOf(o.get("to").asString)
            assertEquals(o.get("expected").asBoolean, canTransition(from, to), "$from → $to")
            n++
        }
        assertEquals(30, n, "tüm durum çiftleri kapsanmalı")

        j.getAsJsonArray("final_durumlar").forEach {
            assertTrue(CommandState.valueOf(it.asString).isFinal(), "${it.asString} final olmalı")
        }
        j.getAsJsonArray("terminale_ulasmis_olabilir").forEach {
            assertTrue(CommandState.valueOf(it.asString).mayHaveReachedTerminal(), it.asString)
        }
    }

    @Test
    fun `backoff basamaklari vektorlere uyuyor`() {
        val j = JsonParser.parseString(vektorDosyasi("backoff.json").readText()).asJsonObject
        val basamaklar = j.getAsJsonArray("basamaklar_ms").map { it.asLong }
        val yuzde = j.get("jitter_yuzde").asInt

        val b = Backoff(rastgele = { it / 2 })   // jitter'ı sıfırla → taban görünsün
        basamaklar.forEachIndexed { i, beklenen -> assertEquals(beklenen, b.sonraki(), "basamak $i") }
        assertEquals(basamaklar.last(), b.sonraki(), "tavanda kalmalı")

        // Jitter bandı — gerçek rastgelelikle
        val j2 = Backoff()
        val ilk = basamaklar.first()
        repeat(50) {
            val g = Backoff().sonraki()
            val alt = ilk - ilk * yuzde / 100
            val ust = ilk + ilk * yuzde / 100
            assertTrue(g in alt..ust, "jitter bandı dışında: $g (band $alt..$ust)")
        }
        j2.sifirla()
    }
}
