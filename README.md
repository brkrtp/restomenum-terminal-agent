# Restomenum Terminal Agent

POS ödeme terminallerini süren cihaz-üstü agent. İki platform, tek sözleşme.

```
android/       Kotlin — DE pazarı.  core/ yazıldı ve doğrulandı; uygulama kabuğu henüz yok.
windows/       TR pazarı.           Henüz yazılmadı; mimari kararı bekliyor.
conformance/   Ortak vektörler.     İki agent da AYNI sonucu üretmek ZORUNDA.
```

## Nereden başlanır

- **Protokol, kimlik akışı ve dokuz değişmez:** [`android/README.md`](android/README.md) — tek
  kaynak, platformdan bağımsız
- **Windows'a özgü kararlar ve TR/ÖKC notu:** [`windows/README.md`](windows/README.md)
- **Neden ortak vektörler:** [`conformance/README.md`](conformance/README.md)
- **Tasarımın tamamı:** [`terminal-plugin-platformu.md`](../firebase/functions/analysis/design/terminal-plugin-platformu.md)

## Test

```bash
JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home" \
  ./gradlew :android:core:test
```

17 test: 13 değişmez testi + 4 uygunluk vektörü testi.

> **JDK 17–21 gerekiyor.** Gradle 8.14 + Kotlin 2.1 **Java 25'i desteklemiyor**. Sisteminizde 25
> varsa `JAVA_HOME`'u 21'e yönlendirin (Android Studio'nun JBR'ı iş görür) ya da kendi
> `gradle.properties` dosyanızı oluşturup `org.gradle.java.home=...` yazın. O dosya makineye özgü
> olduğu için **depoda tutulmuyor**.
