# W79 — "tahsil edilen ≠ istenen" bildirilsin (tasarım, KOD YOK)

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

### 6. Kısmi ödeme — ÖLÇÜLMEDİ
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

## Testler (mutasyonla kanıtlanacak)
1. `Collected != Requested` → `ApprovedAmountMinor` **istenen** kalır (platform
   eşleştirmesi kırılmasın) VE `CollectedAmountMinor` tahsil edileni taşır.
   Mutasyon: `CollectedAmountMinor`'ı `request.AmountMinor`'dan doldur → KIRMIZI.
2. `MerchantDiscountMinor` = fark, yalnız > 0'da konur.
3. Tutar uyuşmazlığında **banka yine yazılır** (bugün NULL geliyor).
   Mutasyon: `:245`'teki eski şartı geri koy → KIRMIZI.
4. `Collected != delta` → hiçbir tutar iddia edilmez.
5. `ticket_payments` toplamı = `closed_tickets.paid_minor`.
6. `stan 0 && rrn boş && inputType 0` → sebep `BANK_DECLINED` **değil**.

## Kapsam dışı
`bekleMs` negatif (−2, −10, −20, −69) → **W80**, ayrı ve küçük.
