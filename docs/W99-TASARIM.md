# W99 — `transFlag`: cihazın zaten söylediği ama attığımız cevap alanı (TASARIM, KOD YOK)

## Neden

Cihaz her kart ödemesinde `ST_BANK_PAYMENT_INFO.transFlag` alanını dolduruyor
(`GmpInterop.cs:237`, **2 bayt**, `:252`'de `new byte[2]`). **`GmpWrapper` bu alanı hiç
okumuyor**; `GmpTicket`/`GmpPaymentLine`'a taşınmıyor, yani atılıyor.

Simülatör dizelerinde (`GmpSampleSim.exe`) `IMP_APP_TRANS_INFO_FLAG_*` diye **cevap** bayrakları
ailesi var: `APPROVED`, `APPROVED_OFFLINE`, `PIN_USED`, `SIGNATURE_REQUIRED`, `DEBIT_CARD`,
`ONUS_CARD`, `INTERNATIONAL_CARD`, `CARD_IN_CHIP_READER`, `FALLBACK_TO_MAGSTIPE`,
`TRANSACTION_VOIDED`, `NOT_PRINT_MERCHANT_COPY`, `AMOUNT_FROM_ECR` ve
**`SALE_WITHOUT_CAMPAIGN`**. Bunları taşıyacak tek aday alan `transFlag`.

**[ÇIKARIM, kanıt değil]** `transFlag` bu aileyi taşıyorsa cihaz kampanyanın uygulanıp
uygulanmadığını **zaten her ödemede söylüyor** ve biz onu atıyoruz.

## Bugün ölçülen değerler (2026-09-09, iki iz dosyası)

| değer (base64) | bayt dizisi | kaç kez | bağlam |
|---|---|---|---|
| `AAA=` | `00 00` | 73 | başarısız kart denemeleri **ve** nakit satırları |
| `AUg=` | `01 48` | 11 | **başarılı kart ödemeleri** |

→ Alan dolu ve ayırt edici; boş bir alan değil.

## Tasarım

### 1. Nereden okunur
`GmpWrapper.Map(...)` içinde ödeme satırları kurulurken — `authorizeCode`/`rrn` ile **aynı yerde**
(W79 §7 için eklenen blok). Ek cihaz çağrısı **YOK**; veri zaten gelen yanıtın içinde.

### 2. Hangi alan adıyla taşınır
`GmpPaymentLine` kaydına yeni alan:

    /// <summary>
    /// Cihazın ödeme cevabındaki 2 baytlık bayrak alanı (`ST_BANK_PAYMENT_INFO.transFlag`),
    /// **TEL SIRASINDA ham hex** (ör. "0148"). Yorumlanmaz.
    /// </summary>
    string? TransFlagHex = null

**Neden `string` ve neden ham:** bit eşlemesi **bilinmiyor** — belgede yok, simülatör dizelerinde
yalnız sabit ADLARI var, değerleri yok. Sayıya çevirmek **bayt sırası varsayımı** yapmak demektir:
`01 48` big-endian `0x0148` mi, little-endian `0x4801` mi **ölçülmedi**. Sayı olarak saklamak o
varsayımı sessizce kalıcılaştırır; bu oturumda tam olarak bu tür varsayımlardan üç kez zarar
gördük (`stan`, `inputType`, `reason ?? NOT_LANDED`). **Ham bayt dizisi hiçbir şey iddia etmez.**

`null` = alan gelmedi/okunamadı. **Boş dizi ile null AYRI**: "cihaz göndermedi" ile
"gönderdi ama sıfır" farklı olgular.

### 3. Nereye yazılır
**Yalnız GÜNLÜĞE**, platform gövdesine **DEĞİL**. Anlamı bilinmeden gövdeye koymak, okuyanın
yorumlamasına davetiye olur.

İki yer:
- Başarılı ödemede, W79'un `[gmp] TAHSİL ≠ İSTENEN` satırına ek alan: `transFlag = "0148"`.
  (O satır yalnız fark varken yazılıyor; farksız turlarda da görmek için **ayrı bir satır**
  gerekebilir — ama her satışa bir satır daha eklemek gürültü olur. Öneri: `[gmp] ödeme sonucu`
  diye YENİ bir satır **yok**; bunun yerine mevcut `fiş KAPANDI` satırına eklenir.)
- Başarısız denemede, mevcut `[gmp] fişe para YAZILMADI — başarısız deneme satırı okundu`
  satırına ek alan.

### 4. Gizlilik
2 baytlık bayrak alanı; kart verisi taşıyamaz. Yine de `PrinterCopy.Maskele` disiplini gereği
**hex olarak** yazılır, ondalık sayı olarak değil (6+ ardışık rakam maskesine takılmasın diye).

### 5. Test (mutasyonla)
- Cihaz `01 48` döndüğünde satırda `TransFlagHex == "0148"`.
- Cihaz alanı **göndermediğinde** `null` (boş dizi ile karışmaz).
- Günlük satırında değer **ham** görünür (mutasyon: ondalığa çevir → KIRMIZI).

## Bunu ne kazandırır — ve ne KAZANDIRMAZ

**Kazandırır:** bayrak deneyinin (W96b, `BANK_TRAN_FLAG_SALE_WITHOUT_CAMPAIGN`) **ölçüm aletini**.
Bugün "kampanya kapandı mı" sorusunu yalnız tutara bakarak cevaplayabiliriz — yani dolaylı
çıkarımla, ki bugün defalarca yanıldığımız yöntem. `transFlag` elde olursa kampanyalı ve
kampanyasız iki turun **farklı biti** doğrudan görülür.

**Kazandırmaz:** bit eşlemesini. Onu ancak **kontrollü iki tur** verir (kampanyalı / bayraklı).
Tek başına W99, "bu alan şu değeri aldı"dan fazlasını söylemez — ve **öyle olduğunu söylemek de
bu tasarımın amacı**.

## Sıra önerisi
**W99 → W96b (bayraklı tek kartlı satış) → banka/ticari izin.** Aleti önce kurmak, sonra deneyi
yapmak; tersi olursa deneyin sonucunu yine tutardan çıkarmaya çalışırız.
