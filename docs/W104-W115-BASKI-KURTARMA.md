# W104–W115 — BASKI ARIZASI, KURTARMA, SÜRE BÜTÇESİ VE ÇİFT TAHSİLAT (ölçüm kaydı)

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

⚠️ **KÂĞIT KONUSUNDA ERKEN İDDİAM ÇÜRÜTÜLDÜ — bkz. §9.** Uzun süre "cihaz hiçbir izde kâğıt
kodu döndürmedi, kâğıt hakkında sıfır ölçümümüz var" dedim. **2026-09-10 02:21'de cihaz
`0x0020 TRAN_RESULT_NO_PAPER` DÖNDÜRDÜ.** Aşağıdaki paragraf o tarihten önceki hâli anlatır ve
tarih kaydı olarak duruyor; geçerli kural §9'da.
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


---

# GECENİN İKİNCİ YARISI (02:00–02:45) — W110–W115

## 9. `0x0020` — ERKEN İDDİAM ÇÜRÜTÜLDÜ

**Ölçüm (2026-09-10 02:21, kasa tarafından okundu, benim ucumdan gitti):**

    "reason":"PRINT_FAILED","printFailedStep":"PrintBeforeMF","printFailedCode":"0x0020"

`0x0020 = TRAN_RESULT_NO_PAPER`. Cihaz kâğıdın bittiğini **söylüyor**.

**Neden daha önce görmedik — [ÇIKARIM, ama güçlü]:** 23:07'de zaman aşımı **10 sn**'ydi ve
cihaz cevabını veremeden kesiyorduk. W104'ün uzattığı süre yalnız kurtarmayı değil
**teşhisi de** mümkün kılmış. Bunu ben iddia etmedim; olgu sırası gösterdi.

**Sonuç:** `reason = OUT_OF_PAPER` artık **AJANDA** üretiliyor (`GmpCodes.NoPaper` sabiti
burada; kasada türetilseydi aynı sabit iki yerde durur ve biri sessizce kayardı).
`printFailedCode` yine HAM gidiyor. Kasiyere artık "KÂĞIT BİTTİ, rulo takın" denebilir —
iddia değil, cihazın beyanı.

**`FP3_GetEcrStatus` taraması — SONUÇ: YOK.** 131 fonksiyon tarandı. Durum/statü adayları
yalnız: `FP3_Echo`, `FP3_FunctionEcrParameter`, `FP3_FunctionEkuReadInfo`,
`FP3_FunctionModuleReadInfo`, `FP3_FunctionPaymentCheck`, `FP3_GetCurrentFiscalCounters`,
`FP3_GetTlvData`. Ucuz bir kâğıt yoklaması için doğrudan çağrı YOK.
`ST_ECHO.status` ise VAR ve atılıyordu (W112) — artık taşınıyor. Ölçüm: sağlıklı **0x0000001D**
(15 okuma, ikisi yayım sonrası doğrulama). Bloke anda (02:05:48) `retcode 0` ile **tamamen
sıfır**. ⚠️ Farkın sebebi **kanıtlanmadı**: "başarı + tüm alanlar sıfır" deseni
`FP3_VoidPayment` hatasında da görülmüştü ve orada "boş yanıt" demekti; cihaz kâğıtsız değil
**meşgul** olduğu için de böyle dönmüş olabilir. Hiçbir karar buna bağlı değil.

## 10. ÇİFT TAHSİLAT — 19,80 TL, ÖLÇÜLDÜ

**Masa M8, oturum `a299f947-f88b-431c-8656-25a095b4c46d`, iki AYRI mali fiş:**

| fiş | ödeme | tutar | alındı | kapandı | kapatan |
|---|---|---|---|---|---|
| `tkt_1cbc1b17…` | `pay_6edebb4d…` | 990 | 02:05:27 | 02:18:28 | **elle** (`TicketFinish`) |
| `tkt_39dd485e…` | `pay_f72c3c5c…` | 990 | 02:21:01 | 02:22:15 | **süpürme, kendiliğinden** |

9,90'lık masa için mali kayıtta **19,80** ve **iki mali belge** var. Geri alınamaz: iki fiş de
kapalı ve mali bellekte; `VoidAll` işlemez, `VoidPayment` kapanmış fişte geçersiz. Düzeltmesi
iade/fiş iptali — **mali ve ticari karar, ajan tek başına yapmaz.**

### Zincir (kendi payım dahil)
1. **02:05:38 — ajanı yayım için durdurdum**; o anda cihazda NAKİT ödeme UÇUŞTAYDI. Para
   cihaza 02:05:27'de yazılmıştı. Yanıt kayboldu (ECONNRESET).
2. 02:09:10 — platform denemeyi `Superseded/stale` saydı; kasa "bilinmiyor"da kaldı.
3. 02:18:28 — o fişi `TicketFinish` ile kapattım (masayı açmak için) ve **bağ silindi**.
4. 02:21:01 — kasa YENİ paymentId ile tekrar denedi; bağ olmadığı için **yeni fiş** açıldı.

⚠️ **ACI AYRINTI:** birinci fiş kapatılmasaydı ikinci ödeme `AMOUNT_EXCEEDS_REMAINING` ile
**REDDEDİLİRDİ** (990 istenen > 0 kalan) ve para cihaza hiç gitmezdi. Masayı açmak için
yapılan şey, çift tahsilatı önleyecek tek kapıyı kaldırdı.

### Asıl kusur kapıda
Ajan dedupe'u **paymentId** üzerinden çalışıyor. Kasa yeni attemptId + yeni paymentId ile
geldiğinde bu ajan için MEŞRU ve YENİ bir ödemedir. "Aynı masaya ikinci kez aynı tutar" diye
bir kapı YOK; o işi fişin kalanı yapıyordu ve fiş kapanınca o da gitti.
→ Kasa tarafında E46 kapısı yazıldı: aynı oturum + aynı terminal için çözülmemiş başka bir
deneme varken yeni tahsilat gönderilmiyor; tetiklendiğinde `TicketQuery` ile cihaza soruluyor.
**Sorgu başarısız olursa kapı YİNE kapalı kalıyor — bilgi eksikliği kapıyı AÇMAZ.**

### Kök sebep artık SORULABİLİR (W115)
İkimiz de "boşta görünüyordu" diye TAHMİN ettik (kasa o gece kendini altı kez yeniden
başlatmış, hiçbirinde bakmamış). `TicketQuery` yanıtına **`deviceBusy`** eklendi: işlem kilidi
tutuluyorsa cihazda uçuşta iş VAR. Sorgu kilidi ALMAZ, yalnız okur.
⚠️ Anlık fotoğraf — garanti değil, gözlem. Ama körlemesine durdurmaktan iyi.

## 11. SÜPÜRME — İLK SAHA KANITI

    02:22:12  [bitir] fiş okundu { toplam=990 tahsil=990 kalan=0
                                   bayraklar=131078 (0x00020006) plan=TumBaskiVeKapat adım 6 }
    02:22:13  adım tamam PrintTotalsAndPayments
    02:22:14  adım tamam PrintBeforeMF          ← 0x0020 alan adım, bu kez geçti
    02:22:15  adım tamam PrintMF
    02:22:15  ✓ FİŞ KAPANDI · [tamamla] TAMAMLANDI { deneme = 1 }
    02:22:45  kapanış platforma bildirildi (200 Recorded / COMPLETED)

Kimse tetiklemedi. **Dün gece 83 dakika süren arıza, 39 saniyede kendiliğinden kapandı.**

## 12. W111 — İŞARET BASKIDAN **ÖNCE**

Süpürme işareti zincir DÖNDÜKTEN sonra yazılıyordu. Süreç zincirin **ortasında** ölünce
(02:05, tam bu oldu) satır hiç yazılmadı ve fiş 990 kuruş tahsilatla açık kaldı ama süpürme
onu **görmedi**. `closed_tickets`'ın kapattığı boşluğun birebir kardeşi.
**İlke: TEHLİKELİ ADIMDAN ÖNCE kalıcı iz bırak.**

## 13. W113 — `NO_OPEN_TICKET` ≠ `UNKNOWN`

İkisi de eskiden `UNKNOWN`dı ve kasa haklı olarak sordu: "kimliksiz UNKNOWN açık fiş yok
demek mi?" Tek kelimeyle iki beyanı taşımak, okuyanın TAHMİN etmesini gerektiriyordu.

- `NO_OPEN_TICKET` — kimliksiz sorgu, defterde bağlı açık fiş yok.
- `UNKNOWN` — belirli bir fiş soruldu, defterde izi yok.

⚠️ `NO_OPEN_TICKET` **"CİHAZDA açık fiş yok" DEMEK DEĞİL**, "bu ajanın defterinde bağlı açık
fiş yok" demek. Sorgu cihaza gitmez (gitse mali numara tüketirdi); cihazda ajanın bilmediği
bir fiş durabilir ve o `BayatFisiTemizle`'nin işidir.

## 14. W114 — UCUZ / PAHALI DENEME

Kullanıcının talimatı: *"kâğıt bittiğini bildiriyorsa … kalan 0 ise bir interval başlatıp fiş
kapanışını otomatik yapsın **başarılı olana kadar**."* Bu, W108'in sekiz denemede durma
kuralıyla çelişiyordu. **Çelişkiyi ölçüm çözdü:**

W108'in gerekçesi denemenin **pahalı** olduğu varsayımıydı — her deneme cihazı dakikalarca
kilitler. Bu, cihaz **cevap vermediğinde** doğru (zaman aşımı). Ama cihaz `0x0020` ile hızlı
ve kesin cevap verdiğinde deneme **ucuzdur**.

> **Bedeli olan denemeler sınırlanır, bedeli olmayanlar sınırlanmaz.**

PAHALI (sekizde durur): zaman aşımı, bütçe bitişi, **ve kod okunamadığı hâl** — bilinmeyeni
ucuz saymak sınırı sessizce kaldırmak olurdu.
UCUZ (sınırsız): cihazın kesin cevabı, `0x0020` dahil.

## 15. TEST ALTYAPISI — İKİ DERS, İKİSİ DE BU GECE ISIRDI

1. **"Testler yeşil + derleme geçiyor" bir ucun BAĞLI olduğunu göstermez.** `TicketQuery` yol
   ayrımı hiç eklenmemişken 560/560 yeşildi ve derleme geçiyordu; canlı POST ortaya çıkardı
   (`{"error":"UnsupportedCategory"}`).
2. **Askıda kalan bir mutasyon "başarısız" sayılmazsa batarya kilitlenir.** Kilit tavanını
   kaldıran mutasyon testi sonsuza kadar astı. İki düzeltme: test zaman aşımlı bekliyor
   (asılma → başarısızlık) ve mutasyon betiği `dotnet test`i zaman aşımıyla koşup askıda
   kalmayı KIRMIZI sayıyor.

## 16. AÇIK MADDELER (güncel)
- **Çift tahsilatın mali düzeltmesi** — kullanıcının kararı bekleniyor.
- **(b) sahiplik/lease** yazılı değil.
- **(d) süpürme sonucunu platforma olay olarak itmek** — kasa tarafı kendi kaydını tazeliyor
  (E44), ama çok kasalı kurulumda diğer istasyonlar öğrenmez.
- `FP3_GetCurrentFiscalCounters` **ölçülmedi** (cihaza gidiyor mu bilinmiyor).
- Fiziksel kâğıtta toplam bloğunun bir kez mi iki kez mi basıldığı — kullanıcıda.

---

## W124 — Kâğıt uyarısı temizlendikten sonra bir daha hiç açılmıyordu

**Belirti (kullanıcı, üç tur):** "Kağıt bitti bilgisini UI frontendde göremiyorum."

**Yanlış aradığım yerler:** olayın kasaya gidip gitmediği, `reason` eşlemesi,
süpürmenin uyarıyı düşürüp düşürmediği. Hepsi doğruydu.

**İmza (ölçüm 2026-09-10 04:31):**

```
KAGIT UYARISI : YOK | adim: PrintBeforeMF
```

Adım dolu, saat boş. İkisi bir arada olamaz — `SetPaperWarning(true)` çağrıldı,
adımı yazdı, saati yazmadı.

**Kök sebep:** `device_health` satırı bir kez oluştuktan sonra

```sql
ON CONFLICT(terminal_id) DO UPDATE SET paper_seen_step = $adim
```

yalnız adımı tazeliyor, `paper_warning_at`'ı NULL bırakıyordu. `paperWarning`
alanı `PaperWarningAt is not null` ile hesaplandığından uyarı **ilk temizlikten
sonra bir daha hiç açılmadı**. "İlk görülme saatini koru" niyetiyle yazılmıştı;
uyarı **kapalıyken** açılmasını da engellediği fark edilmemişti.

**Düzeltme:** `paper_warning_at = COALESCE(device_health.paper_warning_at, $now)`
— doluysa dokunma (ilk görülme korunur), NULL ise şimdiyi yaz (uyarı açılır).

**Testler bunu neden kaçırdı:** sahte depo doğru davranıyordu (`?? now`); yani
**sahte, gerçeği yansıtmıyordu**. Yeşil 618 test, gerçek SQL'deki kusurun
üstünü örtüyordu. Yeni çivi `KAGIT_UYARISI_TEMIZLENDIKTEN_SONRA_TEKRAR_ACILABILIR`
**gerçek `CommandStore`'a** karşı koşuyor. Mutasyon: eski SQL → KIRMIZI,
`$now` (son görülme) → KIRMIZI.

> **Kural:** sahtenin gerçekten davranışça ayrıldığı her nokta, yeşil bir testin
> altında saklanan bir kusurdur. Kalıcılık davranışı gerçek depoya karşı çivilenir.

### Yan ölçüm — kâğıt bilgisinin ömrü 3 ms

```
04:29:36.779  { PrintBeforeMF, code = 0x0020 }   ← kâğıt bitti, TEK sefer
04:29:36.782  { reason = OUT_OF_PAPER }          ← 3 ms sonra kasaya
04:29:46 →    35 deneme, hepsi 0xF01C DEVICE_BUSY
```

Cihaz kâğıtsızlığı **bir kez** bildirir. Sonrasında kendi penceresi açık olduğu
için her komuta yalnız "meşgulüm" der — yani kâğıt durumu **sorulabilir bir şey
değil, yakalanması gereken bir an**. Kaçırılırsa geri getirilemez; bu yüzden
kalıcılık kusuru doğrudan görünmezliğe dönüşüyordu.

---

## W125 — 24 saatlik park, kurtarmayı kâğıt üstünde bırakıyordu

**Ölçüm (2026-09-10, üçüncü kâğıt testi):** cihaz kademeli kötüleşti.

```
06:35:51  deneme 482  0xF01C   sureMs =  5 474   ucuz
06:36:15  deneme 483  0xF003   sureMs = 13 785   ucuz    ← eşiğin 1,2 sn ALTI
06:37:31  deneme 484  0xF000   ~66 000 ms        PAHALI  → 24 SAAT PARK
```

Kural doğru çalıştı: tek pahalı deneme otomatik pencereyi kapattı. Ama sonucu
kabul edilemezdi — üzerinde **990 kuruş** olan fiş 24 saat park edildi ve **rulo
takılsa bile kendiliğinden kapanmayacaktı**. Kullanıcının değişmez şartı ise
"başarılı olana kadar". 24 saat, "vazgeçme"nin başka adıydı.

### İki düzeltme

**① Durma yok, seyrelme var.** `TamamlamaErtelemesi` 24 saat → **30 dakika**.
Ölçülen en pahalı deneme 66 saniye; 66/1800 = cihazın **%3,7'si**. Seyrek ama
vazgeçmiş değil. Log da artık doğruyu söylüyor: "DURDURULDU" → "SEYRELTİLDİ".

**② Park, cihaz düzelince İPTAL edilir.** Serbest bırakan olay tahmin değil
ölçüm olmalı: **olağan bir satışın baskı zinciri baştan sona geçtiyse** kâğıt
vardır ve cihaz komut alıyordur. `ITicketSnapshotStore.UndeferPendingFinishes`
tam orada çağrılır — yeni bir cihaz çağrısı eklemeden, bedelsiz.

> ⚠️ Kurtarmanın kendi başarısı bunu yapamaz: kurtarma rulo çıkıkken bile
> başarılı olabiliyor (04:20'de ölçüldü), yani cihazın düzeldiğini KANITLAMAZ.

### W121'de yazdığım bir iddia ÇÜRÜDÜ

W121'i belgelerken "eşiğin iki yakası arasında ölçülmüş hiçbir şey yok" demiştim.
**13 785 ms bunu çürüttü** — eşiğin 1,2 saniye altında gerçek bir ölçüm var.
Eşik hâlâ savunulabilir (o deneme gerçekten ucuzdu) ama sandığım kadar geniş bir
boşlukta oturmuyor ve bir dahaki ölçüm onu aşabilir.

### Mutasyonun yakaladıkları

- **Gerçek ölü savunma:** başarılı baskıdaki park boşaltma kancası hiç test
  edilmiyordu — silsen 626 test yeşil kalıyordu. Düzeltmenin CAN DAMARI buydu.
- **Benim yanlış iddiam:** `WHERE next_attempt_at > $now`'ı "geciktirmeyi önler"
  diye anlatmıştım. Mutasyon gösterdi ki kaldırınca davranış aynı kalıyor —
  o bir koruma değil, yalnız gereksiz yazmayı önlüyor. Yorum düzeltildi, o
  savunmayı çivilediğini iddia eden test **kaldırıldı**: mutasyonu yeşil geçen
  bir test çivi değildir, çivi görüntüsüdür.

---

## W126 — "takıldı ama deneniyor" ile "park edildi" AYRI şeyler

Panel ajanının isteği ve gerekçesi doğru: `finishStalled` iki farklı gerçeği aynı
kutuya koyuyordu ve **kasiyerin yapması gereken şey ikisinde zıt**.

```
takıldı ama deneniyor  →  BEKLE, kendiliğinden çözülebilir
park edildi            →  BEKLEME, kendiliğinden ÇÖZÜLMEYECEK — rulo taksa bile
```

İkisine aynı şeyi söylemek, "rulo takın, fiş kendiliğinden kapanacak" cümlesinin
94 denemelik durumda yanlış umut vermesiyle aynı sınıf — hatta daha kötüsü,
çünkü park hâlinde beklemek **kesinlikle** sonuç vermiyor.

### Neden zamandan türetilmedi

`next_attempt_at` her iki hâlde de "gelecekte"dir: 10 saniye sonrası ile yarın
orada ayırt edilemez. Zamandan çıkarım yapmak tam da bu gece iki kez yanıldığımız
yöntem. Bu yüzden park depoda **açıkça** işaretlenir: `pending_finish.parked_until`
(NULL = park edilmemiş).

İz üç yerde temizlenir — park boşaltmada, yeni denemede, kayıt düşerken. Temizlenmezse
olağan ritme dönmüş bir fiş panelde hâlâ "park edilmiş" görünür ve kasiyer boşuna
vazgeçerdi.

### Gövde

```json
"finishStalled":      true,          // otomatik pencere kapandı (eşik 8)
"finishDeferred":     true,          // ↑ bunun İÇİNDE daha dar hâl
"finishDeferredUntil": 1757486251000 // yalnız park VARKEN
```

Bekleyen kayıt yoksa **üçü de hiç yazılmaz**; park bitmişse süre yazılmaz —
geçmiş bir tarihi "bekleme süresi" diye sunmak olurdu.

Mutasyon: 7 mutasyon, hepsi kırmızı.

---

## W127 — "okunamadı" ile "sıfır" aynı kutuya konmuştu: iptal yolunda PARA RİSKİ

**Kullanıcı:** "Fiş iptal de çalışmıyor."

**Ölçüm (2026-09-10 16:30, canlı):**

```
16:30:02.574  Reversal isteği
16:30:03.320  fiş sonucu { outcome = Unknown, acikFisVarMiydi = True,
                           odemeSayisi = 0, tutar = 0 }
16:30:22.451  bildirim { state = FAILED }
```

`[gmp] iptal öncesi fiş` satırı log'da **YOK** → iptal öncesi okuma patlamış.

### İptalin çalışmama sebebi ayrı bir kusur DEĞİL

Aynı tıkanıklık: kâğıt bitik, cihaz komut kabul etmiyor, `VoidAll` reddedildi.
Para hareket etmedi.

### Ama ölçerken çıkan asıl kusur

```csharp
if (_gmp.OptionFlags(...).Ok && _gmp.GetTicket(h, out var oncesi).Ok)
{
    iptalTutar = oncesi.PaidAmountMinor;   // yalnız OKUNABİLDİYSE
    iptalSayi  = oncesi.PaymentCount;
}
var r = _gmp.VoidAll(h, out _);            // ⚠️ OKUNAMASA DA DEVAM
```

Okuma patlarsa `iptalTutar`/`iptalSayi` **sıfır kalıyor ve akış devam ediyordu**.
`VoidAll` başarılı olsaydı: 990 kuruşluk fiş iptal edilir, kasaya **"iptal edildi,
0 kuruş"** dönerdi — parayı geri alır, geri aldığımızın **tek kaydını** da yok
ederdik. İptalden sonra fiş yok; sorulacak yer kalmaz.

> **Bizi kurtaran şey tasarım değildi, cihazın aynı anda tıkalı olmasıydı.**

### Düzeltme

Okuma başarısızsa **`VoidAll` hiç çağrılmaz**: `Unknown` + `VOID_READ_FAILED` +
`InProgress`. "Olmadı" demek de yanlış olurdu — kasa bunu belirsizlik olarak
görmeli. Fişe dokunulmaz, kayıt durur, insan karar verir.

Ayrıca banka yolundaki `Approved` dalı tutar/sayı/oturumu bildirmiyordu (raporlama
deliği) — eklendi.

Mutasyon: 4 mutasyon, hepsi kırmızı.
