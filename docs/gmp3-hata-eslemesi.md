# GMP-3 hata kodu → nexo `ErrorCondition` eşlemesi

**Kapsam:** agent'ın (`Restomenum.Agent.Core`) üretebildiği tüm sonuçlar.
**Tek uygulama yeri:** `windows/src/Restomenum.Agent.Core/GmpErrorMap.cs`.
**Kaynak belge:** `docs/ingenico/GMP3_ErrorHandling_EN_v2.docx` — "Error Codes and Possible Actions"
bölümü, kod başlıkları altındaki maddeler. Belge numaralı madde kullanmıyor; dayanak sütununda ilgili
kodun kendi başlığı gösteriliyor ve alıntı birebir veriliyor.

---

## 0. Değişmez

> **"Bilmiyorum" asla "kesin hayır"a çevrilmez.**

`Refusal` (nexo 08) yalnız **banka host'u cevap verip açıkça reddettiğinde** kullanılabilir.
Host'a ulaşılamadığında `UnreachableHost` (nexo 07); kurtarma yoklaması belirsiz kaldıysa
`InProgress`.

**Bugün agent hiçbir yerde `Refusal` üretmiyor** — çünkü issuer'ın açık ret yanıtını okuyacak kanal
bağlı değil (bkz. §4). Bu, `GmpErrorMapTests.HicbirKod_HicbirAdimda_Refusal_URETMEZ` ile çivilendi.

---

## 1. Adım kuralı — para riskini kod değil, ADIM belirler

| Adım | Ne demek | Para hareket etmiş olabilir mi? | Dayanağı |
|---|---|---|---|
| `BeforePayment` | `FP3_Payment` **henüz çağrılmadı** (Start / TicketHeader / OptionFlags / ItemSale) | **Hayır** | GMP-3 belgesi değil — **kendi çağrı sıramız**: ödeme fonksiyonu hiç çağrılmadı |
| `Payment` | `FP3_Payment` çağrıldı | **Bilinmiyor** (her kod için) | 2022 maddesi: *"When an external application call FP3_Payment function, it can be successful on Ingenico device, even though you don't get result properly."* |
| `AfterPayment` | Ödeme sonrası (baskı / kapatma / iptal) | **Evet** | Ödeme başarılı döndü |

> `Payment` adımında **"para hareket etmedi" diyen tek bir kod yok.** Belge 2085 ve 2086 için yalnız
> "başarısız" diyor; "cihazda ödeme oluşmadı" demiyor. Bu, `OdemeAdiminda_HICBIR_KOD_..._SOYLEMEZ`
> testiyle çivilendi.

---

## 2. Ödeme adımı (`FP3_Payment` çağrıldı)

| Kod | Ad | `ErrorCondition` | Para hareket etmiş olabilir mi? | Dayanağı |
|---|---|---|---|---|
| `2086` | `APP_ERR_PAYMENT_NOT_SUCCESSFUL_AND_MORE_ERROR_CODE` | **`UnreachableHost`** | **Bilinmiyor** | Belge (2086): *"payment was not a success and there's a specific error message from Banking application."* Belge o mesajın **ne zaman** üretildiğini söylemiyor → §3'e bak |
| `2085` | `APP_ERR_PAYMENT_NOT_SUCCESSFUL_AND_NO_MORE_ERROR_CODE` | `InProgress` | **Bilinmiyor** | Belge (2085): *"payment was not a success and there's **no specific error message** from Banking application."* Mesaj yoksa issuer yanıtı da yok → kesin-ret kanıtı yok. Belge "ödeme oluşmadı" demiyor |
| `0xF01C` | `DLL_RETCODE_RECV_BUSY` | `Busy` | **Bilinmiyor** | Belge (0xF01C): cihaz meşgul. **Saha:** RECV_BUSY tam olarak kart ödemesi **başarıyla tamamlandıktan sonra** geldi (GMPDLL_2026_04_17_103039) |
| `0xF003` / `0xF007` | `DLL_RETCODE_TIMEOUT` / `DATA_RECV_ERR` | `InProgress` | **Bilinmiyor** | Belge: *"getting this error does not mean that function call was not successful. If you assume that it is, it'll definetely cause serious problems."* |
| **diğer her kod** | — | `InProgress` | **Bilinmiyor** | Belge: *"If these errors **and other errors you don't cover** are returned, you must check current situation with FP3_GetTicket and act accordingly."* |

---

## 3. 2086 host'a gönderilmeden ÖNCE mi üretiliyor, sonrasını da kapsıyor mu?

**Cevap: BİLİNMİYOR.** Tahmin edilmedi.

- `GMP3_ErrorHandling_EN_v2.docx` 2086 maddesi bunu **söylemiyor**. Yalnız "banka uygulamasından özel
  bir hata mesajı var" diyor ve ayrıntı için `ST_PaymentErrMessage` sınıfına yönlendiriyor.
- **Ölçülen dolaylı kanıt:** banka hattı olmayan terminalde 2086'nın yanında görülen mesajlar
  `"BAĞLANTI HATASI"`, `"NO RESPONSE"`, `"İŞLEM ONAYLANMADI"` idi. `"NO RESPONSE"` tam olarak
  *"istek gitti, cevap gelmedi"* anlamına gelir — yani kod **gönderim sonrası timeout'u da
  kapsayabiliyor.** Bu bir belge maddesi değil, gözlenmiş mesaj dizesinden çıkarım.
- **Sonuç:** ayırt edilemediği için 2086 kesin-ret olamaz. Kapsıyorsa `Refusal` demek çift-çekim
  üretir; kapsamıyorsa `UnreachableHost` demek yalnız fazladan bir çözüm turu maliyeti getirir.
  Asimetri açık.

Kesinleştirmenin yolu: `ST_PaymentErrMessage.ErrorCode` (banka hata kodu) alanını okumak — bkz. §4.

---

## 4. `Refusal`'ın tek meşru doğum yeri (bugün bağlı DEĞİL)

`GmpInterop.ST_BANK_PAYMENT_INFO.stPaymentErrMessage`:

```
public string ErrorCode;     // bank error code
public string ErrorMsg;
public string AppErrorCode;  // payment application specific error code
public string AppErrorMsg;
```

Bu alan **sarmalayıcıda yüzeye çıkarılmadı** (`GmpTicket` taşımıyor) ve **canlıda hiç ölçülmedi** —
test terminalinde banka hattı yok, dolayısıyla gerçek bir issuer yanıt kodu hiç görülmedi.

**Bilerek bağlanmadı:** en tehlikeli verdikti (`Refusal` = kesin ret) üreten bir yolu, doğrulanmamış
bir alan okumasına dayandırmak, düzeltmeye çalıştığımız hatanın aynısını daha sinsi biçimde geri
getirirdi. Banka hattı olan bir terminalde ölçüldüğü gün `Refusal` yalnız burada doğacak.

---

## 5. Ödeme öncesi adımlar (`FP3_Payment` çağrılmadı → para hareket etmedi)

Hepsinde para riski **yok** ama hiçbiri `Refusal` **değil**: banka hiç devrede olmadığı için
"kart reddedildi, başka kart isteyin" mesajı kasiyeri olmayan bir kart sorununa yönlendirirdi.

| Kod | Ad | Sonuç sınıfı | `ErrorCondition` | Para? | Dayanağı |
|---|---|---|---|---|---|
| `2080` | `APP_ERR_ALREADY_DONE` | `Declined` | `PaymentRestriction` + reason `TICKET_ALREADY_OPEN` | **Hayır** | Belge (2080): `FP3_Start` reddedildi → **bu denemede ödeme hiç başlamadı.** Açık fişin içindeki para ÖNCEKİ denemenin sorusudur. Belirsiz demek her yeni denemeyi çözüm döngüsünde asardı (2026-09-06/07'de ölçüldü) |
| `0xF01C` | `RECV_BUSY` | `Busy` | Bilinmiyor | Belge (0xF01C) + saha ölçümü |
| `0xF003`/`0xF007` | timeout / recv err | `Unknown` | `InProgress` | Bilinmiyor | Belge: "does not mean that function call was not successful" |
| `2317` | `APP_ERR_GMP3_INVALID_HANDLE` | `Unknown` | `InProgress` + reason `INVALID_HANDLE` | Bilinmiyor | Belge (2317): *"you can basically call FP3_GetTicket function and act accordingly"* → durum okunmadan sonuç söylenmez. **Tanıtıcı önce yenilenir** (`FP3_Start` 2080'de bile hTrx döndürür), sonra karar verilir |
| `2341` | `APP_ERR_GMP3_NO_HANDLE` | `Unknown` | `InProgress` + reason `INVALID_HANDLE` | Bilinmiyor | Belge (2341) |
| `0xF000` | `DLL_RETCODE_PORT_NOT_OPEN` | `Declined` | `UnavailableDevice` | **Hayır** | Belge: GMP.XML/kablo yapılandırması — komut PC'den çıkmadı |
| `0xF01B` | `DLL_RETCODE_ACK_NOT_RECEIVED` | `Declined` | `UnavailableDevice` | **Hayır** | Aynı madde (0xF000 ile birlikte anlatılıyor) |
| `0xF020` | `DLL_RETCODE_PAIRING_REQUIRED` | `Declined` | `UnavailableService` | **Hayır** | Belge: *"you must trigger pairing … before proceeding any other function"* |
| `2053` | `APP_ERR_CASHIER_ENTRY_REQUIRED` | `Declined` | `NotAllowed` | **Hayır** | Belge (2053): kasiyer girişi yapılmamış |
| `2417` | `APP_ERR_GMP3_Z_REQUIRED` | `Declined` | `NotAllowed` | **Hayır** | Belge (2417): satış için Z raporu gerekli |
| `2064` | `APP_ERR_NOT_ALLOWED` | `Declined` | `NotAllowed` | **Hayır** | Belge (2064): TSM izni yok |
| `2310` | `APP_ERR_GMP3_INVALID_DATE_TIME` | `Declined` | `NotAllowed` | **Hayır** | Belge (2310) |
| `2097` | `..._GMP3_TRANSACTION_IS_PENDING` | `Declined` | `NotAllowed` | **Hayır** | Belge (2097) |
| `2009` | `APP_ERR_FISCAL_INVALID_ENTRY` | `Declined` | `MessageFormat` | **Hayır** | Belge (2009): parametreler hatalı dolduruldu |
| `2067` | `APP_ERR_FIS_LIMITI_ASILAMAZ` | `Declined` | `PaymentRestriction` | **Hayır** | Belge (2067): `FP3_ItemSale`'den döner, ödeme aşamasına gelinmedi |
| **diğer her kod** | — | `Unknown` | `InProgress` | Bilinmiyor | Belge: kapsanmayan her kodda `FP3_GetTicket` ile duruma bak |

---

## 6. Terminale hiç gidilmeyen retler (agent'ın kendi kapıları)

| Kaynak | `AdditionalResponse` | `ErrorCondition` | Para? | Dayanağı |
|---|---|---|---|---|
| `GmpTerminalTransport` — kalem yok | `FISCAL_LINES_REQUIRED` | `PaymentRestriction` | **Hayır** | Terminal çağrısı hiç yapılmadı (kod akışı) |
| `GmpTerminalTransport` — departman eşlenmemiş | `PRODUCT_UNMAPPED:<id>` | `PaymentRestriction` | **Hayır** | Aynı |
| `LocalSaleHandler` — GET reddi (hepsi) | `GET:<reason>` | `PaymentRestriction` + reason `AMOUNT_FETCH_FAILED` | **Hayır** | Tutar alınamadığında `FP3_Payment` çağrılmaz → sonuç **kesin**. Eski `Aborted`/`UnreachableHost` belirsiz sınıfıydı ve denemeyi gereksiz yere çözüm döngüsünde bırakıyordu |
| `LocalSaleHandler` — ürün/yöntem/KDV eşleşmesi | `PRODUCT_UNMAPPED` / `PAYMENT_METHOD_UNMAPPED` / `PROVIDER_CONFIG_INCOMPLETE` | `PaymentRestriction` + aynı adlı reason | **Hayır** | Aynı |
| `SaleToPoiResponseBuilder` — §30.5 ihlali | `AUTHORIZED_AMOUNT_INVALID` | `InProgress` | Bilinmiyor | §31.3 sınır değişmezi — **gevşetilmedi** |

---

## 7. Kurtarma yoklamasından sonra koşul ne olur

| Yoklama sonucu | Karar | `ErrorCondition` |
|---|---|---|
| `Landed` | `Approved` | — (Success) |
| `NotLanded` | `RetryLater` | **İlk kod korunur** (2086 → `UnreachableHost`) |
| `Indeterminate` / bütçe tükendi | `Unresolved` | `InProgress` |

`NotLanded`'da ilk koşulun korunması bilinçli: yoklama "ödeme işlenmedi" der ama **sebebi** hâlâ ilk
koddur (hattın olmaması). `InProgress`'e düzleştirmek, ölçülmüş bir gerçeği bilinmezliğe çevirirdi.

---

## 8. Bu tablonun doğurduğu bilinçli sapma

Peer kuralı harfiyen: *"`Refusal` yalnız host'tan gelen açık ret yanıtıyla üretilir."* Uygulanan hâli
**daha sıkı**: `Refusal` hiç üretilmiyor (§4). Sonucu:

- Kartın gerçekten reddedildiği vakalar da `unknown` olarak kapanıyor ve platformun çözüm döngüsüne
  giriyor. **Para güvenli, akış yavaş.**
- Terminale hiç gidilmemiş retler (`PRODUCT_UNMAPPED` vb.) artık `PaymentRestriction`/`MessageFormat`
  taşıyor. Platform bu koşulları kesin-ret listesinde saymıyorsa bunlar da `unknown`'a düşer.
  **Platform tarafı okunamadığı için doğrulanamadı** (`nexoResponse.js` bu makinede yok) — orkestratöre
  soruldu.

---

## 9. `Restomenum` ek bloğu — "kart çekildi mi?" sorusunun cevabı

nexo'da adım kavramı yok: `ErrorCondition` "ödeme fonksiyonu çağrıldı mı"yı taşıyamaz. Tasarım §22.8
gereği bu bilgi ayrı ad alanında gider ve **her** yanıtta bulunur:

```json
"SaleToPOIResponse": {
  "MessageHeader": { ... },
  "PaymentResponse": { ... },
  "Restomenum": { "v": 1, "paymentInvoked": false, "reason": "TICKET_ALREADY_OPEN" }
}
```

- **`paymentInvoked` ASLA atlanmaz.** Alanın yokluğu "bilmiyorum" ile "hayır" arasında yeni bir
  belirsizlik üretirdi — W1'de kapatılan kapının aynısı.
- Kod tarafında varsayılan **`true`** (bkz. `TransportResult.PaymentInvoked`): biri yeni bir dal
  eklerken alanı yazmayı unutursa, "kart kesinlikle çekilmedi" gibi **yanlış bir kesinlik** yaymaktansa
  "çağrıldı" deyip belirsiz kalmak güvenlidir.
- `reason` = neden başarısız. `info` = yolda ne oldu (sonucu değiştirmez). İkisi ayrı anahtar:
  karıştırılsaydı başarılı bir satış, temizlik yaptı diye "sebep" taşır ve platformda hata gibi görünürdü.

### Sabit sözlük (`RestomenumReasons`)

| Anahtar | Ne zaman | `paymentInvoked` |
|---|---|---|
| `FISCAL_LINES_REQUIRED` | Kalemsiz komut | `false` |
| `PRODUCT_UNMAPPED` | Ürün → departman eşlemesi yok | `false` |
| `PAYMENT_METHOD_UNMAPPED` | Ödeme yöntemi cihazda eşlenmemiş | `false` |
| `PROVIDER_CONFIG_INCOMPLETE` | Yapılandırma eksik/çelişkili (§30.12 KDV çelişkisi dâhil) | `false` |
| `AMOUNT_FETCH_FAILED` | Tutar GET'i başarısız | `false` |
| `TICKET_ALREADY_OPEN` | Cihazda açık fiş var, temizlenemedi | `false` |
| `ALREADY_FISCALIZED` | Fiş mali hafızada, iptal edilemez | `false` |
| `INVALID_HANDLE` | Tanıtıcı geçersiz, yenilendikten sonra da okunamadı | değişir |
| `STALE_TICKET_CLEARED` *(info)* | Önceki denemeden kalan **ödemesiz** fiş kanıtla temizlendi, satış sürdü | `true` |

---

## 10. Açık fiş: kanıtlı temizlik

`FP3_Start` `2080` dönünce:

1. Tanıtıcı yenilenir (`FP3_Start` 2080'de bile hTrx'i **açık fişin** tanıtıcısıyla doldurur —
   `GmpWrapper.Start`: `handle = hTrx`, dönüş koduna bakmadan).
2. `OptionFlags(Reload)` + `GetTicket` ile fiş **okunur**; içerik silinmeden **önce** loglanır.
3. `PaymentCount == 0` **ve** banka bacağı yok ise → `VoidAll` → `Close` → aynı satışa devam
   (`Restomenum.info: "STALE_TICKET_CLEARED"`).
4. Aksi hâlde (**ödeme var**, **okunamıyor**, `PaymentCount < 0` bozuk okuma, `VoidAll` başarısız) →
   **dokunulmaz**, `Declined` + `PaymentRestriction` + reason `TICKET_ALREADY_OPEN`.

Silme kararını biz vermiyoruz; **cihazın kendi defteri** veriyor. Okumadan silmek, üzerinde tahsilat
olan bir fişi yok etmek olurdu — geri alınamaz.
