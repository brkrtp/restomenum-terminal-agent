package com.restomenum.agent

/**
 * Sunucu saat offseti (§5.3) — **`expiresAt` kontrolü cihaz saatine bırakılamaz.**
 *
 * Restoran kasalarında duvar saati yanlış olabilir, elle değiştirilebilir veya NTP hiç
 * yapılandırılmamış olabilir. Cihaz saatine güvenip süresi geçmiş bir komutu çalıştırmak, kasiyerin
 * çoktan vazgeçtiği bir tahsilatı terminale düşürmek demektir; tersi de mümkündür — geçerli bir
 * komut "süresi geçti" diye reddedilir.
 *
 * Offset **monotonic clock** üzerinden tutulur: duvar saati değişse bile (kullanıcı saati elle
 * değiştirdi, NTP sıçradı) geçen süre ölçümü bozulmaz.
 *
 * Offset **bilinmiyorsa tahmin YOKTUR** — `CLOCK_UNSYNCED` döner ve komut çalıştırılmaz.
 */
class ClockOffset(
    private val wallClock: () -> Long = System::currentTimeMillis,
    private val monotonic: () -> Long = { System.nanoTime() / 1_000_000 },
) {
    private var offsetMs: Long? = null
    private var senkronMonotonic: Long = 0

    /** Offset'in bayat sayıldığı süre. Bu süreden eskiyse tekrar senkron gerekir. */
    private val gecerlilikMs = 10 * 60_000L

    /**
     * Sunucudan gelen `serverTime` ile offset kurulur.
     * @param serverTimeMs sunucunun bildirdiği an
     * @param istekBaslangiciMonotonic isteği gönderirken alınan monotonic okuma (gidiş-dönüş payı)
     */
    fun senkronla(serverTimeMs: Long, istekBaslangiciMonotonic: Long = monotonic()) {
        // Gidiş-dönüşün yarısı kadar düzeltme: sunucu zamanı yolda geçen sürenin ortasında geçerliydi.
        val turSuresi = monotonic() - istekBaslangiciMonotonic
        val tahminiSunucuSimdi = serverTimeMs + turSuresi / 2
        offsetMs = tahminiSunucuSimdi - wallClock()
        senkronMonotonic = monotonic()
    }

    /** Offset biliniyor ve taze mi? */
    fun senkronMu(): Boolean {
        val o = offsetMs ?: return false
        return (monotonic() - senkronMonotonic) <= gecerlilikMs
    }

    /**
     * Sunucu zamanına göre "şimdi".
     * @throws IllegalStateException offset bilinmiyorsa — TAHMİN YOK (§5.3)
     */
    fun sunucuSimdi(): Long {
        check(senkronMu()) { "CLOCK_UNSYNCED" }
        return wallClock() + offsetMs!!
    }

    /**
     * Komut süresi geçmiş mi?
     * @return `null` offset bilinmiyorsa (karar VERİLEMEZ — çağıran `CLOCK_UNSYNCED` döner)
     */
    fun suresiGectiMi(expiresAtMs: Long): Boolean? {
        if (!senkronMu()) return null
        return sunucuSimdi() >= expiresAtMs
    }

    /** Test/teşhis için ham offset. */
    fun offset(): Long? = if (senkronMu()) offsetMs else null
}
