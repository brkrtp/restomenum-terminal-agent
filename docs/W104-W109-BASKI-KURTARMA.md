# W104–W109 — BASKI ARIZASI, KURTARMA VE SÜRE BÜTÇESİ (ölçüm kaydı)

Bu belge tek bir saha olayından doğan zinciri ve **ölçülen** sayıları tutar. Çıkarımlar
`[ÇIKARIM]` diye işaretli; işaretsiz her sayı bir ölçümdür.

## 0. OLAY — 2026-09-09 23:07, fiş `tkt_c1620a28016849c5b52473446c384923`

Cihaz izi (tek fiş, ham):

    23:07:17:892  PrintTotalsAndPayments (TO 10000)  ->      0  TRAN_RESULT_OK
    23:07:18:461  PrintBeforeMF          (TO 10000)  ->  61443  DLL_RETCODE_TIMEOUT  [10 113 ms]

`PrintMF` ve `Close` **hiç çağrılmadı**. Fiş **990 kuruş gerçek tahsilatla AÇIK** kaldı, masa M8
kilitlendi. Ajan bütün bu süre boyunca sorunsuz çalıştı — o fişi tekrar deneyecek bir mekanizma
YOKTU. Arıza **83 dakika** (23:07 → 00:30) sürdü ve elle çözüldü.

Kasanın gördüğü yanıt: `result:"Success"`, `reason:null`. Arızayı ancak "kalan 0 ama fiş açık"
ÇELİŞKİSİNDEN çıkarabiliyordu — ve kısmi ödemede o çelişki hiç oluşmaz.

## 1. ÇÜRÜTÜLEN ÇIKARIM — "rc 0 döndü" ≠ "bayrak set oldu"

Peer'a "adım 5" (TOTALS basıldı) teşhisi verdim; **YANLIŞTI**. Kurtarma öncesi cihazdan okunan:

    TransactionFlags = 131078 (0x00020006) = GMP3 | FROM_FILE | TICKET_HEADER_PRINTED

`TOTALS_AND_PAYMENTS_PRINTED` (bit 18) **SET DEĞİL** — oysa iz `rc 0 = TRAN_RESULT_OK` diyordu.
Yazıcı yankısı bağımsız ikinci kanal olarak aynı şeyi söyledi; fişte yalnız iki satır vardı:

    1│Espresso               %10      *9,90 │
    2│ARATOPLAM                       *9,90 │

TOPLAM/KDV/NAKİT bloğu YOK. Doğru yer **adım 6**.

⚠️ `FLG_XTRANS_FROM_FILE` (bit 2) set geldi: fiş cihazın **dosyasından** yeniden yüklenmiş.
Dosya anlık görüntüsü baskı adımından önce yazılmışsa **bayrak ile fiziksel kâğıt ayrışabilir**.
Bugün ayrışmadı (iki kanal da aynı dedi) ama ayrışabileceğini bilerek ilerliyoruz.

## 2. MALİ SAYAÇ — üçüncü ve HUKUKEN belirleyici kanal

Eşleşme/durum yanıtından (`GMPDLL_2026_09_10_002038.TXT`):

    saat       EkuNo  ZNo  FisNo
    00:28:27     1     90     5
    00:29:44     1     90     5
    00:30:19     1     90     5
    00:30:54     1     90     6   <-- DEĞİŞTİ

Kurtarma `✓ FİŞ KAPANDI` satırını **00:30:33**'te yazdı. Yani:

- Mali dip **gerçekten mali belleğe işlendi** — bayraktan ya da rc 0'dan çıkarım değil.
- Asılı fiş o ana kadar mali numara **TÜKETMEMİŞTİ** (FisNo 5'te duruyordu) → kurtarma **TEK**
  mali fiş üretti, iki değil.

**Nerede DOLU görüldü:** yalnız eşleşme/durum yanıtı. ⚠️ `ST_TICKET`'ta `ZNo`/`FNo`/`EJNo`
alanları VAR (`GmpInterop.cs:621-623`) ama **hiçbir fiş yanıtında dolu görülmedi** (izde `"FNo"`
ve `"EJNo"` sıfır kez). "Fiş yanıtından bedavaya alırız" demek şu an dayanaksız olur.
`FP3_GetCurrentFiscalCounters(hInt, ref ZNo, ref FNo, ref EKUNo)` adanmış çağrısı var
(`GmpInterop.cs:3728`), zaman aşımı parametresi yok — **`IGmpWrapper`'a bağlı değil, ölçülmedi.**

## 3. SÜRE ÇAKIŞMASI — kasa 180 sn'de pes ediyordu

Kasa: `LOCAL_TIMEOUT_MS = 180 sn`. Ajanın adım adım süreleri toplamı kimse tarafından
tutulmuyordu:

| adım | W104 öncesi | W104 sonrası |
|---|---|---|
| `Payment` | 90 000 | 90 000 |
| `PrintTotalsAndPayments` | 10 000 | 100 000 |
| `PrintBeforeMF` | 10 000 | 100 000 |
| `PrintMF` × **3 deneme** | 300 000 | 300 000 |
| `Close` | 10 000 | 10 000 |
| **toplam** | **420 sn** | **600 sn** |

→ **Bu kapı W104'ün AÇTIĞI kapı değil**; W104 genişletti. En büyük tek kalem `PrintMF`'in üç
denemesi (300 sn) ve o W104'ten önce de öyleydi.

Kasa önce pes ederse deneme **belirsiz** kalır ve uzlaştırma turuna düşer — biz devam edip
başarırken. Sonuç: *kasa "bilinmiyor", cihazda para alınmış.* Bu, düzeltilen arızadan kötü.

### Bütçe (W109)

Satışın **EN BAŞINDA** başlayan **165 sn**'lik toplam bütçe; her baskı adımına
`min(adım tavanı, kalan)` geçilir.

**Neden 165, 170 değil:** ilk 170 seçildi ve aritmetik çivi kırmızıya döndü — `Close` bütçeden
muaf ve tavanı 10 sn, yani `170 + 10 = tam 180`: sıfır pay. `165 + 10 = 175`, geriye 5 sn kalır.

- **Ödeme bütçeden yemez** (kısaltılmaz): kart/PIN insanla etkileşim; kesmek **belirsizlik**
  üretir. Bütçe yalnız baskıyı kısıtlar — baskı kesilirse para çoktan alınmıştır, fiş açık kalır,
  bu belirsizlik değil bilinen aradurumdur ve kurtarması vardır.
- **`Close` bütçeden MUAF, bilerek:** baskı bitti, geriye yalnız kapatmak kaldı; onu bütçe
  yüzünden atlamak **basılmış** bir fişi açık bırakmak olurdu — en kötüsü.
- Yeni sebep **`PRINT_BUDGET_EXHAUSTED`**, `PRINT_TIMEOUT`'tan AYRI: zaman aşımı "cihaza gittik,
  cevap gelmedi" der (cihazda bir şey olmuş OLABİLİR); bu ise "cihaza HİÇ GİTMEDİK" der — o
  adımın olmadığı KESİN.
- **Kilit tavanı 150 sn**: satış yolunda cihaz kilidi eskiden tavansız bekleniyordu; bütçenin
  faydası tam orada kayboluyordu. Dolarsa ödeme cihaza HİÇ gitmez ve **kesin ret** döner
  (`PaymentRestriction` / `TERMINAL_BUSY` / `paymentInvoked:false`). `Busy` koşulu bilerek
  kullanılmadı: platformda "bilinmiyor"a eşleniyor ve kasayı boş yere uzlaştırmaya sokardı.

## 4. `reason` SÖZLEŞMESİ (kasayla mutabık)

`reason` **o yanıtı üreten denemeyi** anlatır, **nihai durumu DEĞİL**. Tarihsel bir olgudur.
Sonradan kurtarma fişi kapatırsa o yanıt güncellenmez — çoktan gönderilmiştir.

**Nihai durumun tek otoritesi `TicketQuery`.**

⚠️ **Hiçbir alandan kâğıdın varlığı/yokluğu ÇIKARILMAZ.** Cihaz hiçbir izde kâğıt kodu
(`TRAN_RESULT_NO_PAPER`, 0x0020) döndürmedi; kâğıt hakkında **sıfır ölçüm** var.
`closed + PRINT_FAILED` yalnız şunu der: fiş mali olarak kapandı, mali dip baskısı üç denemede de
başarısız oldu. Kasiyere "yazıcıyı ve ruloyu **kontrol edin**" dedirtmek doğru; "kâğıt yok"
dedirtmek **yanlış**.

## 5. KURTARMA (W104/W105)

Üreticinin akışı (`GMP3OlumsuzDurumYönetimi_v1.2` §2) kod hâlinde:
adım 3 `MF_PRINTED` → `Close`; 4 `BEFORE_MF` → `PrintMF`+`Close`; 5 `TOTALS` → `PrintMF`+`Close`;
6 bayraksız + tam ödenmiş → **tam zincir**.

**Belgeden bilerek sapılan TEK nokta adım 6.** Üretici harfiyen "`PrintMF` + `Close`" diyor ama
oraya YALNIZ hiçbir baskı yapılmamışken gelinir; `PrintMF`'i tek başına çağırmak müşteriye
**toplamları hiç basılmamış** bir mali belge vermek olurdu.

**`VoidAll`/`VoidPayment` hiçbir dalda YOK** — kullanıcının açık talimatı ve zaten bu fişlerde
tanım gereği müşteriden alınmış para var. Test dört baskı bitinin **16 kombinasyonunu** × ödeme
durumu × kuru prova/uygula tarayıp ikisinin de çağrılmadığını çiviliyor.

**Ödemesi tamamlanmamış fişe dokunulmaz** (`TICKET_NOT_COMPLETE`, üretici adım 7) — `--onayla`
bunu GEVŞETMEZ.

### Süpürme üç şartı (W105 + W108)
1. **Satış her zaman öncelikli** — cihaz kilidi `Wait(0)`, meşgulse tur ATLANIR. Güvenliği
   sağlayan şey yaş eşiği DEĞİL, kilittir: satış yolu `_islemKilidi`'ni **baskı zinciri dahil**
   tutar (`LocalSaleHandler:677`).
2. **Üstel geri çekilme** (`Outbox.Bekleme` ile aynı eğri) + 5 denemede alarm.
   **W108:** 8 denemeden sonra otomatik dokunma DURUR ve kayıt 24 saate ertelenir — kâğıt
   gerçekten bittiyse her deneme dakikalarca cihazı HERKESE kapatır. **Vazgeçme değil, silme
   değil:** fişte para var; elle `--finish-ticket` her an çalışır.
3. **Ödemesi eksik fişe dokunulmaz**, `VoidAll` hiçbir dalda yok.

**Tetik KANITTIR, yoklama değil:** `pending_finish` kaydı yalnız baskı gerçekten patladığında
yazılır. Cihazı periyodik yoklamak, Türkiye'de NORMAL olan kısmi ödeme aradurumunu da her turda
cihaza sordururdu.

## 6. `TicketQuery` (W106) — CİHAZA GİTMEZ

Kasa ölçtü: fiş kapandıktan ve masa kapandıktan sonra köprünün kendi kaydı bayat kaldı ve
kasiyere **yanlış** uyarı çıktı ("1 ödeme henüz deftere işlenmedi" — işlenmişti).

⚠️ **Cihaza gitmemek bir tercih değil:** `FP3_Start` açık fiş YOKKEN **yeni fiş açar** ve
kapatılsa bile **mali numarayı tüketir**. Her masa kapanışında bir sorgu = her sorguda bir mali
numara. Cevap tamamen defterden; kilit almaz, satış sürerken bile anında döner (ölçüm: **2,9 ms**).

Üç AYRI olgu, üç AYRI alan (katlamak yanlış uyarının ta kendisiydi):
`ticketState` (OPEN/CLOSED/**UNKNOWN**) · `ledgerWritten` · `pendingFinish*`.
**UNKNOWN "AÇIK" DEĞİLDİR.** Ölçülmeyen alan gövdeye KONMAZ.

## 7. KAÇAK — testler yeşilken yol ayrımı hiç eklenmemişti

W106 commit edildiğinde ayrıştırıcı/işleyici/gövde testliydi, 560/560 yeşildi, derleme geçiyordu
— ama `AgentWorker`'daki **yol ayrımı hiç eklenmemişti** (yama betiği ilk çapada durmuş).
Testlerin hiçbiri HTTP dikişine dokunmadığı için görmediler. Canlı ajana gerçek bir POST atınca
çıktı: `{"error":"UnsupportedCategory"}` HTTP 400.

→ **Ders:** "testler yeşil + derleme geçiyor" bir ucun BAĞLI olduğunu göstermez. Dikişin
kapatılabilir yarısı artık testli.

## 8. AÇIK MADDELER
- **Sahiplik/lease** (peer (b)): aynı cihaza ikinci bir süreç bakarsa kilit paylaşılmaz. Bugün
  tek ajan + tek yuvalı eşleşme fiilen ikinci süpürücüyü imkânsız kılıyor ama **yazılı değil**.
- **Süpürme sonucu platforma olay olarak yazılmıyor** (peer (d)) — şu an yalnız günlük +
  `TicketQuery`.
- `FP3_GetCurrentFiscalCounters` **ölçülmedi**; cihaza gidiyorsa kilit/süre maliyeti var.
- Fiziksel kâğıtta toplam bloğu bir kez mi iki kez mi basıldı — **kullanıcıda**, sorulacak.
- `Restomenum.Agent.Gmp`'nin **test projesi yok**: `TimeoutPrint` sabiti ve `Map`'in alan taşıması
  mutasyonla sınanamıyor. Sahada ölçümle doğrulandı (bayrak 131078 geldi → taşıma çalışıyor).
