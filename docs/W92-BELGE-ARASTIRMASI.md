# W92 — Kısmi kart ödemesi sonrası fiş iptali: ÜRETİCİ BELGELERİ NE DİYOR

Kullanıcı talebi (2026-09-09): *parçalı ödemede kredi kartı alındıktan sonra fiş iptalini
dökümanlardan araştır.* Kod ve cihaz kullanılmadı; yalnız `docs/ingenico/` altındaki üretici
belgeleri. **Bu klasör bugüne dek analiz edilmemişti** (kendi README'si öyle diyor).

## 1. Kısmi KART ödemesi alınmış fişin doğru iptal dizisi

**GMP3-Workshop.pdf 7.1.7 (APP_ERR_PAYMENT_FOUND = 2069), aynen:**
> This error means that there is a successful payment in that ticket, which stops you calling the
> function you tried to call. ... You'll get this error because before calling FP3_VoidAll method,
> you have make sure that there is no successful payment. **You can check current situation with
> FP3_GetTicket and Void successful payments with FP3_VoidPayment then you can call FP3_VoidAll**

→ Belgelenmiş sıra: **GetTicket → VoidPayment (her başarılı ödeme) → VoidAll.** Bizim kod
`VoidAll`'ı ÖNCE çağırıp 2069'u keşif aracı olarak kullanıyor, sonra aynı diziye giriyor.

**4.2.3 (FP3_VoidPayment):** *this can't be used after total amount printed out with
FP3_PrintTotalsAndPayments* · *If the payment was in credit card, **Ingenico device will take of
any other details**.* → Kart bacağının banka tarafını **cihaz kendi hallediyor**; ödeme iptali
`PrintTotalsAndPayments` sonrası imkânsız (2357 dalımız uyumlu).

**4.2.5 — KULLANMADIĞIMIZ FONKSİYON `FP3_ReversePayment`:** *reverse payment (negative payments)
(REFUND, VOID, ext) operation*; `REVERSE_PAYMENT_BANK_CARD_VOID = 0x00200000`,
`REVERSE_PAYMENT_BANK_CARD_REFUND = 0x00400000`. Sabitler `GmpInterop.cs:887-889`'da **VAR**,
fonksiyon `IGmpWrapper`'a **bağlı DEĞİL**. Tercih ölçütü **BELGEDE YOK**.

## 2. YARIM KALMIŞ İŞLEMDEN KURTARMA — üreticinin 9 adımlı akışı

`GMP3OlumsuzDurumYönetimi_v1.2.pdf` 2. bölüm:

1. DLL hata döndürür.
2. **ReloadTransaction** = `FP3_OptionFlags` + `FP3_GetTicket`; `TransactionFlags` okunur.
3. `MF_PRINTED` varsa → yalnız `Close`.
4. `BEFORE_MF_PRINTED` varsa → `PrintMF` + `Close`.
5. `TOTALS_AND_PAYMENTS_PRINTED` varsa → `PrintMF` + `Close`.
6. `TotalReceiptPayment == TotalReceiptAmount` ise → `PrintMF` + `Close`.
7. **Parçalı ödeme var mı? EVET: Kalan ödemeler tamamlanır. PrintMF yazdırılır ve Close ile fiş
   kapatılabilir.**
8. Ürünler/toplamlar doğruysa → kalan ödemeler tamamlanıp kapatılır.
9. `TICKET_HEADER_PRINTED` varsa → VoidAll ile kapatılabilir veya ItemSale'e devam.

→ **Üreticinin bu duruma önerdiği çıkış iptali TEKRAR DENEMEK DEĞİL; kalan ödemeyi tamamlayıp
fişi kapatmaktır (adım 7).** `VoidAll` yalnız ödeme YOKKEN (adım 9) öneriliyor.
→ Adım 2 bizim yoklamamızın yaptığı şeyin ta kendisi.
→ ⚠️ **`TransactionFlags` bizde OKUNUYOR ama ÇÖZÜLMÜYOR** (günlükte ham `131074`). Adım 3-5 ve 9
bu bayrakları gerektiriyor; o dalları bugün uygulayamıyoruz.

## 3. Zaman aşımı

- `DLL_RETCODE_TIMEOUT` (0xF003): *bir fonksiyonun tanımlanan timeout değerinden fazla sürmesi*.
- **`VoidPayment`/`VoidAll` için önerilen süre BELGEDE YOK.**
- Olumsuz Durum 3. bölüm örneğinde: *FP3_Payment işleminin Timeout süresi **90000ms (90sn)***
  → 90 sn üreticinin kendi örnek değeri (bizim `TimeoutCard`).
- **Banka ters işleminin beklenen süresi BELGEDE YOK.**

## 4. IsTcpKeepAlive — BİZDE FALSE, üretici tam bu durum için ekletmiş

> Bu özelliğin eklenme amacı; **ödeme işlemi başlatıldıktan sonra herhangi bir anda yaşanabilecek
> bir bağlantı kesintisini timeout süresince bekletip hatanın görüntülenmesi yerine kısa
> aralıklarda bağlantıyı sürekli kontrol ederek erken tespit etmek**. Yalnız **ETHERNET**.
> ...bağlantı kesintisi **6sn içerisinde** tespit edilmiş olup hata döndürülmüştür.
> (1. bölüm) TRUE ise bağlantı *aşama farketmeksizin **3 5 sn** aralıklarla test edilmektedir*.

GMP.XML'imizde **`IsTcpKeepAlive:FALSE`**, bağlantımız **Ethernet** (192.168.1.189:7500).
→ Ölçtüğümüz **66 saniyelik blokaj** bu ayarla **3-6 saniyeye** inebilir; W67'nin gerekçesinin
bir kısmını doğrudan karşılar.
→ **[ÇIKARIM]** Bugünkü `VoidPayment` 0xF003'ü **bizim 10 sn'lik timeout'umuzdur** (tam
10 114 ms, keep-alive kapalı) — keep-alive kaynaklı değil.

## 5. Yarım iptalden kurtarma / tekillik

- **`FP3_VoidPayment`in aynı ödeme için TEKRAR çağrılmasının ne yapacağı BELGEDE YOK.**
- **`FP3_FunctionPaymentCheck` / `FP3_FunctionTransactionInquiry` / `FP3_GetCurrentFiscalCounters`
  bu belgelerde HİÇ TARİF EDİLMİYOR** — yalnız başlıkta adları var. Üreticinin kurtarma akışı
  bunları kullanmıyor, `ReloadTransaction` diyor.
- Olumsuz Durum 1. bölüm: *Cihazda ödeme mevcut iken fiş iptal edilemez.*

## 6. Test senaryoları — AYNEN (Worldline_GMP3_test_cases_v2.0.xlsx)

**VO03 · Parçalı ödemede Nakit Ödemenin İptali / Void Payment Cash** — parçalı nakit → nakit
iptal → tüm tutar kredili. Beklenen: *Kredili ödemenin SATIŞ fişleri bastırılmalıdır.*

**VO04 · Parçalı ödemede Kredili Ödemenin İptali / Void Payment Credit** — parçalı kredili →
**kredili ödeme iptal** → **tüm tutar Nakit ödenerek fiş bitirilir**. Beklenen: *Kredili ödemenin
**SATIŞ - İPTAL fişleri** bastırılmalıdır* (EN: *Credit card **void slip** must be printed*).

**VO05 · Ödeme öncesinde tüm fişin İptali** — *ÖKC üzerinde tüm fiş iptal edilmiş olmalıdır.*

**VO06 · Parçalı ödeme sonrasında Kredili işlemin ve Tüm fişin İptali / Void All Ticket (After
credit card payment)** — parçalı kredili → **kredili ödeme iptal → tüm fiş iptal**. Beklenen:
*Kredi kartlı ödeme başarılı olarak iptal edilebilmelidir. (**İptal fişleri basılmalı**) ÖKC tüm
fiş iptal edilir.(automatically by Ext.System)*

→ VO04 ile VO06 **iki AYRI senaryo**: VO04 = ödeme iptali + **kalanı nakitle kapat**;
VO06 = ödeme iptali + **fişi de iptal et**. İkisinde de **iptal fişi basılması** bekleniyor.

## 7. Bugünkü olayla ilişki

**[ÇIKARIM]** Fiş 88/11 için üreticinin belgelenmiş çıkışı **2. bölüm adım 7**: kalan ödemeyi
tamamla, `PrintMF`, `Close`. Yani bizim **(b)** seçeneğimiz — ve onu engelleyen şey cihaz değil,
**kendi `DevamDogrula` kontrolümüz** (`GmpTerminalTransport.Fis.cs:44`; 2331 != 2380).
Belge bu durumda iptali tekrar denemeyi **önermiyor**.
**[BELGEDE YOK]** Ters işlemin bankaya ulaşıp ulaşmadığını cihazdan sorgulamanın bir yolu.

## Taranan, bu konuda ek bilgi çıkmayan belgeler
`GMP3_ErrorHandling_EN_v2.docx`, `GMP3_GeneralDataFlow_EN_v1.docx`, `GMP3_KDVCalculation_TR_v1.docx`,
`GMP3_MaliFisteYemekCekiKullanimi_TR.docx`, `Dinamik KDV_TR.pdf`, `Fiş Limiti Aşılmaz 1.pdf`,
`GMP3 Serbest giriş Kapama(Açma.pdf`, `GMP3+Entegrasyon+Projesi+Süreçleri+20250728.pdf`.