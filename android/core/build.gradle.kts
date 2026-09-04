// `core` SAF JVM modülüdür — Android bağımlılığı YOKTUR ve bu bilinçlidir.
//
// §12.2'nin dokuz değişmezinin çoğu (atomik dedupe, süre kontrolü, saat offseti, UNKNOWN akışı,
// outbox replay) saf mantıktır. Android modülüne gömülselerdi doğrulamak için cihaz/emülatör
// gerekirdi ve pratikte hiç test edilmezlerdi. Burada JVM'de saniyeler içinde koşuyorlar.
plugins {
    kotlin("jvm") version "2.1.0"
}
repositories { mavenCentral() }
dependencies {
    implementation("org.xerial:sqlite-jdbc:3.47.1.0")   // yerel dayanıklı store (§12.3, SQLite WAL)
    testImplementation(kotlin("test"))
    // Yalnız test: uygunluk vektörleri (conformance/*.json) okunur. Vektörü koda gömmek,
    // dosya değiştiğinde testin güncellenmemesi demekti — kopya yine ayrışırdı.
    testImplementation("com.google.code.gson:gson:2.11.0")
}
kotlin { jvmToolchain(21) }
tasks.test { useJUnitPlatform(); testLogging { showStandardStreams = true } }
