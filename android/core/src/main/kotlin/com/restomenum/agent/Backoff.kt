package com.restomenum.agent

import kotlin.random.Random

/**
 * Yeniden bağlanma gecikmesi (§5.2) — **jitter ZORUNLUDUR**.
 *
 * Bir gateway instance'ı kapandığında ona bağlı tüm agent'lar aynı anda kopar. Sabit gecikmeyle
 * hepsi aynı saniyede geri dönerse **yeniden bağlanma fırtınası** olur: 10.000 cihazda ~33
 * bağlantı/sn, 50.000'de ~166. Jitter bu dalgayı yayar.
 *
 * Basamaklar: 0.5s, 1s, 2s, 5s, 10s, maks 30s (±%20 jitter).
 */
class Backoff(private val rastgele: (Double) -> Double = { Random.nextDouble(it) }) {
    private val basamaklarMs = longArrayOf(500, 1_000, 2_000, 5_000, 10_000, 30_000)
    private var deneme = 0

    /** Başarılı bağlantıdan sonra çağrılır — bir sonraki kopma en kısa gecikmeyle başlasın. */
    fun sifirla() { deneme = 0 }

    /** Sıradaki gecikme (ms), jitter uygulanmış. */
    fun sonraki(): Long {
        val taban = basamaklarMs[minOf(deneme, basamaklarMs.size - 1)]
        deneme++
        // ±%20 jitter: dalgayı yayar. Yalnız pozitif jitter, dalgayı yaymak yerine ötelerdi.
        val sapma = taban * 0.2
        val delta = rastgele(2 * sapma) - sapma
        return (taban + delta).toLong().coerceAtLeast(1)
    }

    fun denemeSayisi(): Int = deneme
}
