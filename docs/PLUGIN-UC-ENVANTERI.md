# Restomenum ÖKC Eklentisi — Uç Envanteri (sözleşme referansı)

> Bu belge **ölçülmüş davranışı** anlatır, tasarım niyetini değil. Her madde koddaki
> satırla ve mümkün olduğunca sahadaki ölçümle bağlanmıştır.
> Kaynak: `restomenum-agent`, son güncelleme **2026-09-09** (W52–W62 ölçümleri işlendi).

## Özet

| # | Yön | Uç | Amaç |
|---|-----|----|------|
| G1 | kasa → ajan | `POST /nexo` (`MessageCategory: Payment`) | ödeme başlat |
| G2 | kasa → ajan | `POST /nexo` (`MessageCategory: Reversal`) | ödeme/fiş iptali |
| P1 | ajan → platform | `POST {EnrollUrl}` | ilk kayıt (tek kullanımlık kod) |
| P2 | ajan → platform | `POST {SessionUrl}` | cihaz oturumu (JWT) |
| P3 | ajan → platform | `GET {Plugins}plugin-api/payments/{paymentId}` | tutar çekimi (GET = ACK) |
| P4 | ajan → platform | `POST {Plugins}plugin-api/payments/{paymentId}/result` | ödeme sonucu |
| P5 | ajan → platform | `POST {Plugins}plugin-api/payments/ticket-cancel/result` | fiş iptali sonucu |
| P6 | ajan → platform | `POST {Plugins}plugin-api/payments/ticket-closed` | fiş kapandı + ödeme satırları |
| C1 | ajan → config | `POST {Config}api/device/session` | config kanalı oturumu |
| C2 | ajan → config | `POST {Config}api/device/departments` | cihaz departman tablosu |
| C3 | ajan → config | `GET {Config}api/device/mapping` | ürün/bölüm eşlemesi (ETag) |

**Taban adresler** (`appsettings.json` → `Agent`):
- `ListenPrefix` = `http://127.0.0.1:7788/`, `ListenPath` = `/nexo` — **yalnız loopback**
- `PluginsApiUrl` — P3–P6
- `SessionUrl` — P2 (P3–P6'dan AYRI adres)
- `EnrollUrl` — P1
- `DeviceConfigSetup` (base64: `{url, secret}`) — C1–C3 (yine AYRI adres)

---

## G1 — `POST /nexo` · `MessageCategory: Payment`

**Dinleyici:** `AgentWorker.HandleRequestAsync`. Yol `/nexo` değilse **404**, metot POST
değilse **405**. Önek loopback olduğu için ağdan erişilemez.

**Ayrıştırma:** `SaleToPoiRequestParser.Parse`. Zorunlu alanlar ve ret sebepleri:

| Kural | Ret |
|---|---|
| gövde JSON değil | `Malformed: JSON değil` |
| `SaleToPOIRequest` zarfı yok | `Malformed` |
| `MessageHeader` yok / `MessageCategory` yok | `Malformed` |
| kategori Payment/Reversal değil | `UnsupportedCategory` |
| `MessageClass`≠`Service` veya `MessageType`≠`Request` | `Malformed` |
| `ServiceID` boş veya >10 karakter | `InvalidServiceId` |
| `POIID` yok | `Malformed` |
| `SaleID` >32 karakter | `Malformed` |
| `PaymentRequest` yok | `Malformed` |
| **`RequestedAmount` zarfta VAR** | `AmountNotAllowed` |
| `SaleData`/`SaleTransactionID` yok | `Malformed` |
| `TransactionID` `pay_`+40hex değil | `InvalidPaymentId` |
| `TimeStamp` ISO-8601 değil | `Malformed` |
| `SaleReferenceID` yok | `Malformed` |

> **Neden `AmountNotAllowed`:** tutar zarftan DEĞİL, platformdan (P3) çekilir. Kasanın
> yazdığı bir tutarı kabul etmek, iki ayrı gerçek kaynağı olur ve hangisinin doğru
> olduğunu kimse bilemezdi.

**`Restomenum` uzantı bloğu** (opsiyonel, `SaleToPOIRequest` içinde):
- `saleSessionId` — kısmi ödemede **aynı fişe devam** anahtarı. Yoksa devam yolu kapalıdır.
- `bankBkmId` — kasiyerin seçtiği banka. **Yalnız** sayı ve `0 < x ≤ 65535` ise kabul;
  aksi halde alan yok sayılır ve bankayı cihaz seçer. (Kırpmak, seçilenden BAŞKA
  bankaya göndermek olurdu.)

**Yanıt:** `SaleToPOIResponse` / `PaymentResponse` — `Response.Result` = `Success` |
`Failure` | `InProgress`, `ErrorCondition`, `AdditionalResponse`.
`Restomenum` bloğunda: `ticketState` (`OPEN`/`CLOSED`), `ticketId`,
`deviceTicketTotalMinor`, `deviceRemainingMinor`, `bankBkmId`, `taxMismatches[]`
(`productCode`, `productRateBasisPoints`, `departmentIndex`, `departmentRateBasisPoints`).

**Akış (ölçülmüş aşamalar, `[yerel] süre dökümü`):**
`tutarMs` (P3, oturum dahil) → `eslemeMs` (C3, sert 2 sn) → `cihazMs` (terminal, kilit
dahil) → `bildirimMs` (P4 + P6). Tipik: cihaz ~7,8 sn; oturum soğuksa +4,6 sn.

---

## G2 — `POST /nexo` · `MessageCategory: Reversal`

**Ayrıştırma:** `ReversalRequestParser.Parse`. G1'in başlık kuralları aynen geçerli, ek olarak:

- `ReversalRequest` yok → `Malformed`
- `SaleData/SaleTransactionID` yok → `Malformed`; `pay_`+40hex değilse `InvalidPaymentId`
- **Referans zorunluluğu:** `OriginalPOITransaction.POITransactionID` **veya**
  `MessageReference.ServiceID` gerekli — **ancak `Restomenum.scope == "ticket"` ise ARANMAZ.**
  > Referanssız ödeme iptali "cihazda ne varsa iptal et" demektir; yanlış fişi iptal
  > etmek geri alınamaz. Fiş bazlı iptalde kasiyer cihazın başındadır ve ekranda fişi görür.

**`Restomenum` bloğu:** `scope` (`ticket` = fiş bazlı), `saleSessionId`, `ticketCancelId`.

**İki ayrı yol:**
1. `scope: ticket` → `FisIptalAsync` → cihazda `VoidAll` + `Close`, bağ silinir, **P5**'e bildirilir.
2. aksi halde → ödeme bazlı ters işlem (referansla).

⚠️ **Türkiye'de tek ödeme iptali CİHAZDA DESTEKLENMİYOR** (kullanıcı beyanı, 2026-09-08).
Sahada çalışan tek yol **fiş bazlı iptaldir**. (2) numaralı yol kodda duruyor ama
**sahada test edilmemiştir ve edilemez**. `[VARSAYIM]`

**Yanıt:** `ReversalResponse` + `Restomenum`: `scope`, `saleSessionId`,
`cancelledSaleSessionId`, `ticketId`, `ticketCancelId`, `voidedPaymentCount`,
`voidedAmountMinor`, `info`, `reason`, `paymentInvoked`.

---

## P1 — `POST {EnrollUrl}` · ilk kayıt

Gövde: `serverId` + `data{ code, publicKey, fingerprint, platform:"windows", version }`.
`WindowsEnrollment`. Tek kullanımlık `EnrollmentCode` ile cihazı kaydeder, dönen
`connectorId` **diske** yazılır. Kayıtlıysa çağrılmaz. Başarısızsa **host hiç çalışmaz**
(fail-closed) — yarı kayıtlı bir cihazla satışa girilmez.

## P2 — `POST {SessionUrl}` · cihaz oturumu

Gövde: `serverId` + `data{ connectorId, nonce, **timestamp**, fingerprint, signature }`.
`timestamp` **JSON NUMBER**'dır (`HttpSessionProvider.cs:141` — `long.Parse(ts)`), string değil.
İmza **DER** kodlu olmak zorundadır (ham `r‖s` reddedilir).
Yanıt: `success`, `data.token` (JWT), `data.expiresInSec`, `serverTime`.
`serverTime` ile yerel saat farkı senkronlanır; sunucu `stale` derse **tek** tekrar.

**`expiresInSec` = 300 (ÖLÇÜLDÜ, 900 DEĞİL).** Belgede uzun süre 900 yazıyordu; W53 ömrü
günlüğe yazdırınca saha iki kez **300** gösterdi (2026-09-09 00:42 ve 01:03). Kodun
varsayılanı da 300 (`HttpSessionProvider.cs:169`) — yanıt alanı düşerse uydurma bir ömürle
devam etmemek için.

**W52:** jeton `expiresInSec − 60 sn` boyunca **önbelleklenir** (yalnız bellekte), yani
pratik geçerlilik **240 sn**. 401 → **tek** geçersiz kılma + **tek** tekrar.
Ölçüm: önbellek öncesi satış başına **3 tur**, sonrası **1**; soğuk başlangıçta tur
maliyeti 4,59 sn. Marjın davranışsal teyidi (2026-09-09 01:07): jeton 01:03:55'te alındı,
01:07:34'teki satış (219 sn sonra) **`oturumHttp = 0`** ile geçti — 240 sn penceresinin
içinde; 21 saniye sonra gelseydi yeni bir tur atılacaktı.

## P3 — `GET plugin-api/payments/{paymentId}` · tutar çekimi

**GET = ACK.** Bu çağrı başarılı olmadan terminale gidilmez.
Yanıt `data` → `PaymentTransaction.AmountsReq.RequestedAmount`, `SaleItem[]`,
`RestomenumExt.{Exponent, SaleTotalAmount, State, ExpiresAt, ItemsScope, PaymentMethodId}`.
`ItemsScope` ∈ `currentTicket` | `fullSale` | `none` — **eklenti bu alanı YOK SAYIYOR**
(ayrıştırılır, hiçbir kararda okunmaz). Portal düzeltmesi 2026-09-08; ilk yazımımdaki
`"all"|"partial"` **uydurmaydı**.

Ret kodları → sebep: `plugin.connector.unauthorized`→Unauthorized ·
`plugin.payment.notFound` · `plugin.payment.expired` · `plugin.payment.notActionable` ·
`plugin.payment.amountWindowClosed` · `plugin.payment.saleItemsUnavailable` ·
`plugin.rateLimited`. 200 ama beklenmedik şekil → `Unknown` (uydurma yok).

## P4 — `POST plugin-api/payments/{paymentId}/result` · ödeme sonucu

Gövde = G1'in yanıt gövdesinin aynısı (**tek üretici**, `SaleToPoiResponseBuilder`).
**Dayanıklı:** önce `outbox`a yazılır, sonra POST edilir.

**Platform yanıt semantiği** (`ResultNotifyParser`):

| HTTP | Sonuç | Outbox |
|---|---|---|
| 200, `recorded != false` | `Recorded` | silinir |
| 200, `recorded == false` | `Superseded` | silinir |
| 400 | `Rejected` | silinir (**alarm**) |
| 404 | `NotFound` | silinir (**alarm**) |
| 409 | `Conflict` | silinir (**alarm**) |
| 429 | `RateLimited` | **kalır**, geri çekilme |
| 5xx / 401 / diğer | `NetworkError` | **kalır**, geri çekilme |

`replayed` ve `alreadyPosted` yalnız teşhise taşınır; **karar vermez**. Alan gelmezse
`null` kalır — "gelmedi" ile "false" ayrı beyanlardır.

### P4 varyantı — onayın geri çekilmesi (`LANDED_RETRACTED`)

Ayrı uç **değildir**: aynı `payments/{paymentId}/result` ucuna,
`Restomenum.info = "LANDED_RETRACTED"` taşıyan bir gövdeyle POST edilir.

Gövde `Result: "Failure"` + `ErrorCondition: "InProgress"` ile gider — `Success`
**DEĞİL**, çünkü `Success ⇒ AuthorizedAmount zorunlu` değişmezi çiğnenirdi ve
`PaymentResult` boş olduğu için mesaj kendi içinde tutarsız olurdu.

**Bu neden çalışıyor** (platform tarafı ölçüldü, `statusSink.js:171`): koşul
`providerInfo === 'LANDED_RETRACTED' && attempt.state === 'APPROVED'` — buradaki
`attempt.state` **defterdeki önceden onaylanmış denemenin** durumudur, gövdedeki
`Result` değil. Gövde `Failure` olduğu için `status:'unknown'` sınıfına düşer ve o
sınıf için 171'den önce erken dönüş yoktur. Platform `{recorded:false,
reason:'landedRetracted'}` döner. **İki tarafta da değişiklik gerekmiyor.**

**Yalnız operatör tetikler:** `--retract <commandId>`. Otomatik tetikleyici YOK —
hayaletin nasıl oluştuğuna dair güvenilir bir sinyal olmadığı için uydurma tetikleyici
doğru onayları bozardı. Cihaza DOKUNMAZ. Başarı ölçütü `Outcome == Recorded`.

`[VARSAYIM]` Bu yol sahada hiç çalıştırılmadı.

## P5 — `POST plugin-api/payments/ticket-cancel/result` · fiş iptali

Ödeme ucundan **AYRI**: iptal bir denemeye değil FİŞE aittir.
Tekilleştirme anahtarı `ticketCancelId`. Dayanıklı (outbox önce).
Sahada ölçüldü: ağ kesikken iptal cihazda tamamlandı, bildirim kuyrukta
30→60→120→240 sn geri çekilmeyle bekledi, bağlantı gelince **1,5 sn**de gitti.

## P6 — `POST plugin-api/payments/ticket-closed` · fiş kapanışı

⚠️ **K-33 sonrası bu bir MUTABAKAT çağrısıdır, "deftere ilk yazım" DEĞİL.** Kısmi
ödemeler platforma **onay anında** (P4, `ticketState:"OPEN"`) bildirilir ve o an
deftere yazılır; kapanışta satırlar çoğunlukla **zaten yazılmıştır**. Platform
`posted:0, alreadyPosted:N` (+ varsa `mismatch.unlistedPayments`) döner, **silme yok**.
Eklenti tarafı değişmedi: `payments[]` her zaman fişin **TÜM** satırlarını taşır.
Gövde: `ticketId`, `saleSessionId`, `ticketState: CLOSED`, `totalMinor`, `paidMinor`,
`payments[]` (`paymentId`, `amountMinor`, `methodType` ∈ `cash|card|qr|bilmiyorum`, `bankBkmId`).
Tekilleştirme anahtarı **`ticketId`** (`paymentId` OLAMAZ — bildirim bir denemeye ait değil).

## C1–C3 — config kanalı (ayrı adres, ayrı sır)

- **C1** `POST api/device/session` — `{enrollmentSecret}` → oturum.
- **C2** `POST api/device/departments` — dört alan gider:
  `departments[]` (`index`, `name`, `taxRateBasisPoints`), `deviceInfo`, `capabilities`,
  `paymentApplications`. **Yalnız eşleşmeden SONRA** okunabilir (cihaz eşli değilken
  `GetDepartments` çalışmaz).
  - **`taxRateBasisPoints` `null` olabilir** ve bu "%0" DEĞİL, "oran bilinmiyor" demektir
    (cihazın vergi indeksi tabloda karşılık bulamadı). Uydurma 0 yazılmaz.
  - **Bildirim tabloyu DEĞİŞTİRİR, birleştirmez.** Bu yüzden tablo *boş okunursa* bildirim
    hiç gönderilmez: boş liste platformdakini kırpar ve eşleme ekranı çalışmaz hâle gelir.
    "Okunamadı" (rc≠0) ile "boş okundu" (rc=0, 0 kayıt) ayrı ele alınır; ikisi de eski
    tabloyu olduğu yerde bırakır.
  - **`null` alan = alan yok = KORUMA.** Gövde varsayılan seçeneklerle serialize edildiği
    için `null` alanlar tele *çıkar* (`"paymentApplications": null`), ama eklenti tarafında
    alanın olmaması ile `null` olması aynı muamele görüyor: ikisi de saklananı korur, yalnız
    **boş dizi** listeyi siler (a7 gerçek uçtan ölçtü, 2026-09-09). Aynısı `deviceInfo` için
    de geçerli.
  - **`capabilities`** — ajanın beyan ettiği op'lar; kasa/panel düğmelerini buna göre
    gösterir. Kural: **ölçülmemiş yetenek beyan edilmez.** Bugün yedi op:
    `sale.cash`, `sale.card`, `void` + (W55, `--capability-probe` ile ölçüldü)
    `sale.giftVoucher`, `sale.points`, `sale.bankTransfer`, `sale.cheque`.
    **Beyan EDİLMEYENLER:** `sale.mealVoucher` (8) ve `sale.mobileWallet` (16) — DLL
    tablosunda ikisinin de yanında "(Uygulama varsa)" yazıyor ve bu terminalde öyle bir
    uygulama yok (`--payment-apps`: 4 kayıt, dördü de banka kartı); `sale.foreignCurrency`
    (2) — ajan her tutarı TL gönderiyor.
- **C3** `GET api/device/mapping` — `If-None-Match: W/"v{n}"`; **304 = değişmedi**.

### Eşleme ve tablo hangi tetikle tazelenir

| ne | tetik | notlar |
|---|---|---|
| **Eşleme** (C3'ten ÇEKME) | açılış + **~30 dk ± %20** yoklama | `DeviceConfigPoller`; jitter şart (aynı işletmede çok cihaz aynı saniyede vurmasın) |
| **Eşleme** (satış öncesi) | her satıştan hemen önce, **sert 2 sn** | W29; hata yutulur, diskteki sürümle devam — satışı **asla** düşürmez |
| **Cihaz bölüm tablosu** (C2'ye BİLDİRME) | eşleşme (`--pair`, `--payment-apps`, `--void`, `--cancel-ticket`) **+ yoklama turu (W56)** | W56 öncesi TEK yol eşleşmeydi ve terminalde değişen bir KDV oranı, biri yeniden eşleştirene kadar görünmüyordu |

**W56 kuralları:** tazeleme cihazı **satışın kilidinden** alır ve **beklemez** (meşguldü →
tur atlanır, satış önceliklidir); tablo **yalnız platformdakinden farklıysa** bildirilir
(her turda bildirmek eşleme sürümünü boşuna artırır ve "sürüm değişti" sinyalini
anlamsızlaştırır); okunamayan/boş tablo **bildirilmez**; `null` oran ile `0` oran **ayrı**
sayılır.

---

## Değişmezler (bütün uçlar için)

1. **Tutar tek kaynaktan:** platformdan (P3). Zarftaki tutar reddedilir.
2. **Dayanıklılık:** para taşıyan her bildirim önce `outbox`a yazılır, sonra gönderilir.
   Vazgeçme/silme/retention **yok**; geri çekilme 30/60/120/240/300 sn'de tavan yapar.
3. **Bağlantı geri gelince** açılış drain'i geri çekilmeyi **bir turluk atlar**
   (sahada 194 sn marjla kanıtlandı).
4. **Belirsizlik ≠ ret:** `FP3_Payment` çağrıldıysa sonuç terminale sorularak çözülür,
   asla yeniden gönderilmez.
5. **Uydurma yok:** okunamayan alan `null` kalır; "veri yok" ile "sıfır/boş" ayrı yazılır.
6. **Jeton ve `Authorization` hiçbir loga girmez** — yapısal: HTTP istemcilerinin
   günlükçü bağımlılığı yoktur.
7. **Terminal başına TEK işlem:** satış, iptal, kurtarma ve bakım turu **aynı** kilidi
   alır. İkinci bir kilit açmak, kuralı iki yerde tanımlamak ve birinin ötekini görmemesi
   demek olurdu.
8. **Kilit cihazı korur, defteri korumaz.** Satış kilidi yalnız cihaz çağrısı boyunca
   tutulur (bildirimi beklemek kurtarmayı ve iptali satışın BULUT turu kadar geciktirirdi).
   Defteri koruyan ayrı bir kural var: kurtarma turu **bu süreçte uçuşta olan** komutlara
   dokunmaz (W62). Sahada ölçüldü — kurtarma, hâlâ koşan bir komutun adına P4 gönderdi ve
   yoklamasını KAPATTI; satış o an çökseydi kayıt sessizce kaybolurdu.
9. **Ölçülmemiş yetenek beyan edilmez.** Beyan, kasiyere "bu düğme çalışır" sözü vermektir;
   çalışmayan bir düğme, olmayan düğmeden kötüdür. Yeni bir ödeme tipi için sıra hep aynı:
   önce `--capability-probe` ile **ölç**, sonra `capabilities`'e ekle.

---

## Cihaz olguları (ölçüldü, belgede yok)

Bunlar üretici belgesinde yazmıyor; sahada ölçülüp burada kayda geçti.

- **Mali fiş numarası (`FNo`) fiş AÇILIRKEN verilir**, kapanırken değil: başarılı bir
  `ItemSale`'in kendi yanıtı `ZNo`/`FNo` taşır.
- **Yazdırılmadan iptal edilen fiş de `FNo` TÜKETİR.** 2026-09-09, ZNo 87 içinde üç kez
  ölçüldü: 3 (satış) → 4 (`--capability-probe`) → 5 (iptal edilen fiş) → 6 (satış).
  **Sayaçtaki boşluk bir kayıp değil, iptal edilmiş fişlerin izidir.**
- **`ItemSale` HATA dönerse yanıtında sayaçlar SIFIR gelir** (`ZNo=0, FNo=0`); numarayı
  görmek için `VoidAll`/`GetTicket` yanıtına bakılır.
- **Fiş limiti** `ItemSale`'den **2067** (`APP_ERR_FIS_LIMITI_ASILAMAZ`) ile döner; ödeme
  aşamasına hiç gelinmez. Limit DEĞERİ hiçbir çağrıdan okunamıyor (`GMP.XML`'de de yok);
  ölçülen tek sınır: **≤ 100.000,00 TL**.
- **KDV'yi cihaz DEPARTMANDAN türetir**; telde `taxRate` her zaman `0` gider. Doğrulandı:
  dept 10 (%10) 891 kuruşluk kalem → `TotalReceiptTax` **81** = 891×10/110.
- **`bankBkmId = 0` = "cihaz seçsin"** ve cihaz aynı fişte art arda iki denemede **farklı**
  banka seçebiliyor (GARANTİ 62 → AKBANK 46).
- **Kurulu ödeme uygulamaları ≠ desteklenen ödeme tipleri.** `FP3_GetPaymentApplicationInfo`
  yalnız **banka kartı** uygulamalarını listeler. Hediye çeki / puan / havale / çek
  uygulama gerektirmiyor (DLL tablosunda "(Uygulama varsa)" notu yalnız yemek çeki ve
  mobilde var) ve dördü de `FP3_Payment` ile **rc=0** kabul edildi.
