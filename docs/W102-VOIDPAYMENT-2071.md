# W102 — `FP3_VoidPayment` → `2071 APP_ERR_NOT_APPROVED` (ARAŞTIRMA, KOD YOK)

**Kök sebep kullanıcıdan geldi (cihaz başı gözlem, 2026-09-09):** *"Kredi kartı iptal işleminde
tekrar kart bilgilerini istiyor ve kart şifresi istiyor, kart şifresi elimde yok, iptal işlemini
geçelim."* → Cihaz **kart + PIN** bekledi, verilmedi, **30 023 ms** sonra `2071` döndü.

Ölçüm: fiş **88/12**, 22:30. `VoidAll(TO 90000)` → **2069** (316 ms) → `VoidPayment(TO 90000)` →
**2071 `APP_ERR_NOT_APPROVED`** (30 023 ms). Cihaza dokunulmadı.

## (a) Kart + PIN şartı belgede yazıyor mu — **HAYIR. Ama KARTSIZ bir yol VAR (iade olarak).**

**Şart hiçbir yerde yazmıyor:**
- **`2071` için Workshop'ta açıklayıcı bölüm YOK.** §7.1 altında 2009, 2021, 2064, **2069**, 2080,
  2085, 2086, 2088, 2119 düz yazıyla anlatılmış; **2071 yalnız hata kodu tablosunda** bir satır
  (`GMP3-Workshop` s.~53). Anlamı tarif edilmemiş.
- `FP3_VoidPayment` hakkında belgenin söylediği her şey (§4.2.3): *"used for voiding previous
  successful payments… can't be used after total amount printed out with
  FP3_PrintTotalsAndPayments"* + *"If the payment was in credit card, **Ingenico device will take
  of any other details**."* → Kart/PIN isteyip istemediği **yazmıyor**; "cihaz halleder" deniyor.
- Test senaryoları (VO04/VO06) yalnız **beklenen sonucu** yazıyor (*"başarılı olarak iptal
  edilebilmelidir, iptal fişleri basılmalı"*), **ön koşul saymıyor**.
- `GmpSampleSim.exe` dizelerinde iptale özel ekran/onay alanı yok.
→ **Kullanıcının gözlemi bu boşluğu dolduran tek kanal** ve belgeyle çelişmiyor; belge sessiz.

### 🔑 KARTSIZ TERS İŞLEM — belgede VAR, bizde BAĞLI DEĞİL

`GMP3-Workshop` §5.13.1.2, iade tipleri, **aynen**:
> "4- **Kartsiz Referansli Iade - Garanti Bankasi (Refund using reference, credit card no
> required)**" · "In this refund type, **credit card is not required** and transaction parameters
> are required by banking application which provided via external system."

Bizim interop'ta karşılığı **var ama kullanılmıyor**:
`PAYMENT_SUBTYPE_REFERENCED_REFUND_WITHOUT_CARD = 0x00000008` (`GmpInterop.cs:913`), ayrıca
`REFERENCED_REFUND = 6`, `..._WITH_CARD = 7`. Çağrı ise `FP3_ReversePayment` (§4.2.5,
`REVERSE_PAYMENT_BANK_CARD_VOID = 0x00200000`, `..._REFUND = 0x00400000`) — **`IGmpWrapper`'a
bağlı değil** (W94).
**Ve bankamız GARANTİ** — belgede kartsız yol tam olarak Garanti için tarif edilmiş.

⚠️ **AMA BU BİR "İPTAL" DEĞİL, "İADE"DİR** ve fark önemlidir:
- İptal (void) aynı gün, orijinal işlemi ortadan kaldırır; iade (refund) **yeni ve ters yönlü bir
  işlemdir** — mali/muhasebe karşılığı farklıdır.
- Belge iadeyi **kendi işlem tipleri** altında anlatıyor; **açık bir fişin ortasında** kullanılıp
  kullanılamayacağı **yazmıyor**.
- "transaction parameters … provided via external system" diyor: orijinal işlemin referansları
  (rrn/stan/batch/authCode) bizden istenecek. **Bunlar elimizde var** (ödeme satırından).
- **Hiç ölçülmedi, hiç çalıştırılmadı.**

## (b) 30 saniye nereden — **BİZDEN DEĞİL; ötesi BELGEDE YOK**

- Çağrının zaman aşımı **90 000 ms**'ti (izde `VoidPayment(TO : 90000)`) → **30 sn bizim sınırımız
  değil**; cihaz kendi cevabını verdi. (W87 olmasaydı 10 sn'de kesilir, bu cevabı hiç görmezdik.)
- Canlı `GMP.XML`: `AckTimeOut 3000`, `CommTimeOut 90000` (**yalnız eşleştirmede**),
  `InterCharacterTimeOut 100`, `RetryCounter 3`, `IpRetryCount 3`, `IsTcpKeepAlive FALSE`.
  **Hiçbiri kart/PIN bekleme süresini yönetmiyor.**
- Belgelerde "kart bekleme süresi" diye bir parametre **tarif edilmiyor** (12 belge tarandı).
→ **[ÇIKARIM, kullanıcı gözlemiyle uyumlu]** 30 sn, cihazın kart/PIN ekranında beklediği süre;
kullanıcı vazgeçince/süre dolunca `2071` döndü. Kıyas: aynı turda başarılı kart ödemesi 39,8 sn,
cevapsız deneme 44,4 sn — hepsi insan bandında.

## (c) 2071 ile E003 aynı aile mi — **HAYIR** · Bankaya gidildiğini gösteren alan — **YOK**

- **`2071` GMP/ÖKC dönüş kodudur** (`APP_ERR_*`, `FP3_*` çağrısının dönüşü).
  **`E003` banka uygulamasının kendi kodudur** (`stPaymentErrMessage.AppErrorCode`, dize).
  Türkçe ifadeleri benzese de **ad alanları ayrı**; "aynı aile" demek yanlış olur.
- `VoidPayment` hata dönünce `ST_TICKET` **tamamen sıfır** geldi (22:30:59 yanıtı, ham):

      TransactionFlags = 0
      TotalReceiptAmount = 0 · TotalReceiptTax = 0 · TotalReceiptDiscount = 0
      TotalReceiptIncrease = 0 · TotalReceiptItem = 0 · TotalReceiptPayment = 0
      TotalReceiptReversal = 0 · numberOfPayments = 0

  **`rrn`, `stan`, `authorizeCode` yok; ödeme satırı hiç yok.** Cevapta ters işlemin bankaya
  ulaşıp ulaşmadığını gösteren **tek bir alan bile yok**.
⚠️ Boş alanlar **kanıt değildir** — "bankaya gitmedi" diye değil, "cevapta bilgi yok" diye okunur.
(Kullanıcının gözlemi burada bağımsız kanal: kart hiç okutulmadıysa bankaya da gitmemiştir.)

## (d) Onaylanmayan iptalden sonra doğru sıra — **BELGE: TEKRAR DENEME, TAMAMLA VE KAPAT**

`GMP3OlumsuzDurumYönetimi_v1.2` §2, 9 adımlı akış. Bizim hâlimiz **adım 7**:
> **"Parçalı Ödeme Var mı? EVET: Kalan ödemeler tamamlanır. PrintMF metodu yazdırılır ve Close
> metodu ile fiş kapatılabilir."**

`VoidAll` yalnız **adım 9**'da (ödeme YOKKEN). **"VoidPayment'ı tekrar dene" diye bir adım YOK.**
**Sahada da öyle oldu:** 22:33'te kalan 1390 nakit ödendi, fiş kapandı (2331/2331) ve defter
değişmezi (`Σ collected_minor == paid_minor`) **ilk kez tuttu**.
⚠️ Tekrar denemenin riski duruyor: `VoidPayment`in aynı ödeme için ikinci çağrısı **belgede
tanımsız**; ilk deneme bankada bir şey bıraktıysa **çift iade** üretebilir.

## Banka mutabakatı için üçlü (kullanıcı kart uygulamasından sorsun)

| alan | değer |
|---|---|
| banka | **GARANTİ BBVA** (`bankBkmId 62`) |
| tutar | **941 kuruş = 9,41 TL** |
| `stan` | **735** · `batchNo` **136** |
| saat | **22:29** (cihaz fiş saati `szTicketTime 222637`; ödeme onayı 22:29:09) |
| beklenen | tahsilat **DURUYOR** (iade yok) — çünkü iptal `2071` ile onaylanmadı |

## Yan bulgu — FAILED gövdesindeki `reason`

Bildirim **200 `Recorded`** aldığı için outbox kaydı onaylanıp **silindi**; literal gövde
gösterilemiyor. Koddan kesin türetilir (`GmpTerminalTransport.Iptal.cs`, `REVERSAL_FAILED` dalı):
`reason` = **`VOID_INCOMPLETE`** · `providerResultCode` = **`REVERSAL_FAILED:0x0817`** ·
`voidedPaymentCount` **0** · `voidedAmountMinor` **0** · `attemptedVoidAmountMinor` **941** ·
`attemptedVoidCount` **1** · `cancelledSaleSessionId` `a0a15fdb-…` ·
`ticketId` `tkt_c1740fb09df940c4906659ca215aaa07`.

## Öneri (karar kullanıcıda/orkestrasyonda)

Kartsız iptal ihtiyacı gerçekse tek belgelenmiş yol **`FP3_ReversePayment` + referanslı KARTSIZ
İADE (Garanti)**. Ama bu **iade**dir, iptal değil; **açık fişte kullanımı belgede yok**; çağrı
bizde **bağlı değil**; ve **hiç ölçülmedi**. Yani küçük bir iş değil: W94 (bağlama) + yeni bir
akış + sertifikasyon/muhasebe sorusu. **Bugünkü pratik çıkış, üreticinin dediği gibi kalanı
ödeyip fişi kapatmaktır** — bugün iki kez çalıştı.
