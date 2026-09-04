# Restomenum Terminal Agent — Windows

POS ödeme terminalini süren cihaz-üstü agent'ın **Windows** tarafı. **Türkiye pazarı için hedef
platform budur** (DE → Android, bkz. [`../android/`](../android/)).

> **Durum: HENÜZ YAZILMADI.** Bu dosya, yazacak geliştirici için sözleşmeyi ve alınması gereken
> mimari kararı anlatır. Kod yok — ve olmamasının sebebi §1'deki karar.

---

## 1. Karar VERİLDİ: .NET Worker Service

**Bazı POS terminalleri yalnız .NET ile sürülebilir.** Hepsini desteklemek istediğimize göre .NET
agent opsiyonel değil, **zorunludur**. "Windows'ta da JVM koşturup tek uygulamayla idare etmek"
seçeneği bu yüzden **elendi** — terminal SDK'sı JVM'den sürülemiyor.

Sonuç: **iki ayrı uygulama kesin.** Dolayısıyla §12.2'nin dokuz değişmezi iki kez uygulanacak ve
[`../conformance/`](../conformance/) vektörleri **şarttır, tercih değil**.

> Ayrım **pazar değil terminal SDK'sıdır**. Ağırlık olarak TR→Windows, DE→Android beklense de bu bir
> eşleme değildir: Almanya'da .NET-only bir terminal kullanan restoran bu agent'a muhtaçtır. Hangi
> agent'ın gerektiği **terminal modeli başına** belirlenir.

### Vektörleri geçmek ne demek

`conformance/` altındaki üç JSON dosyası **dosyadan okunur**, koda gömülmez. Android tarafı bunu
zaten yapıyor (`ConformanceTest.kt`, 4 test). .NET tarafı da aynı dosyaları okuyup aynı sonucu
üretmelidir; sapma testte patlar, üretimde değil.

## 2. Sözleşme

Telin üzerindeki protokol, kimlik akışı ve dokuz değişmez **platformdan bağımsızdır** ve tek yerde
anlatılıyor: **[`../android/README.md`](../android/README.md) §3–§5.** Buradaki uygulama onları
birebir karşılamak zorundadır. İki kopya doküman tutmuyoruz — ayrışır.

Özet olarak Windows tarafının uygulaması gerekenler:

| Konu | Android karşılığı | Windows'ta |
|------|-------------------|------------|
| Servis modeli | `connectedDevice` foreground service | Worker Service (`BackgroundService`), otomatik başlatma, hata sonrası restart |
| Anahtar deposu | Android Keystore, dışa aktarılamaz | **CNG / DPAPI**, `NCRYPT_ALLOW_EXPORT_FLAG` **VERİLMEZ** |
| Cihaz parmak izi | `ANDROID_ID` + donanım | Makine SID + anakart/TPM kimliği — **imaj kopyalandığında değişmeli** |
| Dayanıklı store | SQLite WAL | SQLite WAL (aynı şema, §12.3) |
| WSS | OkHttp | `ClientWebSocket` |

---

## 3. Türkiye'ye özgü: ÖKC kalem dökümü

TR'nin Windows olması, §20-H'yi doğrudan bu agent'ın sorunu yapar: **TR ÖKC akışları kalem dökümü
olmadan çalışmıyor** (`Hugin.js` `SaleItems[]`, eksik satırda toptan ret; `InposClass.js`
`items[].vat`). Komut envelope'ında bugün `lines` **opsiyonel**.

Karar henüz verilmedi (§20-H, iki yol öneriliyor). Windows agent'ı yazmaya başlamadan önce bu
netleşmeli — aksi hâlde TR'de çalışmayan bir agent çıkar.

---

## 4. Neyi ASLA yapmayın

- **NATS credential'ı taşımayın** (§12.2/8). Agent yalnız gateway'e WSS ile bağlanır; kuyruğa
  doğrudan erişimi yoktur. Cihaz ele geçirilirse fark buradadır.
- **Kart verisi saklamayın** (§12.3). `result_json` yalnız `rrn`, `stan`, `approvalCode`,
  `cardLast4` (4 hane), `scheme` taşır. PAN/CVV/manyetik şerit — hiçbiri.
- **Dedupe'u "önce oku, yoksa yaz" ile yapmayın** (§12.2/2). Oturum devrinde iki soket aynı komutu
  alır, ikisi de "yok" görür → çift tahsilat. Atomik `INSERT`/`changes()` kullanın.
- **`SENT_TO_TERMINAL` sonrası SALE'i tekrarlamayın** (§12.2/6). Belirsizlik `UNKNOWN` akışıyla,
  yalnız **sorgu** ile çözülür.
- Protokolü buraya yeniden yazmayın — Android README tek kaynaktır.
