# Yerel ödeme sözleşmesi (TASLAK)

> ⚠️ **TASLAK — koda dökülmedi, değişebilir.** Bu belge, POS kasası ile sağlayıcının cihazdaki
> uygulaması arasındaki yerel çağrının sözleşmesidir. Kaynak: özel depodaki tasarım belgesinin
> §22 bölümü (K-20). Buraya yalnız **sözleşme** kopyalandı; işletme detayları (proje kimlikleri,
> adresler, sırlar) **dahil edilmedi**.

## Neden değişti

Ödeme komutu **her zaman kasadan doğuyor** (başka tetikleyici yok) ve POS istemcileri tarayıcı
değil **Electron/Flutter** uygulamaları. Bu ikisi bulut taşımasının (WSS + NATS) gerekçesini
ortadan kaldırdı.

```
ESKİ: kasa → bulut → NATS → gateway → WSS → ajan → terminal
YENİ: kasa → yerel HTTP → ajan → terminal
```

Sektör standardı da bu yönde: nexo Retailer Protocol (eski EPAS, ISO 20022), UnifiedPOS/OPOS,
Adyen yerel mod, Oracle OPI terminal modu.

## Akış

1. Kasa, platformda bir ödeme kaydı açar → **`paymentId`** alır
2. Kasa, sağlayıcının **cihazdaki uygulamasına** yerel HTTP ile `SaleToPOIRequest` yollar —
   **yalnız kimlik**, tutar yok
3. Sağlayıcının uygulaması **tutarı ve kalem dökümünü platformdan çeker**
4. Kendi protokolüyle terminale gönderir, sonucu alır
5. Sonucu **hem kasaya senkron döndürür** (kasiyer görsün) **hem platforma bildirir** (defterin
   otoritesi). İkisi birbirinin yerine geçmez.

## Üç ayrı kimlik — karıştırılmamalı

| Alan | Nedir | Neden ayrı |
|---|---|---|
| `SaleTransactionID.TransactionID` | **`paymentId`** — platformun ödeme kaydı | Defterin anahtarı: tekillik, tutar tavanı, mutabakat |
| `SaleReferenceID` | **sipariş numarası** | Sağlayıcının referansı: sorgulama, kendi kaydı, fiş |
| `MessageHeader.ServiceID` | yerel çağrının tekrar anahtarı | Ağ hatası sonrası aynı POST'u elemek |

**Sipariş numarası ödeme anahtarı DEĞİLDİR.** Bir siparişe birden çok ödeme yazılabilir (kısmi /
bölünmüş hesap), sipariş yeniden açılabilir, ve beklenen tutar bilinmezse tavan uygulanamaz.
nexo standardı da bu ikisini bilerek ayırır.

## İstek — `SaleToPOIRequest`

```jsonc
{
  "SaleToPOIRequest": {
    "MessageHeader": {
      "ProtocolVersion": "3.0",
      "MessageClass": "Service",
      "MessageCategory": "Payment",
      "MessageType": "Request",
      "ServiceID": "a1b2c3d4e5",   // ≤10 karakter, 48 saat içinde tekil
      "SaleID": "kasa-3",           // kasa istasyonu
      "POIID": "term-01"            // terminal kimliği
    },
    "PaymentRequest": {
      "SaleData": {
        "SaleTransactionID": { "TransactionID": "pay_9f3c…", "TimeStamp": "2026-09-05T15:40:12Z" },
        "SaleReferenceID": "1042"
      }
      // PaymentTransaction YOK — tutar burada taşınmaz
    }
  }
}
```

## Tutarın alınması — `GET /plugin-api/payments/{paymentId}`

Sağlayıcının uygulaması **kimliğini kanıtlayarak** (cihaz oturum token'ı — kayıt/anahtar/DER imza
akışı **aynen korunuyor**) detayı çeker. Yanıt da nexo şeklindedir:

```jsonc
{
  "PaymentTransaction": {
    "AmountsReq": { "Currency": "TRY", "RequestedAmount": 240.00 },
    "SaleItem": [
      { "ItemID": 0, "ProductLabel": "Adana", "Quantity": 2,
        "UnitPrice": 120.00, "ItemAmount": 240.00, "TaxCode": "10" }
    ]
  },
  "SaleData": { "SaleReferenceID": "1042" }
}
```

- `SaleItem` **Türkiye'de zorunlu**, **Avrupa'da hiç gönderilmez** (pazar ayrımı).
- Bu uç tutarın **tek otoritesidir**; yerel istemci onu değiştiremez.
- ⚠️ Platform erişilemezse ödeme başlatılamaz — **çevrimdışı ödeme desteklenmiyor** (bilinçli karar).

## Yanıt — `SaleToPOIResponse`

```jsonc
{
  "SaleToPOIResponse": {
    "MessageHeader": { /* aynı ServiceID, MessageType: "Response" */ },
    "PaymentResponse": {
      "SaleData": { "SaleTransactionID": { "TransactionID": "pay_9f3c…", "TimeStamp": "…" } },
      "POIData":  { "POITransactionID":  { "TransactionID": "0000123456" } },
      "PaymentResult": { "AmountsResp": { "Currency": "TRY", "AuthorizedAmount": 240.00 } },
      "Response": { "Result": "Success", "ErrorCondition": null, "AdditionalResponse": "…" }
    }
  }
}
```

`Result`: `Success` | `Failure`. Belirsizlik (para hareket etti mi bilinmiyor) `ErrorCondition`
ile taşınır.

## ⚠️ Para birimi kuralı — dikkat

Tel üzerinde tutarlar **nexo uyumlu ondalık** (`240.00`). Ama:

- **İçeride her hesap tam sayı kuruş (minor unit) kalır.** Toplama, tavan karşılaştırması, defter
  yazımı — hiçbirinde ondalık aritmetik yapılmaz.
- **Dönüşüm yalnız sınırda.** Gelirken **yuvarlama ile** (`Math.round` / `decimal.Round`) tam sayıya
  çevrilir. **Kesme (truncate) YASAK:** `99.99` bir double'da `99.98999…` olarak durur ve kesme
  **1 kuruş eksik** yazar.
- Dönüşüm tek bir yardımcıda toplanır; ikinci bir kopya çıkarsa ikisi zamanla ıraksar.

## Kapsam

Bu turda yalnız `MessageCategory: "Payment"`. nexo'nun diğer kategorileri (`Reversal`, `Abort`,
`TransactionStatus`…) zarf aynı kaldığı için sonradan kırıcı olmadan eklenebilir.

## Ajanda ne korunuyor, ne gidiyor

| Korunuyor | Gidiyor |
|---|---|
| Cihaz anahtarı, parmak izi, kayıt akışı, DER imza, oturum token'ı | WSS bağlantısı, gateway |
| Dayanıklı komut deposu + outbox + replay | Reauth döngüsü, backoff, jitter, 4429 |
| GMP sarmalayıcı, uygunluk vektörleri, DI kökü | NATS'ın tamamı |
