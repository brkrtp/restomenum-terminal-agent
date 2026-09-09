# W79 — "tahsil edilen ≠ istenen" bildirilsin — **SON HÂL** (tasarım, KOD YOK)

> Bu belge P138 paketinin sözleşmesidir. Bütün maddeler 2026-09-09 saha ölçümlerine dayanır;
> ölçülmemiş olanlar açıkça öyle işaretlidir. **Kod, bu tasarım onaylandıktan sonra yazılacak.**

**Durum:** tasarım. Kampanya ortasında yayım yapılmayacak; bu belge uygulanacak değişikliğin
sözleşmesidir.

## Neden (ölçüldü, 2026-09-09)

Banka uygulaması fişin **mali toplamını** düşürebiliyor:

| fiş | istenen | tahsil | indirim | oran | banka |
|---|---|---|---|---|---|
| 88/2 | 7150 | 6793 | 357 | %4,993 | GARANTİ BBVA (62) |
| 88/4 |  990 |  941 |  49 | %4,949 | GARANTİ BBVA (62) |

İki ölçümde de tekrarladı → **tek seferlik değil.** İndirim KDV matrahını da düşürüyor
(979→930, 90→86). Sonuç: müşterinin kartından 67,93 TL çekildi, kasa 71,50 TL sandı —
**3,57 TL gerçek para farkı**, alarmsız.

## Bugünkü kusurun tam yeri

- `GmpTerminalTransport.cs:338` — `ApprovedAmountMinor: request.AmountMinor`
  → platforma **İSTENEN** tutar gidiyor (`SaleToPoiResponseBuilder.cs:60-65` bunu
  `AuthorizedAmount` olarak yazıyor).
- `GmpTerminalTransport.cs:248` — `RecordTicketPayment(fisId, request.PaymentId,
  request.AmountMinor, …)` → defter satırı da **istenen** tutarı yazıyor
  (`CommandStore.cs:514`). Sonuç: `ticket_payments` 990 iken `closed_tickets` 941.
- `GmpTerminalTransport.cs:245` — `var banka = sonSatir is { } sat && sat.AmountMinor ==
  request.AmountMinor ? sat.BankBkmId : null` → tutar tutmadığı için **banka düşürüldü**
  (`bank_bkm_id` NULL), oysa cihaz `bkm 62` diyor.

**Emsal (aynı kod tabanında doğru yapılmış):** `GmpTerminalTransport.Okuma.cs:254` kurtarma
yolunda `ApprovedAmountMinor: delta` — yani **gerçekten yazılan** tutar. Ana yol bundan
sapıyor; W79 ana yolu emsale hizalar.

## Değişiklik sözleşmesi

### 1. Tahsil edilen tutarın kaynağı
Bu denemenin cihazdaki satırı = **son dolu ödeme satırı** (`GmpTerminalTransport.cs:244`,
başarısızlık yolunda `:214` zaten aynı kuralı kullanıyor). `Collected = sonSatir.AmountMinor`.
**Çapraz doğrulama:** `delta = tk.PaidAmountMinor − (ödeme öncesi PaidAmountMinor)`.
İkisi eşitse tutar iddia edilir; **eşit değilse hiçbir tutar iddia edilmez** (`Unknown`) —
"bilmiyorum", yanlış tutardan iyidir.

### 2. Yeni alanlar (`TerminalTransport.cs:144` ve `:320` kayıtları)

⚠️ **İLK TASARIMIM YANLIŞTI, PLATFORM ÖLÇÜMÜYLE DÜZELTİLDİ.** "`ApprovedAmountMinor` =
tahsil edilen olsun" demiştim; platform tarafında `statusSink.js:236-240` onaylı sonuçta
**bildirilen tutar `attempt.amountMinor`'a eşit değilse sonucu HİÇ KAYDETMİYOR** (K-10
kapalı). Yani o değişiklik ıraksamayı düzeltmez, **sonucu tamamen kaybettirirdi** — bugünkü
sessiz 357 kuruşluk fark yerine sessiz bir "sonuç yok" üretirdi. Doğrusu:

- `ApprovedAmountMinor` → **İSTENEN tutar OLARAK KALIR** (platform eşleştirmesi buna bağlı).
- `CollectedAmountMinor` (yeni) — cihazın gerçekten tahsil ettiği.
- `MerchantDiscountMinor` (yeni) — `İstenen − Tahsil`, yalnız **> 0** ise konur.
- `RequestedAmountMinor` (yeni) — açıkça yazılır; `ApprovedAmountMinor` ile aynı değer
  olsa da okuyanın hangi sayının ne olduğunu tahmin etmesi gerekmesin.
- Negatif fark (tahsil > istenen) ayrı ve daha ciddi bir olaydır: alan konmaz, WARN.

### 3. Günlük
`Requested != Collected` olduğunda **WARN**: `{CommandId, istenen, tahsil, fark, banka, bkm}`.
Bugün bu satır hiç yok; ıraksama sessiz.

### 4. Banka atfı — tutardan AYRILIR
`:245`'teki `AmountMinor == request.AmountMinor` şartı kaldırılır. Yerine "bu satır bu
denemeye ait mi" sorusu: ödeme satırı sayısı bu denemede arttıysa son satır bu denemedir.
Böylece **tutar uyuşmazlığı BİLDİRİLİR, banka yine de YAZILIR.** Bugünkü davranış tek bir
olguyu (tutar tutmuyor) iki karara birden bindiriyor ve doğru bilgiyi düşürüyor.

### 5. Defter
`ticket_payments`: `amount_minor` **istenen olarak kalır** (platformla aynı sayı,
mutabakat tek eksende yürüsün); yeni sütun `collected_minor` (migration,
`CommandStore.cs:145` şema bloğu). Değişmez: `sum(collected_minor)` = 
`closed_tickets.paid_minor`. Bugün bu değişmez YOK ve 88/2'de 7150 ≠ 6793 olarak kırıldı.

### 6. Kısmi ödeme — **ÖLÇÜLDÜ (FR30 + fiş 88/11)**

**İndirimin tabanı, o BACAĞIN tutarıdır — fişin toplamı değil.** FR30 3. bacağı: istenen 1390 →
tahsil 1321, indirim **69**; 69/1390 = %4,96 (fiş toplamına göre olsaydı %2,9 olurdu). Fiş toplamı
2380 → 2311'e düştü, `TotalReceiptTax` 322 → 312, ve **açıkta bakiye kalmadı** (990 + 1321 = 2311).
Fiş 88/11'de de aynı: istenen 990 → tahsil 941, fiş 2380 → 2331, kalan 1390 tutarlı kaldı.
`DeviceRemainingMinor` cihazın kendi `Total − Paid` farkından geldiği için **kendiliğinden doğru**;
ek bir hesap gerekmiyor.

### 6-eski. (kaldırıldı — yukarıdaki ölçüm bunun yerine geçti)
`DeviceRemainingMinor` (`GmpTerminalTransport.cs:349`, `:333`) cihazın kendi
`TotalAmountMinor − PaidAmountMinor` farkından geliyor (`GmpContracts.cs:311`). Banka
indirimi `TotalReceiptAmount`'ı da düşürdüğü için kalan **kendiliğinden** doğru olmalı —
ama **ölçülmedi**. Gereken deney: iki bacaklı fiş, ilk bacak kart (indirim uygulanır),
ikinci bacak nakit; kalanın indirimli toplamdan mı yoksa orijinalden mi hesaplandığına
bakılır. FR30 bunu doğal olarak üretir.

### 6b. ADAY MADDE — CLOSED'da `deviceRemainingMinor: 0` (platform isteği)

Platform tarafı ölçtü (`bodyRead.js:99-106`, validator'da ikinci kapı): iki alan **çift olarak**
kabul ediliyor — `deviceRemainingMinor` yoksa `deviceTicketTotalMinor` da düşüyor. Kapanan
fişlerde `deviceRemainingMinor`'ı tasarım gereği göndermediğimiz için (§6) **gönderdiğimiz
6793/941 platforma hiç ulaşmadı**; P65 kıyası kapanışta hiç koşmadı. P138'in kör noktasıyla aynı
yer.

Seçenek (b) = ajan CLOSED'da `deviceRemainingMinor: 0` gönderir. Ucuz ve platformda değişiklik
gerektirmiyor. **Kabul edilebilir, ama tek şartla:** "0" ile "bilinmiyor" ayrımı korunmalı.
Kapanan fişte kalan gerçekten **bilinen bir sıfırdır** — belirsizlik değil. Ancak kapanış
`GetTicket`'i başarısız olduysa (`GmpTerminalTransport.cs:257-259` üç şartı) elimizde toplam da
yoktur; o hâlde **hiçbir alan konmamalı**, bugünkü gibi. Kural:

> `TicketState == "CLOSED"` **ve** `DeviceTicketTotalMinor`/`DevicePaidMinor` gerçekten okunduysa
> → `deviceRemainingMinor = 0` konur. Okuma başarısızsa **üçü de konmaz.**

Böylece sıfır yalnız ölçülmüş bir sıfırdır; uydurulmuş bir sıfır hiçbir zaman gitmez. Test:
kapanış okuması başarısızken `deviceRemainingMinor` **yok** (mutasyon: koşulsuz 0 yaz → KIRMIZI).

⚠️ Ad birleştirme YAPILMAYACAK (platform kararı): kapanış gövdesindeki `paidMinor` tahsil
sayacıdır, ödeme gövdesindeki `deviceRemainingMinor` kalandır; `ticketClose.js:308` kıyası bu
ayrıma dayanıyor.

### 7. Sebep etiketi de yanlış — W79'a dahil
`GmpTerminalTransport.Okuma.cs:231-246`: `acikRet = KullanilabilirKod(AppErrorCode)` →
kod varsa `Refusal` + **`BANK_DECLINED`**. Ölçüldü (FR02 deneme 1): `AppErrorCode E003`
"KULLANICI VAZGECTI" ile **banka hiç sorulmamıştı** — `stan 0`, `rrn ""`,
`authorizeCode ""`, `inputType 0`, `pan ""`. Yani kullanılabilir kod ≠ banka reddi.
**Üçüncü hâl** gerekiyor: *"bankaya gitmedi"*.

⚠️ **İLK ÖNERDİĞİM İMZA YANLIŞTI — ikinci ölçüm çürüttü.** `stan == 0 && rrn boş &&
inputType == 0` demiştim. FR30 2. bacağında (17:13, aynı E003) **`stan = 722`** geldi, yani
sıfır DEĞİL — oysa kart yine okutulmamıştı (`inputType 0`, `pan ""`) ve yetkilendirme yoktu
(`rrn ""`, `authorizeCode ""`, `payAmount 0`). **`stan` yerel olarak deneme başına
veriliyor; bankaya ulaşıldığının kanıtı DEĞİL.** İmzada kullanılamaz.

**Ayakta kalan kanıt:** `authorizeCode == "" && rrn == "" && payAmount == 0` → yetkilendirme
olmadı. Kart hiç okunmadıysa ayrıca `inputType == 0 && pan == ""`. İki ölçümde de dördü birden
tuttu.

⚠️ **METİN EŞLEŞTİRMESİ YASAK.** Aynı olay iki bankada iki farklı metin verdi:
AKBANK → `ErrorMsg "KULLANICI VAZGECTI"`, `AppErrorMsg "İŞLEM ONAYLANMADI"`;
GARANTİ BBVA → `ErrorMsg "KULLANICI VAZGEÇTİ"` (Ç/İ ile), `AppErrorMsg "(0000E003)-"`.
Kararı **yalnız `AppErrorCode` + yukarıdaki alan kanıtı** kurmalı; metin teşhiste gösterilir,
karar verdirmez. Para güvenliği bugünkü hâlde de doğru (`Refusal` = tekrar güvenli),
yanlış olan kasiyere söylenen sebep: "banka reddetti" başka kart denetir; doğrusu "müşteri
vazgeçti, aynı kartla tekrar dene". Yeni sebep adı platform sözleşmesini ilgilendirir →
karşı tarafla kararlaştırılmalı.

### 8. Kasa/portal sözleşmesi — önerilen adlar
`requestedAmountMinor`, `collectedAmountMinor`, `bankDiscountMinor`; nexo tarafında
`AuthorizedAmount` = **tahsil edilen** kalır (sözleşme değişmez, §30.5).

### 9. AÇIK MADDE — P130 kapısı: `voidedPaymentCount 0` gönderirken kimlik ZORUNLU

Platform ölçtü (`reversal.js:446`): `voidedPaymentCount 0` gelen bir iptalde gövde
**`ticketId`** ya da **`cancelledSaleSessionId`** ya da **`TICKET_CANCELLED`** beyanından en az
birini taşımak zorunda; yoksa **400**. W83 ile sayım `payAmount > 0`'a daraltılırsa bugün
`3` giden alanlar `0`'a düşecek — yani bu kapı **W83'ten SONRA çok daha sık** çalınacak.

Bugünkü gövdenin gerçek durumu (`SaleToPoiResponseBuilder.cs:300-323`):
- `cancelledSaleSessionId` **anahtarı her zaman var**, ama bağ yoksa değeri açıkça `null`.
- `ticketId` yalnız `CancelledTicketId` doluysa konur.
- `info` = `TICKET_CANCELLED` (fiş açıktı) **veya `TICKET_NOT_OPEN`** (fiş yoktu).
- `reason` yalnız doluysa konur.

⚠️ **Riskli tek dal: "boşa Fiş İptal" (cihazda fiş yokken).** O yolda
(`GmpTerminalTransport.Iptal.cs:133-136`) sonuç `Approved, TicketWasOpen: false` olur ve gövde
**üç kimliğin HİÇBİRİNİ** taşımaz: `voidedPaymentCount 0`, `cancelledSaleSessionId null`,
`ticketId` yok, `info` = **`TICKET_NOT_OPEN`**. Yani P130 kapısına takılır → **400**.
W68'den sonra 400 kaydı SİLMİYOR, **damgalıyor** — dolayısıyla ortaya *hiçbir zaman
çözülemeyecek* kalıcı bir "defter boşluğu" kaydı çıkar; oysa kaydedilecek bir olay yoktur.

**Karar gereken (platform tarafı):** ya (a) platform `TICKET_NOT_OPEN` beyanını da geçerli
sayar, ya da (b) ajan bu dalda bildirimi **hiç göndermez** (iptal edilecek bir şey yoktu →
deftere yazılacak bir şey de yok). (b) daha basit ama "kasiyer düğmeye bastı" olgusunu
platformdan gizler. Ölçmeden karar verilmemeli: **bu dal bugüne dek sahada hiç koşmadı.**

### 10. W83 — `voidedPaymentCount` DARALTILMAZ, BÖLÜNÜR

**Ölçüm (VO05, fiş 88/10):** üç başarısız kart denemesi olan fişin iptalinde gövde
`voidedPaymentCount 3` + `voidedAmountMinor 0` gönderdi — *"üç ödeme geri alındı, sıfır lira"*.
Kasa "3 ödeme geri alındı" gördü; oysa hiç para yoktu.
**Cihaz ikisini zaten ayrı tutuyor:** `totalNumberOfPayments 3` ve `TotalReceiptPayment 0`.
- `voidedPaymentCount` → **`payAmount > 0` olan satırlar** (o vakada **0**) — `voidedAmountMinor`
  ile aynı tabana oturur, çift artık tutarlı olur.
- `voidedAttemptCount` (**yeni**) → tüm satırlar (o vakada **3**) — başarısız denemeler görünür
  kalır; `NO_RESPONSE` incelemesinde ihtiyaç duyulan sayı budur.
⚠️ Platform `voidedPaymentCount`u bugün "deneme sayısı" diye okuyorsa **anlamı sessizce
çevirmeyin**; o hâlde yalnız yeni alan eklenir. (Platform ölçümü: `expectedVoidCount` zaten
APPROVED sayısı → hizalama doğru yönde.)

### 11. W88 — BAŞARISIZLIK DALINDA TUTAR **NİYET** TAŞIYOR

**Ölçüm (fiş 88/11, 18:11):** `VoidPayment` zaman aşımına uğradı; gövde aynı anda
`Result "Failure"` + `reason VOID_INCOMPLETE` **ve** `voidedPaymentCount 1` +
`voidedAmountMinor 941` dedi. Platform **400 `invalidResult`** ile reddetti — gövde kendi içinde
çelişkili olduğu için makul. Kaynak: `GmpTerminalTransport.Iptal.cs:173` başarısızlık dalında
`VoidedPaymentCount: sayi, VoidedAmountMinor: tutar` yine dolduruluyor.
**Kural:** başarısızlıkta **`voidedPaymentCount = 0`, `voidedAmountMinor = 0`**; niyet ayrı alanda:
**`attemptedVoidAmountMinor`** / **`attemptedVoidCount`**. "Ne kadar geri alındı" ile "ne kadarını
geri almaya çalıştık" iki ayrı sorudur.
**Yan etki (istenen):** gövde çelişkisi kalkınca P130 kapısı da geçilebilir hâle gelir.

### 12. W86 — SATIR YOKKEN `reason` VARSAYILANI KESİN KONUŞUYOR

**Ölçüm (18:00, zaman aşımı 5112):** cihazda **hiç ödeme satırı oluşmadı**
(`totalNumberOfPayments 0`). Yoklama sebepsiz `NotLanded` döndürdü ve
`AgentOrchestrator.cs:256` varsayılanı devreye girdi: `Reason: sonuc.Reason ?? NotLanded` →
gövde **`reason NOT_LANDED`** ("hiç başlamadı") dedi, `ErrorCondition` **`PaymentRestriction`**
oldu, platform **REJECTED** yazdı ve **banka incelemesi açılmadı**.
Oysa elimizdeki kanıt yalnız **mali taraf** (fişte para yok, sayaç kıpırdamadı); zaman aşımının
**banka tarafında** ne olduğu bilinmiyor — ve zaman aşımı yetim provizyonun klasik senaryosudur.
**Kural:** satır oluşmadığında da **`info: NOT_LANDED` gönderilsin** (kanıt sağlam: `delta = 0`
**ve** `PaymentCount` değişmedi) ama **`reason` UYDURULMASIN**. `??` varsayılanı, kanıtın
yokluğunu olumlu bir iddiaya çevirmemeli.
**Aynı aile:** `NO_RESPONSE` dalında cihazın ödeme satırındaki alanlar (`inputType`, `pan`,
`rrn`, `authorizeCode`) **kanıt sayılmaz** — onları dolduracak cevap zaten gelmemiştir; cihaz
o satırı `(00000000)-DEFAULT` ile doldurduğunu kendisi yazıyor. Ayrım ancak **bağımsız bir
kanaldan** kurulabilir (bkz. W85/W99/W100, ya da insan tanıklığı).

### 13. UYGULAMA SIRASI VE YENİ DEĞİŞMEZ

Sıra: **ajan → platform → kasa → panel.** Ajan alanları göndermeden platform okuyamaz; platform
kaydetmeden kasa gösteremez.
**Yeni değişmez (ajan tarafında test edilebilir):**
`sum(ticket_payments.collected_minor) == closed_tickets.paid_minor`
Bugün bu değişmez YOK ve dört kez kırıldı (88/2: 7150 ↔ 6793 · 88/4: 990 ↔ 941 · FR30: 3380 ↔ 2311
· 88/11: 3380 ↔ 2331).

## Testler (mutasyonla kanıtlanacak)
1. `Collected != Requested` → `ApprovedAmountMinor` **istenen** kalır (platform
   eşleştirmesi kırılmasın) VE `CollectedAmountMinor` tahsil edileni taşır.
   Mutasyon: `CollectedAmountMinor`'ı `request.AmountMinor`'dan doldur → KIRMIZI.
2. `MerchantDiscountMinor` = fark, yalnız > 0'da konur.
3. Tutar uyuşmazlığında **banka yine yazılır** (bugün NULL geliyor).
   Mutasyon: `:245`'teki eski şartı geri koy → KIRMIZI.
4. `Collected != delta` → hiçbir tutar iddia edilmez.
5. `ticket_payments` toplamı = `closed_tickets.paid_minor`.
6. `authorizeCode "" && rrn "" && payAmount 0` → sebep `BANK_DECLINED` **değil**
   (⚠️ `stan` KULLANILMAZ — çürütüldü, bkz. §7).
7. Başarısız iptalde `voidedAmountMinor == 0` ve `attemptedVoidAmountMinor == 941`.
   Mutasyon: eski hâli geri koy (niyeti `voidedAmountMinor`e yaz) → KIRMIZI.
8. `voidedPaymentCount` yalnız `payAmount > 0` satırları sayar; `voidedAttemptCount` hepsini.
9. Ödeme satırı hiç oluşmadığında `info == NOT_LANDED` ve `reason` **konmaz**.
10. Kapanış okuması başarısızken `deviceRemainingMinor` **yok** (uydurulmuş sıfır gitmez).

## Kapsam dışı
`bekleMs` negatif (−2, −10, −20, −69) → **W80**, ayrı ve küçük.
