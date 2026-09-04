# Restomenum Terminal Agent — Android

POS ödeme terminalini süren cihaz-üstü agent'ın **Android** tarafı. Almanya pazarı için hedef
platform budur (TR → Windows, bkz. [`../windows/`](../windows/)).

> **Durum:** `core/` yazıldı ve doğrulandı (13/13). **Uygulama kabuğu HENÜZ YOK** — bu dosya onu
> yazacak geliştirici için yazıldı.

---

## 1. Agent ne yapar

```
Kasiyer "Kredi Kartı" der
        │
        ▼
 Restomenum backend ──── JetStream ────► dispatcher ────► gateway (WSS)
                                                              │
                                                              ▼
                                                        AGENT (bu proje)
                                                              │
                                                     yerel ağ / USB / BT
                                                              ▼
                                                        POS TERMİNALİ
```

Agent **ayrı bir servistir**, POS uygulamasının içinde değil (karar K-02):

- POS kapalıyken de ayakta kalır (gün sonu, ikinci kasa, kiosk)
- Tek terminale birden çok kasiyer istasyonu bağlanabilir
- Cihaz yeniden başlayınca kendi başına döner

Android'de bu, **`connectedDevice` tipinde foreground service** demektir.

---

## 2. Neden `core/` ayrı ve saf JVM

`core/` Android'e hiç bağımlı değildir ve bu **bilinçli**. §12.2'nin dokuz değişmezinin çoğu saf
mantıktır: atomik dedupe, durum makinesi, saat offseti, jitter, retention. Android modülüne
gömülselerdi doğrulamak için cihaz veya emülatör gerekirdi ve pratikte **hiç test edilmezlerdi**.

Ayrık olduğu için 8 iş parçacığıyla gerçek yarış senaryosu saniyeler içinde koşuluyor:

```bash
JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home" \
  ./gradlew :android:core:test
```

> Gradle 8.14 + Kotlin 2.1 **Java 25'i desteklemiyor**; `gradle.properties` Android Studio'nun
> JBR'ını (Java 21) işaret eder. Kendi JDK'nızı kullanacaksanız 17–21 arası olmalı.

**Kabuğu yazarken bu mantığı Android tarafına kopyalamayın.** Kopya, kaçınılmaz olarak ayrışır;
ayrışan bir dedupe mantığının bedeli **çift tahsilattır**.

---

## 3. Telin üzerindeki protokol

Agent gateway'e **tek bir WSS bağlantısı** açar ve JSON frame'leri konuşur.

**Uç:** `wss://<gateway-host>/v1/agent`

### 3.1 El sıkışma

Bağlanır bağlanmaz `hello` gönderilmelidir. **10 saniye** içinde gelmezse gateway bağlantıyı
`4408` ile kapatır (kimliksiz soket tutulmaz).

```jsonc
// agent → gateway
{ "type": "hello", "token": "<session JWT>", "version": "1.0.3" }

// gateway → agent
{ "type": "hello.ok", "gatewayId": "gw-a1b2c3", "serverTime": 1788544448470 }
```

`token`, `POST /v1/connectors/session` ucundan alınır (bkz. §4).

**`serverTime` ATLANMAMALIDIR** — cihaz saatine güvenilmez (§5.3). `ClockOffset.senkronla()`
çağırın; offset bilinmeden komut çalıştırmak yasaktır.

### 3.2 Komut

```jsonc
// gateway → agent
{
  "type": "command",
  "requestId": "9f2a...",          // ACK'te AYNEN geri gönderilmeli
  "v": 1,
  "tenantId": "srv123",
  "connectorId": "conn9",
  "command": {
    "type": "payment",
    "capability": "payment.terminal",
    "commandId": "pay_01J...",     // DEDUPE ANAHTARI
    "paymentId": "pay_01J...",
    "expiresAt": 1788544508470,
    "payload": {
      "terminalId": "kasa-1",
      "amountMinor": 24000,
      "currency": "TRY",
      "exponent": 2,
      "allowTip": false
    }
  }
}

// agent → gateway (KOMUTU ALDIM — sonuç DEĞİL)
{ "type": "command.ack", "requestId": "9f2a...", "accepted": true }
```

**`command.ack` "aldım ve dayanıklı olarak yazdım" demektir, "tahsilat başarılı" demek DEĞİLDİR.**
Gateway 3 saniye içinde ACK görmezse kabul etmez ve dispatcher komutu JetStream'de bırakır. Yani:

- `accepted:true` göndermeden **önce** komutu `CommandStore.kaydet` ile yazın (§12.2/1)
- Yazamadıysanız `accepted:false` gönderin — komut kaybolmaz, yeniden teslim edilir

### 3.3 Yeniden kimlik doğrulama

Session JWT'nin ömrü 5 dakikadır ama **soketin ömrü ona bağlı değildir**. Agent, TTL'in yarısında
yeni token sunar:

```jsonc
{ "type": "reauth", "token": "<yeni JWT>" }   // agent → gateway
{ "type": "reauth.ok", "serverTime": 1788544600000 }
```

Başka bir `connectorId`'nin token'ıyla `reauth` denemesi bağlantıyı `4401` ile kapatır.

> **Neden soket kapatılmıyor:** `exp` anında soketi kapatmak 10.000 cihazda saniyede ~33 yeniden
> bağlanma üretirdi. Kimlik bağlantı içinde tazelenir.

### 3.4 Kapanma kodları

| Kod | Anlamı | Agent ne yapmalı |
|-----|--------|------------------|
| `4401` | Token geçersiz/süresi geçmiş | Yeni session al, sonra bağlan |
| `4403` | Oturum iptal edildi | **Yeniden denemeyin** — cihaz devre dışı bırakılmış |
| `4408` | `hello` zamanında gelmedi | Hata; bağlanır bağlanmaz `hello` gönderin |
| `4409` | Aynı connector başka soketle bağlandı | Bu soket eskidir, sessizce bırakın |
| `4503` | Gateway kapanıyor | **Jitter'lı** yeniden bağlanma (`Backoff`) |

---

## 4. Kimlik (enrollment + session)

### 4.1 Enrollment — cihaz bir kez kaydolur

1. Panelden tek kullanımlık kod üretilir (TTL 10 dk)
2. Agent **cihazda** anahtar çifti üretir — **Android Keystore**, `setUserAuthenticationRequired(false)`,
   mümkünse donanım destekli ve **dışa aktarılamaz**
3. `POST /v1/connectors/enroll` ← kod + public key (PEM/SPKI) + **cihaz parmak izi** + platform
4. Backend cihazı tenant'a bağlar

> **Cihaz parmak izi neden var:** restoranlarda yeni kasa kurarken eskisinin diski/imajı kopyalanır.
> Anahtar tek başına kimlik olsaydı klon, ikinci makinede aynı `connectorId` ile oturum açar ve iki
> makine aynı terminali sürerdi. Parmak izi uyuşmazlığı **reddedilir ve alarm üretir**.
>
> Android'de parmak izi için `Settings.Secure.ANDROID_ID` + donanım imzası gibi **cihaza bağlı ve
> fabrika ayarına kadar kalıcı** bir değer türetin. Rastgele üretip `SharedPreferences`'a yazmayın —
> imaj kopyalandığında o da kopyalanır ve korumanın tamamı boşa çıkar.

### 4.2 Session — her bağlantıdan önce

```
POST /v1/connectors/session
{ "serverId": "...", "data": {
    "connectorId": "conn9",
    "nonce": "<rastgele, tek kullanımlık>",
    "timestamp": 1788544448470,
    "fingerprint": "<cihaz parmak izi>",
    "signature": "<base64>"
}}
```

İmzalanan **kanonik dize** (uzunluk önekli — düz birleştirme belirsizdir):

```
"<len>:<connectorId>|<len>:<nonce>|<len>:<timestamp>"
```

Örnek: `connectorId="conn9"`, `nonce="abc"`, `timestamp=1788544448470` →
`5:conn9|3:abc|13:1788544448470`

Bunu Keystore'daki özel anahtarla imzalayın (Ed25519 / EC P-256 / RSA-PSS desteklenir).

Yanıt: `{ token, expiresInSec: 300, serverTime }`.

**Ret her zaman aynıdır** (`plugin.connector.unauthorized`) — sebep bilinçli olarak sızdırılmaz.
`timestamp` ±60 sn penceresi dışındaysa `staleRequest` döner; `serverTime` ile offset'inizi
düzeltip **bir kez** tekrar deneyin.

---

## 5. Dokuz değişmez — pazarlık konusu DEĞİL

Her biri gerçek bir para hatasına karşılık gelir. `core/` bunların çoğunu zaten uygular; kabuğu
yazarken **etrafından dolaşmayın**.

| # | Kural | İhlalin bedeli |
|---|-------|----------------|
| 1 | Komut dayanıklı store'a **yazılmadan** `accepted:true` gönderilmez | Crash'te komut kaybolur, "aldım" demişsinizdir |
| 2 | Dedupe **atomik** (`INSERT OR IGNORE` + `changes()`) | Oturum devrinde iki soket aynı komutu alır → **çift tahsilat** |
| 3 | Aynı `commandId` ikinci kez gelirse terminal **çağrılmaz**, önceki sonuç replay edilir | Çift tahsilat |
| 4 | Terminal başına tek aktif finansal işlem | Cihaz ikinci komutu sıraya alır, müşteriye iki kez kart okutur |
| 5 | `expiresAt` geçmişse SALE çalışmaz; offset bilinmiyorsa `CLOCK_UNSYNCED` | Kasiyerin vazgeçtiği tahsilat dakikalar sonra terminale düşer |
| 6 | `SENT_TO_TERMINAL` sonrası otomatik SALE retry **YOK** → `UNKNOWN` akışı | Çift tahsilat |
| 7 | Bulut koparsa terminal işlemi devam eder; sonuç local outbox'ta bekler | Tahsilat gerçekleşir ama deftere hiç yazılmaz |
| 8 | Agent **hiçbir NATS credential'ı taşımaz** | Cihaz ele geçirilirse tüm kuyruğa erişim |
| 9 | Private key OS secure store'da, dışa aktarılamaz | Anahtar kopyalanır, klon cihaz oturum açar |

`core/` şu an 1, 2, 3, 5, 6 ve retention'ı uygular. **4, 7, 8, 9 kabuğun sorumluluğudur.**

---

## 6. `core/` sözleşmesi

```kotlin
// Komut geldi — ÖNCE yaz, sonra ACK gönder (§12.2/1)
val store = CommandStore.ac("$filesDir/agent.db")
when (val r = store.kaydet(commandId, paymentId, terminalId, expiresAt)) {
    is KayitSonucu.Yeni   -> { /* accepted:true gönder, sonra terminale git */ }
    is KayitSonucu.Tekrar -> { /* accepted:true gönder, TERMİNALE GİTME, r.komut'u replay et */ }
}

// Süre kontrolü — cihaz saatine GÜVENME
val saat = ClockOffset()
saat.senkronla(helloOk.serverTime)          // her hello.ok ve heartbeat'te
when (saat.suresiGectiMi(cmd.expiresAt)) {
    null  -> reddet("CLOCK_UNSYNCED")       // offset bilinmiyor → TAHMİN YOK
    true  -> store.ilerlet(id, RECEIVED, EXPIRED)
    false -> { /* devam */ }
}

// Durum ilerletme — beklenen durumu VER (yarış koruması)
store.ilerlet(id, RECEIVED, SENT_TO_TERMINAL)          // → true yazıldıysa
store.ilerlet(id, SENT_TO_TERMINAL, COMPLETED, resultJson = "...")

// Yeniden bağlanma
val backoff = Backoff()
backoff.sonraki()      // 500, 1000, 2000, 5000, 10000, 30000 ms (±%20 jitter)
backoff.sifirla()      // BAŞARILI bağlantıdan sonra
```

`ilerlet` `false` dönerse **yazma olmamıştır** — durum beklediğinizden farklıydı. Sessizce
geçmeyin; loglayın.

---

## 7. Kabukta yapılacaklar (henüz YOK)

| İş | Not |
|----|-----|
| `connectedDevice` foreground service | Kalıcı bildirim, boot receiver, `START_STICKY` |
| Keystore ile anahtar üretimi + enrollment | §4.1; dışa aktarılamaz olmalı |
| Cihaz parmak izi türetimi | İmaj kopyalandığında **değişmeli** |
| WSS istemcisi | OkHttp; §3'teki frame'ler; `Backoff` ile yeniden bağlanma |
| Terminal transport'u | §8.3 profilleri: `http-local`, `tcp-length-prefix` |
| Event outbox | §12.2/7 — bulut koparsa sonuç burada bekler, dönünce replay |
| Terminal semaforu | §12.2/4 — terminal başına tek aktif işlem |

**Kart verisi ASLA saklanmaz** (§12.3). `result_json` yalnız maskeli/referans alanları taşır:
`rrn`, `stan`, `approvalCode`, `cardLast4` (4 hane), `scheme`. PAN, CVV, manyetik şerit — hiçbiri.

---

## 8. Nereye bakılır

- **Protokol ve kararların tamamı:** [`terminal-plugin-platformu.md`](../../firebase/functions/analysis/design/terminal-plugin-platformu.md)
  — §5 (kimlik), §8 (Device Channel), §12 (agent), §17.2 (fault suite)
- **Gateway kaynağı** (telin diğer ucu): [`gcloud/payment-transport/gateway.js`](../../gcloud/payment-transport/gateway.js)
- **Session ucu:** [`functions/api/connectors/session.js`](../../firebase/functions/api/connectors/session.js)
- **Uygunluk vektörleri:** [`../conformance/`](../conformance/) — Windows agent'ı ile **aynı**
  davranışı kanıtlamak zorundasınız
