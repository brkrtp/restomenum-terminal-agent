# Restomenum ÖKC Eklentisi — Uç Envanteri (sözleşme referansı)

> Bu belge **ölçülmüş davranışı** anlatır, tasarım niyetini değil. Her madde koddaki
> satırla ve mümkün olduğunca sahadaki ölçümle bağlanmıştır.
> Kaynak: `restomenum-agent`, 2026-09-08.

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

Gövde: `serverId` + `data{ connectorId, nonce, ts, fingerprint, signature }`.
İmza **DER** kodlu olmak zorundadır (ham `r‖s` reddedilir).
Yanıt: `success`, `data.token` (JWT), `data.expiresInSec` (900), `serverTime`.
`serverTime` ile yerel saat farkı senkronlanır; sunucu `stale` derse **tek** tekrar.

**W52:** jeton `expiresInSec − 60 sn` boyunca **önbelleklenir** (yalnız bellekte).
401 → **tek** geçersiz kılma + **tek** tekrar. Ölçüm: önbellek öncesi satış başına
**3 tur**, sonrası **1**; soğuk başlangıçta tur maliyeti 4,59 sn.

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
- **C2** `POST api/device/departments` — cihazdan okunan departman tablosu
  (`index`, `name`, `taxRateBasisPoints`) + `deviceInfo`. **Yalnız eşleşmeden SONRA** okunabilir.
- **C3** `GET api/device/mapping` — `If-None-Match: W/"v{n}"`; **304 = değişmedi**.
  Satıştan hemen önce **sert 2 sn** zaman aşımıyla tazelenir; hata yutulur ve diskteki
  sürümle devam edilir — bu çağrı satışı **asla** düşürmez.

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
