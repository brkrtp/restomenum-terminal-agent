# restomenum-terminal-agent — edge agent (Faz 3)

## Kapsam

POS terminalini süren **cihaz üstü agent**. İki platform:

```
android/       Kotlin — DE pazarı. core/ yazıldı ve doğrulandı; uygulama kabuğu YOK.
windows/       TR pazarı. Henüz yazılmadı; mimari kararı bekliyor (README'ye bak).
conformance/   Ortak vektörler — iki agent da AYNI sonucu üretmek ZORUNDA.
```

**Geliştirici dokümanı `README.md` dosyalarındadır**, bu dosyada değil: bu dosya Claude içindir
(mimari kararlar ve yasaklar), `README.md` insan geliştirici içindir (protokol, kurulum, sözleşme).

Tasarım: [terminal-plugin-platformu.md](../firebase/functions/analysis/design/terminal-plugin-platformu.md)
§5.2, §5.3, §12.

## Bağlam

`core` **saf JVM** modülüdür; Android bağımlılığı YOKTUR ve bu bilinçlidir. §12.2'nin dokuz
değişmezinin çoğu (atomik dedupe, süre kontrolü, saat offseti, UNKNOWN akışı, retention) saf
mantıktır. Android modülüne gömülselerdi doğrulamak için cihaz/emülatör gerekirdi ve pratikte hiç
test edilmezlerdi — burada JVM'de saniyeler içinde koşuyorlar.

```
android/core/
  AgentState.kt    komut durum makinesi — GERİ KENAR YOK
  CommandStore.kt  SQLite WAL, ATOMİK dedupe
  ClockOffset.kt   sunucu saat offseti (monotonic)
  Backoff.kt       yeniden bağlanma, jitter'lı
```

## Kurallar

- **Dedupe atomik olmalı.** `INSERT OR IGNORE` + etkilenen satır sayısına bak. "Önce SELECT, yoksa
  INSERT" YASAK: oturum devrinde iki soket aynı komutu alır, ikisi de "yok" görür, komut İKİ KEZ
  terminale gider — müşterinin kartından iki kez çekilir.
- **Durum yazımı da atomik olmalı.** `UPDATE … WHERE state = <beklenen>`; kontrolü koda taşımak
  (oku, karşılaştır, yaz) aynı yarışı geri getirir.
- **Terminale ulaşmış olabilecek bir komut için SALE tekrarı YOK** (§12.2/6). `SENT_TO_TERMINAL` ve
  `UNKNOWN` yalnız SORGU ile çözülür.
- **Saat offseti bilinmiyorsa karar verilmez** (§5.3). `suresiGectiMi` `null` döner → `CLOCK_UNSYNCED`.
  Tahmin yok: cihaz saatine güvenmek, geçerli tahsilatları reddetmeye veya süresi geçmişleri
  çalıştırmaya yol açar.
- **Yeniden bağlanmada jitter ZORUNLU** (§5.2). Sabit gecikme, gateway kapandığında tüm agent'ları
  aynı saniyede geri döndürür (10.000 cihazda ~33 bağlantı/sn).
- **Kart verisi TUTULMAZ** (§12.3). `result_json` yalnız maskeli/referans alanları taşır.
- Retention uçuştaki komutu **asla** silmez — `UNKNOWN` bir kaydı silmek, çözülmemiş bir tahsilatı
  kaybetmektir.

## Yapma

- `android/core`'a Android bağımlılığı ekleme; eklendiği an testler cihaz ister ve koşulmaz olur.
- Protokolü `windows/README.md`'ye yeniden yazma — tek kaynak `android/README.md` §3-§5; iki kopya
  doküman kaçınılmaz olarak ayrışır.
- Uygunluk vektörlerini koda gömme; dosyadan oku. Gömülen kopya, dosya değişince güncellenmez.
- Agent'a **NATS credential'ı verme** (§12.2/8). Agent yalnız gateway'e WSS ile bağlanır.
- Platformun `stateMachine.js` sözlüğünü buraya kopyalama: platform bir ödeme DENEMESİNİ izler,
  agent kendi elindeki KOMUTU. Tek enum'a sıkıştırmak, agent'ın bilemeyeceği durumları
  (`UNRESOLVED`, `REVERSED`) agent'ın sorumluluğuymuş gibi gösterir.

## Derleme

Gradle 8.14 + Kotlin 2.1 **Java 25'i desteklemiyor**; `gradle.properties` Android Studio'nun
JBR'ını (Java 21) işaret eder.

```
JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home" ./gradlew :android:core:test
```
