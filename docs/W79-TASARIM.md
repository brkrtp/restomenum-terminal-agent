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
- `ApprovedAmountMinor` → **tahsil edilen** (anlamı değişiyor, adı değil: nexo
  `AuthorizedAmount` "para hareket etti" demek, `SaleToPoiResponseBuilder.cs:43`).
- `RequestedAmountMinor` (yeni) — kasanın istediği.
- `BankDiscountMinor` (yeni) — `Requested − Collected`, yalnız **> 0** ise konur.
  Negatif çıkarsa (tahsil > istenen) bu ayrı ve daha ciddi bir olaydır: alan konmaz,
  `Unknown` + WARN.

### 3. Günlük
`Requested != Collected` olduğunda **WARN**: `{CommandId, istenen, tahsil, fark, banka, bkm}`.
Bugün bu satır hiç yok; ıraksama sessiz.

### 4. Banka atfı — tutardan AYRILIR
`:245`'teki `AmountMinor == request.AmountMinor` şartı kaldırılır. Yerine "bu satır bu
denemeye ait mi" sorusu: ödeme satırı sayısı bu denemede arttıysa son satır bu denemedir.
Böylece **tutar uyuşmazlığı BİLDİRİLİR, banka yine de YAZILIR.** Bugünkü davranış tek bir
olguyu (tutar tutmuyor) iki karara birden bindiriyor ve doğru bilgiyi düşürüyor.

### 5. Defter
`ticket_payments`: `amount_minor` = **tahsil edilen**; yeni sütun `requested_minor`
(migration, `CommandStore.cs:145` şema bloğu). Böylece `ticket_payments` toplamı ile
`closed_tickets.paid_minor` **tutar**.

### 6. Kısmi ödeme — ÖLÇÜLMEDİ
`DeviceRemainingMinor` (`GmpTerminalTransport.cs:349`, `:333`) cihazın kendi
`TotalAmountMinor − PaidAmountMinor` farkından geliyor (`GmpContracts.cs:311`). Banka
indirimi `TotalReceiptAmount`'ı da düşürdüğü için kalan **kendiliğinden** doğru olmalı —
ama **ölçülmedi**. Gereken deney: iki bacaklı fiş, ilk bacak kart (indirim uygulanır),
ikinci bacak nakit; kalanın indirimli toplamdan mı yoksa orijinalden mi hesaplandığına
bakılır. FR30 bunu doğal olarak üretir.

### 7. Sebep etiketi de yanlış — W79'a dahil
`GmpTerminalTransport.Okuma.cs:231-246`: `acikRet = KullanilabilirKod(AppErrorCode)` →
kod varsa `Refusal` + **`BANK_DECLINED`**. Ölçüldü (FR02 deneme 1): `AppErrorCode E003`
"KULLANICI VAZGECTI" ile **banka hiç sorulmamıştı** — `stan 0`, `rrn ""`,
`authorizeCode ""`, `inputType 0`, `pan ""`. Yani kullanılabilir kod ≠ banka reddi.
**Üçüncü hâl** gerekiyor: *"bankaya gitmedi"*. Ayırt eden kanıt elde: `stan == 0 && rrn
boş && inputType == 0`. Para güvenliği bugünkü hâlde de doğru (`Refusal` = tekrar güvenli),
yanlış olan kasiyere söylenen sebep: "banka reddetti" başka kart denetir; doğrusu "müşteri
vazgeçti, aynı kartla tekrar dene". Yeni sebep adı platform sözleşmesini ilgilendirir →
karşı tarafla kararlaştırılmalı.

### 8. Kasa/portal sözleşmesi — önerilen adlar
`requestedAmountMinor`, `collectedAmountMinor`, `bankDiscountMinor`; nexo tarafında
`AuthorizedAmount` = **tahsil edilen** kalır (sözleşme değişmez, §30.5).

## Testler (mutasyonla kanıtlanacak)
1. `Collected != Requested` → `ApprovedAmountMinor` **tahsil edileni** taşır.
   Mutasyon: `request.AmountMinor`'a geri döndür → KIRMIZI.
2. `BankDiscountMinor` = fark, yalnız > 0'da konur.
3. Tutar uyuşmazlığında **banka yine yazılır** (bugün NULL geliyor).
   Mutasyon: `:245`'teki eski şartı geri koy → KIRMIZI.
4. `Collected != delta` → hiçbir tutar iddia edilmez.
5. `ticket_payments` toplamı = `closed_tickets.paid_minor`.
6. `stan 0 && rrn boş && inputType 0` → sebep `BANK_DECLINED` **değil**.

## Kapsam dışı
`bekleMs` negatif (−2, −10, −20, −69) → **W80**, ayrı ve küçük.
