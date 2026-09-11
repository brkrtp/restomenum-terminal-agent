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

---

## W128 — KALICI SEBEP: bayat tanıtıcı kurtarması TEK YOLA konmuştu

**Kullanıcı:** "Bu bug'ı daha önce çözmüştük fakat şimdi tekrarlandı. Kalıcı
sebebini araştır."

**Cevap: tekrarlamadı — aynı kusur BAŞKA KAPIDAN girdi.**

### Kanıt kodun kendi yorumunda duruyordu

```
// ── BAYAT TANITICI KURTARMASI ──  (Okuma.cs)
// Bu ölçüldü: 2026-09-06 01:50 ve 2026-09-07 14:30 … 6 turun 6'sında 2317
// aldı ve belirsizlik ÇÖZÜLEMEDİ — tam da en gerekli anda.
```

Daha önce çözülen bug buydu. Ama kurtarma **yalnız `ReadTicket`'e** kondu:

| yol | `h == 0` tazeleme | bayat (2317) kurtarma |
|---|---|---|
| `ReadTicket` | var | **var** |
| `Void` | var | **YOK** |
| `VoidTicket` | var | **YOK** |

`_handle == 0`, "tanıtıcı lazım"ın **tek** işareti sanılmıştı. Sıfır olmayan ama
**ölü** bir tanıtıcı `ReadTicket` dışında hiçbir yerde görünmüyordu.

### Sahadaki ölçüm

```
16:30:02 → 16:30:03   iptal, 727 ms,  outcome=Unknown
16:39:48 → 16:39:48   iptal, 273 ms,  outcome=Unknown   ← RULO TAKILI
```

Cihaza gidip bekleyen bir çağrı 273 ms'de dönmez; **ölü tanıtıcı hemen döner.**
Süreç 12 saattir ayaktaydı (04:25:12) ve tanıtıcı olay anında ölmüştü — ajan
yeniden başlamadan iptal çalışamazdı. "Yeniden başlatınca düzeliyor" desenİnin
tamamı bu.

### Neden nokta yaması yapılmadı

Her yeni yol kendi kurtarmasını hatırlamak zorunda kalırsa kusur **bir sonraki
kapıdan yine girer** — bugün olan tam olarak buydu. Tanıtıcı artık **tek kapıdan**
alınıyor (`GecerliTanitici`): hem yokluğu hem bayatlığı orada çözülüyor.

Bedeli, tanıtıcı elde varken bir ek `OptionFlags` canlılık sınaması. Testte
gizlenmedi, **açıkça yazıldı**: bilinçli bir seçim.

### İkinci kusur: teşhis edilemezlik

`[iptal] fiş sonucu` log satırı cihazın kodunu **yazmıyordu**. Kod üretiliyor ve
kasaya gidiyordu ama bizim tarafımızda hiçbir yerde görünmüyordu — bugün iptalin
neden patladığını log'dan çıkaramamamızın sebebi buydu. `kod` alanı eklendi.

> Teşhis edilemeyen bir kusur, çözülmüş sayılamaz.

Mutasyon: 4 mutasyon, hepsi kırmızı.

---

## W129 — kendi keyfi sınırım gerçek bir anahtarı kesti, ve red sebebi görünmüyordu

**Ölçüm (2026-09-10 18:32:59):**

```
WRN geçersiz TicketFinish (ticketFinishId zorunlu)
    [uzakUç=127.0.0.1:58174 ua=axios/1.15.0 uzunluk=317]
```

Kasa geçerli bir istek gönderdi; ben reddettim.

### İki ayrı kusur

**① Sınır yanlıştı.** Kasanın deterministik anahtar şekli
`<uuid>-<terminalId>-ticketfinish` = 36+1+16+13 = **66 karakter**. Sınırım
**64**'tü. 64 benim keyfi seçimimdi; anahtarın kendisi iyi tasarlanmıştı
(deterministik, okunabilir, çakışmaz). Sınır **taşmayı** engellemek için var,
makul anahtarları kesmek için değil → 128.

**② Red sebebi görünmüyordu.** "Yok", "boş", "çok uzun" ve "yanlış yer" aynı tek
mesajla yazılıyordu. Karşı taraf kendi tarafında hata aradı — oysa kusur bendeydi.
Artık sebep ayrışıyor ve **gelen uzunluk da yazılıyor**, böylece karşı taraf
ölçebiliyor.

> W128'deki görünmez cihaz koduyla **aynı sınıf** hata:
> **teşhis edilemeyen bir red, karşı tarafı kör arama yaptırır.**

Mutasyon: 4 mutasyon, hepsi kırmızı.

---

## E58 turu — kurtarma düğmeden önce davrandı

Aynı turda ölçülen tam iz (`tkt_d2d6126492c44b9fb1869b784d6776ce`):

```
18:28:33  kalem satırı basıldı { adet=1, eksik=False }   ← kâğıt VAR
18:29:28→18:30:08  süpürme 5 tur ATLADI (cihaz satışta)
18:29:48  UÇUŞTAKİ komutlar atlandı { atlanan=1 }        ← 19,80 kuralı çalıştı
18:30:13  PrintBeforeMF patladı { 0xF003 } → reason=PRINT_TIMEOUT
18:30:14  ödeme bildirimi APPROVED → COMPLETED
18:30:23→18:31:41  denemeler 1–6: 0xF01C, ~5500 ms, bedel=ucuz, aralık 10 sn
18:31:52  fiş okundu { 990/990, kalan=0, 0x00020006, TumBaskiVeKapat, adım 6 }
18:31:55  ✓ FİŞ KAPANDI  (deneme 7)
```

**Baskı patlamasından kapanışa 101,7 saniye, gözetimsiz.**

İki şey kanıtlandı:

- **W121 bedelini ödedi:** altı deneme de `0xF01C`/5,5 sn olduğu için **ucuz**
  sınıflandı ve 10 saniyede bir koştu. Eski kod-tabanlı kuralda `0xF01C` pahalı
  sayılıyordu; öyle kalsaydı üstel geri çekilme devreye girer ve fiş dakikalarca
  açık kalırdı.
- **Kâğıt bitmesi ≠ baskı zaman aşımı.** Kalem satırı basıldı, yani kâğıt vardı;
  `PrintBeforeMF` yine de 100 saniyede zaman aşımına düştü. `OUT_OF_PAPER` dalı
  KOŞMADI ve `paperWarning` açılmadı — **doğrusu da bu**: cihaz kâğıtsız olduğunu
  söylemedi.

---

## W130 — iki ayrıştırıcı aynı şeye farklı davranıyordu

Panel ajanı sordu: "`ticketCancelId` de 66 karakter ve çalışıyordu — tutarsızlık
var, kontrol et." Haklıydı.

**Ölçüm:**

| anahtar | sınır | 66 karakterlik gerçek anahtar |
|---|---|---|
| `ServiceID` | >10 ret | — |
| `SaleID` | >32 ret | — |
| `ticketFinishId` | **>64 ret** | **REDDEDİLDİ** |
| `ticketCancelId` | **sınır YOK** | geçti |

Aynı kasadan gelen, aynı şekilde üretilmiş iki anahtardan biri reddedildi öteki
geçti. **Kimse bunu tasarlamamıştı** — iki ayrı yerde, iki ayrı gün yazılmıştı.

Peer'in ikinci ihtimali (**iptaller sessizce reddediliyor olabilir**) ölçümle
**çürüdü**: iptal yolunda hiç sınır yoktu, dolayısıyla sessiz red de yoktu.
`VOID_INCOMPLETE` yanıtları gerçekten cihazdan geliyordu.

### Düzeltme

Tek politika: `TekrarAnahtari.EnCok = 128`, `Dogrula()` her iki ayrıştırıcıda.
Sebep ayrışıyor ve **gelen uzunluk yazılıyor**.

> ⚠️ Bu, iptal yoluna **YENİ bir red ekliyor** — orada eskiden hiç sınır yoktu.
> Bilinçli: sınır ölçülenin (66) iki katı, ve red artık sebebiyle görünür.
> Bu anahtarlar depoda birincil anahtar oluyor (`event_id`); sınırsız dış girdiyi
> anahtar yapmak, boyutu karşı tarafın insafına bırakmaktır. Ama para yolunda
> sessiz bir red bırakmaktansa **görünür ve cömert** bir sınır seçildi.

Anahtarı OLMAYAN istekler etkilenmez (fiş kapsamında `ticketCancelId` isteğe
bağlı) — bu ayrıca çivilendi, çünkü doğrulamayı ona da uygulamak bugüne kadar
çalışan istekleri sessizce reddetmek olurdu.

Mutasyon: 5 mutasyon, hepsi kırmızı (biri 22 testi düşürdü).

---

## W131 — `TICKET_NOT_COMPLETE` replay EDİLMEZ, ve bu DOĞRUDUR

Panel ajanı aynı `ticketFinishId`'yi ikinci kez gönderdi (2026-09-10 18:48:01):

```
18:46:45  TicketFinish finishId=85b09a94-…-fin-227e3a79  → 1084 ms, TICKET_NOT_COMPLETE
18:48:01  AYNI finishId                                  → 1071 ms, cihaza YİNE GİDİLDİ
```

`[bitir] tekrar gelen istek — saklanan sonuç replay edildi` satırı **çıkmadı**.

Depoda ölçüldü:

```
command_id   85b09a94-…-fin-227e3a79
kind         finish
state        RECEIVED      ← final DEĞİL
result_json  NULL
```

### Bu bir kusur DEĞİL — tasarım

Sonuç yalnız `Kapandi || FisAcikDegil` olduğunda saklanır. `TICKET_NOT_COMPLETE`
**geçici** bir durumdur: kalan tahsil edilirse fiş tamamlanabilir hâle gelir.
Kesin sonuç diye saklamak, elle kurtarmayı tek denemeye indirirdi — oysa bu
zarfın varlık sebebi insanın doğru anı seçebilmesi.

Tekrar **güvenli**: fiş AÇIK olduğu için `FP3_Start` 2080 döner, yeni fiş
açılmaz, mali numara tüketilmez, `plan = Dokunma` olduğu için fişe dokunulmaz.
Bedeli ~1 saniye cihaz zamanı.

> **Beklentim yanlıştı, kod doğruydu.** Kendi kodum hakkında saklama kuralına
> bakmadan beklenti bildirdim; ölçüm onu çürüttü.

### Sınırlı bir kalıntı (izlenerek bulundu, ölçülmedi)

Fiş BAŞKA bir yolla kapanırsa (süpürme ya da kalanın tahsili) ve aynı `finishId`
sonra tekrar gönderilirse: kayıt hâlâ `RECEIVED`, istek cihaza gider, açık fiş
bulunmaz ve `TanitciyiYenile` yoklama fişi açıp kapatır → **bir mali numara
tüketilir**. Sonrası güvenli: o çağrı `FisAcikDegil` döner, final saklanır,
sonraki basışlar replay edilir. Yani kayıp **`finishId` başına en fazla bir**.

Panel tarafı bunu kendi ucunda zaten önlüyor (E59d: tamamlamadan önce
`queryDeviceTicket`, açık fiş yoksa istek gönderilmiyor).

---

## W132 — ÖLÇÜMÜN HANGİ KODDA YAPILDIĞI, ÖLÇÜMÜN KENDİSİ KADAR ÖNEMLİ

`ticketFinishId` reddi için "66 > 64" teşhisini koydum, sonra **geri aldım**:
iki reddin de gövdesi 317 bayttı ve anahtar kısalsaydı gövde de kısalırdı diye
düşündüm — "alan gövdeye hiç girmiyor" dedim.

**Geri alma YANLIŞTI.** Panel ajanı kısaltmayı yapmış ama süreci yeniden
başlatmamıştı; Electron ana süreci diskten yeniden okumaz. Yani ölçtüğüm iki
istek de **eski koddu** ve ikisi de 66 karakterlik anahtarı taşıyordu. Yeniden
başlatıldıktan sonra aynı istek 300 bayt oldu (fark tam 17 = 66−49) ve **geçti**.

İlk teşhis doğruydu; W129/W130 tam da onun düzeltmesi.

> **Ders (üç kez farklı kılıkta çıktı):** ölçüm, ölçtüğünü sandığın kodda
> yapılmıyor olabilir. Sahte depo gerçeği yansıtmıyordu (W124); mutasyon betiği
> dosyayı geri koyarken zaman damgasını kaybedip bayat derleme ölçüyordu; şimdi
> de yeni kod eski süreçte ölçüldü. **Kod değişti ≠ çalışan kod değişti.**

---

## W133 — `IsTcpKeepAlive` AÇILDI (yapılandırma değişikliği, kod değil)

**Tarih:** 2026-09-11 00:11 · **Dosya:** `publish/host-windows/GMP.XML`
**Yedek:** `GMP.XML.keepalive-oncesi-<damga>` (aynı klasörde)

```
<IsTcpKeepAlive>FALSE</IsTcpKeepAlive>   →   TRUE      (tek satır, bayt düzeyinde)
```

### Belge ne diyor (`GMP3 OPTIMIZASYONU_TR`, s.2)

> Değer TRUE olarak ayarlandığında, cihaz ile yazılım arasındaki bağlantı periyodik
> olarak (**AckTimeOut süresi baz alınarak**) kontrol edilir. Bağlantının kopması
> durumunda, çalışmakta olan GMP fonksiyonunun **tanımlı timeout süresi beklenmeden**
> `DLL_RETCODE_TIMEOUT` (0xF003) hatası döndürülür.

⚠️ **Dil farkı kayda geçsin:** bu paragrafta "tavsiye ederiz" **geçmiyor** — tarif
ediyor. Aynı belgede `LogThreadOpen` için *"önemle tavsiye ederiz"*, Ethernet için
*"önerilmektedir"*, hafifletilmiş sorgular için *"rica ederiz"* yazıyor. Yani
`IsTcpKeepAlive` bir optimizasyon belgesinde faydası anlatılarak yer alıyor;
fiilen öneri, ama açık bir tavsiye fiili yok.

### Şartlar sağlanıyor (ölçüldü)

| şart | değer |
|---|---|
| Ethernet bağlantısı | `IsTcpConnection TRUE`, 192.168.1.189:7500 |
| DLL ≥ 16v25 | **16.38.0.0** |
| `AckTimeOut` | 3000 ms |
| `CommTimeOut` | 90000 ms |

### Beklenen davranış değişikliği

Kopma artık `CommTimeOut`'u (90 sn) beklemeden bildirilmeli. Bugün ölçtüğümüz
`0xF003`'ler ~100 saniyeydi ve bu `CommTimeOut`'a çok yakındı — kablo testlerinde
(D1–D3) bu sürenin **saniyeler mertebesine** inmesi bekleniyor. **Ölçülmedi;**
kablo turunda doğrulanacak.

### Yeniden başlatma ölçümleri (00:11)

```
00:11:26  status=0x0000001D sağlıklı
00:11:33  EŞLEŞME BAŞARILI — INGENICO/IDE281 seri=2B25035325
00:11:45  departman tablosu bildirildi (12)
```

**Bonus:** kâğıt uyarısı yeniden başlatmayı ATLATTI — `paperWarning True`,
damga 23:47:29 değişmedi. Uyarı bellekte değil defterde yaşıyor; ilk kez
doğrulandı.

⚠️ **Kod yayımlanmadı.** Çalışan ikili hâlâ 2026-09-10 16:51 derlemesi.
W129/W130 (anahtar sınırı 128 + ayrışan red sebebi) commit'li ama SAHADA DEĞİL.

---

## W134 — keepalive AÇILDI ama bu senaryoda DEVREYE GİRMEDİ (ölçüldü)

Kablo çekikken ödeme (D1), aynı senaryo iki ayarla:

| | `cihazMs` | `toplamMs` |
|---|---|---|
| `IsTcpKeepAlive FALSE` | 66 163 | 68 112 |
| `IsTcpKeepAlive TRUE` | **66 160** | 73 196 |

**Cihaz bacağı 3 milisaniye farkla aynı.** Toplamdaki ~5 saniye tamamen bizim
bulut bacağımızdan (`tutarMs` 790→5926, `oturumMs` 373→5483) — cihazdan değil.
Yani "biraz uzadı" yorumu yanlış olurdu; cihaz tarafında hiçbir şey değişmedi.

### 66 saniyenin tamamı (GMPDLL izi)

```
00:15:53.034  Socket Opened
00:16:14.073  WSA Connect. WSAGetLastError = 10060      ← 21,04 sn
00:16:15.088  Socket Opened
00:16:36.113  WSA Connect. WSAGetLastError = 10060      ← 21,03 sn
00:16:37.115  Socket Opened
00:16:58.156  WSA Connect. WSAGetLastError = 10060      ← 21,04 sn
00:16:59.162  Connection Error
              FP3_Start → 0x0000F000 DLL_RETCODE_PORT_NOT_OPEN
```

`66,13 sn = 3 × 21 sn + ~1 sn aralar`. KAPALI koşu birebir aynı desende
(23:52:19.487 → 23:53:25.634 = 66,15 sn).

### Süreyi belirleyen şey GMP.xml DEĞİL

- `AckTimeOut` (3000) × `IpRetryCount` (3) döngüsü **çalışmadı**: 3 deneme oldu
  (`IpRetryCount`) ama her denemenin süresini `AckTimeOut` belirlemedi.
- `CommTimeOut`'a (90 000) **dayanılmadı**.
- Süreyi belirleyen: **işletim sisteminin TCP connect zaman aşımı**
  (`WSAETIMEDOUT`, hata 10060) — Windows'ta ~21 sn. GMP.xml'deki hiçbir ayar
  bunu etkilemiyor.
- Dönen kod `0xF003 TIMEOUT` değil, **`0xF000 PORT_NOT_OPEN`**.

### Keepalive neden girmedi — kesin sebep

Keepalive **KURULU** bir bağlantıyı yoklar. Burada soket hiç bağlanamadı; hata
`connect()` aşamasında. Ortada canlı tutulacak bağlantı YOK. DLL izinde
keepalive'a dair tek satır bile geçmiyor.

> **Belge yanlış değil**, senaryo farklıydı: belge "ödeme işlemi başlatıldıktan
> SONRA yaşanabilecek bir kesinti" diyor. Bizimki baştan kopuk bir hedefe
> bağlanma denemesiydi.

### D2 için düşülen tahmin (sınanacak)

Bağlantı KURULUYKEN kopacağı için keepalive devreye girmeli ve **`0xF003`**
dönmeli. Bakılacak şey süreden önce **KOD**: `0xF003` → keepalive çalıştı;
`0xF000` → yine bağlanamama yoluna düşüldü.

### Bedavaya kazanç (ölçüldü, uygulanmadı)

Bu senaryoda 66 saniyenin **44'ü boşa**: ölü adrese üç kez bağlanmayı denemek
hiçbir şey kazandırmıyor. `IpRetryCount` 3 → 1 bu senaryoyu **~21 saniyeye**
indirir; cihaz varken davranış değişmez (ilk denemede bağlanır).

---

## W135 — `IpRetryCount` DEĞİŞTİRİLEMİYOR: DLL, `GMP.XML`'i ayrıştırdığı anda GERİ YAZIYOR

W134'te ölçtüğüm "66 saniyenin 44'ü boşa" kazancını uygulamayı denedim.
**Tutmadı.**

```
00:46            GMP.XML s.26: <IpRetryCount>3</IpRetryCount> → 1   (diff tek satır)
00:47:03         ajan yeniden başlatıldı
00:47:04.097     DLL izi: Interface Data = ... IpRetryCount:3 ...
00:47:04.097268  GMP.XML dosya damgası — AYNI MİLİSANİYE
```

DLL, dosyayı **ayrıştırdığı anda geri yazıyor**. Diskteki değer yine 3;
düzenleme açılışta ezildi.

### Dosya OKUNUYOR — değer reddediliyor

Aynı satırda `IsTcpKeepAlive:TRUE` görünüyor; o da benim değişikliğim ve
**korunmuş**. Yani DLL dosyayı okuyor, `IsTcpKeepAlive`'ı olduğu gibi geri
yazıyor, ama `IpRetryCount=1`'i kabul etmeyip 3'e çeviriyor.

En olası açıklama bir **alt sınır/normalleştirme** (muhtemelen minimum 3).
⚠️ **ÖLÇÜLMEDİ, çıkarım.** Kesinleştirmenin yolu 2 yazıp ne geri geldiğine
bakmak — bir yeniden başlatma daha.

### Sonuç

Kablo çekikken **3 × 21 sn ≈ 66 sn taban** ve `GMP.XML` üzerinden
düşürülemiyor. 21 saniyenin kendisi zaten işletim sisteminin TCP connect zaman
aşımı ve GMP ayarlarına bağlı değil.

> Panelde "Cihaza gönderiliyor…" yazısının 66+ saniye sürmesi **ayarla
> çözülebilecek bir şey değil.** Kasa tarafındaki ara evre (`waitingDevice`)
> bu yüzden yardımcı değil, **asıl** çözüm.

### Yöntem notu

Bu, "kod değişti ≠ çalışan kod değişti" dersinin **dördüncü** kılığı — ve ilk
kez düzeltme *hiç* uygulanmadığı hâlde uygulanmış görünüyordu. Panel ajanının
"açılış izinden doğrula" uyarısı olmasa "yaptım" diye rapor edilecekti.
**Yapılandırma değişikliği, çalışan sistemde doğrulanana kadar yapılmamış
sayılır.**

---

## W136 — 3 × 21 SANİYE ÖLÜ ZAMAN DEĞİL, **KURTARMA PENCERESİ**. Önerim yanlıştı.

W134'te "66 saniyenin 44'ü boşa gidiyor, `IpRetryCount` 3→1 yapılsın" dedim.
W135'te DLL'in bunu kabul etmediğini ölçtüm. **İyi ki etmemiş.**

### Ölçüm (D1, 1. tur — kablo yarıda geri takıldı)

```
00:54:17.565  Socket Opened                        ← 1. deneme
00:54:38.599  WSA Connect 10060                    ← 21,03 sn, BAŞARISIZ
00:54:39.607  Socket Opened                        ← 2. deneme
00:54:55.109  Socket Closed  (hata YOK)            ← BAĞLANDI, 15,5 sn içinde
00:54:55.110  FP3_Start → TRAN_RESULT_OK
00:54:55.111  FP3_TicketHeader → OK
00:54:56.707  FP3_OptionFlags  → OK
00:54:57.103  FP3_ItemSale     → OK
00:54:57.993  FP3_Payment      → OK      ← PARA GERÇEKTEN ALINDI
              … fiş kapandı, 990/990, mali fiş kesildi
```

Kullanıcı kabloyu ~00:54:55'te geri taktı. **İkinci deneme onu yakaladı** —
kendi 21 saniyelik penceresinin 15,5. saniyesinde, 5,5 saniye payla.

### Önerim uygulansaydı ne olurdu

`IpRetryCount = 1` ile **tek** deneme olurdu: 00:54:17 → 00:54:38'de başarısız.
Kablo 16,5 saniye SONRA geri geldi. Ödeme reddedilirdi ve 9,90 TL tahsil
edilemezdi.

> **"Boşa giden 44 saniye" diye adlandırdığım şey, kablonun geri takılması için
> tanınan süreymiş.** Ölçümü doğru yapmıştım (3 × 21 sn), ANLAMINI yanlış
> okudum: bir maliyet sandım, oysa bir kurtarma penceresiymiş.

Bu, aynı sayıya bakıp iki farklı sonuca varmanın örneği. Farkı yaratan şey ek
bir ölçüm değil, **senaryonun değişmesiydi** (kablo geri takılınca ne oluyor).
Kablo çekik KALIRSA 44 saniye gerçekten boşa gidiyor; geri TAKILIRSA parayı
kurtarıyor. İkisi aynı ayarla yönetiliyor ve ikinci hâl daha değerli.

### Sonuç

`IpRetryCount` 3'te KALMALI. DLL'in değeri reddetmesi kusur değil, koruma.
W134'teki "bedavaya kazanç" önerisi **geri çekildi**.

⚠️ Ve `FP3_Payment` 00:54:57.993'te çağrıldı, 00:54:58.439'da OK döndü — yani
`paymentInvoked: true` doğru. Kasa tarafının bu bayrağa dayanarak "para
çekilmedi" dememesi, ölçümle desteklendi.

---

## W137 — 21 saniye SABİT DEĞİL: ilk deneme ısınmış ağda kısalıyor

W134/W135'te "3 × 21 sn = 66 sn taban" demiştim. D1'in 2. turu bunu düzeltti.

```
1. deneme  01:00:45.243 → 01:00:53.297 =  8,05 sn   ← KISA
2. deneme  01:00:54.311 → 01:01:15.334 = 21,02 sn
3. deneme  01:01:16.347 → 01:01:37.369 = 21,02 sn
           01:01:38.378  FP3_Start → 0xF000 PORT_NOT_OPEN
           cihaz bacağı toplam 53,14 sn
```

Karşılaştırma: kablonun uzun süredir çekik olduğu ilk ölçümde üç denemenin
**üçü de** 21,03/21,04 sn sürmüş ve toplam 66,13 sn olmuştu.

Aradaki fark, panelin bildirdiği 68/73 sn ile 56 sn arasındaki farkı da
açıklıyor — gürültü değil, **ilk denemenin süresi.**

**Neden:** bu turdan hemen önce (00:54–00:55) kablo takılıydı ve bağlantı
gerçekten kurulmuştu. İşletim sisteminin ağ komşuluk durumu "ısınmış" olduğu
için ilk `connect()` tam zaman aşımını beklemeden düştü. Sonraki denemeler o
durum bayatlayınca tam 21 saniyeye çıktı.

> **Düzeltme:** 21 saniye bir ÜST sınır, sabit değil. Ölçülen aralık şu an
> 8,05 – 21,04 sn. Dolayısıyla "66 saniye taban" ifadesi de yanlış; toplam,
> kablonun ne kadar süredir çekik olduğuna göre **53–66 sn** arasında değişiyor.

Tahmin tuttu: bağlantı hiç kurulamadığı için keepalive yine devreye girmedi ve
kod `0xF000` (PORT_NOT_OPEN) geldi, `0xF003` değil. D2 (bağlantı kuruluyken
kopma) hâlâ keepalive'ın asıl sınavı.

---

## W138 — SAHİPLİK KAPISI: başkasının fişini mali olarak kesinleştirme

Panel/kasa ajanı `TicketFinish` zarfına `saleSessionId` ekledi ve bende
değişiklik ZORUNLU olmadığını söyledi. Yine de ekledim — çünkü **cihaza dokunan
taraf benim.**

### Zarar

Tamamlama, cihazdaki **açık fişi** tamamlar; hangi fiş olduğuna bakmaz. Kasanın
tetiği kendi `open` kaydı ve o kayıt bayatlayabiliyor (karşı taraf E44'te
belgeledi). S1 kapatılırken cihazda S2'nin fişi açıksa, kapı olmadan **S2'nin
fişi mali olarak kesinleşir** ve S2'nin kasiyeri sebebini hiç öğrenmez.
**Mali kesinleşme geri alınamaz.**

### Üç durum, ikisi geçer

| durum | karar | gerekçe |
|---|---|---|
| kanıt YOK | **GEÇ** | "alan gelmedi" ≠ "başkasının"; yokluğu suçlama saymak eski kasa sürümünde yarım mali belgeyi yarım bırakırdı |
| defterde bağ YOK | **GEÇ** | karşılaştıracak şey yok; uydurma kesinlik satmayız — zaten bu zarfın varlık sebebi o durum |
| ikisi VAR ve FARKLI | **DUR** | cihaza hiç gidilmez, `reason: SESSION_MISMATCH` |

Bu, `paymentInvoked` üç durumluluğuyla ve `PendingFinishAttempts` yokluğuyla
aynı ilke: **ölçülemeyeni suçlama, ama ölçüleni de yok sayma.**

### İki taraflı savunma

Kasa da kendi tarafında engelliyor. Aynı kuralın iki bağımsız yerde durması
bilinçli: bu gece tek noktaya bağlı üç savunmanın da (E61 alan yolu, kâğıt
uyarısı, `finishTicket` testsizliği) sessizce öldüğünü gördük.

Mutasyon: 5 mutasyon, hepsi kırmızı. 646/646 yeşil.

---

## W139 — HER GMP ÇAĞRISI KENDİ SOKETİNİ AÇIP KAPATIYOR

Kablo testlerinin izini okurken çıktı; keepalive tartışmasının tamamını
açıklıyor.

Başarılı bir satışın DLL izi:

```
FP3_Start         Socket Opened … Socket Closed
FP3_TicketHeader  Socket Opened … Socket Closed
FP3_OptionFlags   Socket Opened … Socket Closed
FP3_ItemSale      Socket Opened … Socket Closed
FP3_Payment       Socket Opened … Socket Closed
```

**Ajan ile cihaz arasında sürekli bir bağlantı YOK.** Her komut için yeni bir
TCP oturumu kuruluyor, komut biter bitmez kapanıyor.

### Keepalive neden hiçbir turda devreye girmedi

Keepalive **kurulu** bir bağlantıyı yoklar. Burada bağlantı yalnız tek bir
çağrının süresi boyunca var. Kısa çağrılarda (ölçülen: `FP3_Payment` nakit
akışında **0,44 sn**, `FP3_ItemSale` 0,47 sn) yoklanacak pencere pratikte yok.

⇒ Keepalive ancak **tek bir çağrı uzun sürerse** anlamlı. Öyle tek bir çağrı
var: `FP3_Payment (TO : 90000)` — ve yalnız **kart** ödemesinde uzun sürüyor,
çünkü cihaz "kartı okutun" ekranında insanı bekliyor ve soket açık kalıyor.

> Nakit akışında kopma penceresini elle yakalamak **imkânsız**: `FP3_Payment`
> 0,44 saniye sürüyor. Bu, üç D2 denemesinin neden hep `FP3_Start`ın
> connect'inde yakalanıp `0xF000` ürettiğini de açıklıyor.

### Sınanacak tahmin (kart turu)

| | beklenen |
|---|---|
| `paymentInvoked` | **true** (ilk kez) |
| kod | **0xF003 TIMEOUT** — `0xF000` DEĞİL, çünkü soket KURULUYKEN koparılacak |
| süre | 90 sn'lik `TO` dolmadan dönmeli; ne kadar kısaldığı asıl ölçüm |

### Yan sonuç: bağlantı maliyeti

Her çağrı bir TCP el sıkışması demek. Cihaz erişilebilirken bu milisaniyeler
(ölçülen soket açılış→kapanış: 0,35–1,6 sn arası, komuta göre). Ama cihaz
ERİŞİLEMEZKEN her çağrı kendi 3 × 21 saniyesini ödüyor — kurtarma zincirinin
neden bu kadar uzun sürebildiğinin bir parçası da bu.

---

## W140 — KEEPALIVE ÇALIŞTI: 90 saniye → 4,45 saniye

Kart turu, kablo `FP3_Payment` sırasında çekildi (cihaz kart bekliyordu, soket
KURULUYDU). W139'da "keepalive ancak tek uzun çağrıda anlamlı" demiştim; o tek
çağrı buydu.

```
02:22:26.336  >>>>> FP3_Payment (TO : 90000)
02:22:26.339  Socket Opened
02:22:26.340  Socket Connected                    ← bağlantı KURULDU
02:22:30.792  Socket recv Error!!  WSAGetLastError = 0
02:22:30.792  FP3_Payment → 0x0000F003 DLL_RETCODE_TIMEOUT
```

**4,45 saniye.** 90 000 ms bütçenin %5'i. `AckTimeOut` 3000 ms — yani bir yoklama
aralığı artı tespit. Kod `0xF003 TIMEOUT`, `0xF000 PORT_NOT_OPEN` değil.

Düşülen tahminin üç maddesi de tuttu: `paymentInvoked` true, kod `0xF003`,
zaman aşımı dolmadan dönüş.

### Kasa 89 saniye ölçtü — ikisi de doğru, aynı şey değil

```
02:22:26.336 → 02:22:30.792   FP3_Payment                   4,45 sn  ← keepalive
02:23:00.828 → 02:23:49.920   3 bağlanma denemesi (21+21+5)   ~79 sn  ← BELİRSİZLİK ÇÖZÜMÜ
02:23:50.841                  cihazMs = 87743, toplamMs = 89372
```

O 79 saniye ödeme çağrısı değil: ödemenin cihaza inip inmediğini öğrenmek için
fiş okunuyor ve o **yeni bir bağlantı** istiyor. Kablo hâlâ çekik olduğu için
`connect()` aşamasında düşüyor — orada keepalive'ın canlı tutacağı bir şey yok
(W139).

> **Keepalive olmasaydı** `FP3_Payment` 90 saniyeyi doldururdu ve toplam ~175
> saniye olurdu. Kasanın 180 saniyelik kapısına **5 saniye** kalırdı. Ayar
> muhtemelen bizi zaman aşımına düşmekten kurtardı.

### Yöntem notu

Karşı taraf "keepalive süreyi kısaltmadı" diye negatif sonuç kaydetmek üzereydi
ve ölçümü doğruydu — 89 390 ms gerçekten öyleydi. Yanlış olan, o sürenin
**neyin** süresi olduğuydu. Toplam süre, içindeki tek çağrının süresi hakkında
hiçbir şey söylemiyor.

**Aynı sayı, iki farklı olguya ait.** Bu gece bunun üçüncü örneği: 68 vs 73
saniye farkının cihazda değil bulut bacağında olması (W134), 21 saniyenin sabit
sanılması (W137), ve şimdi bu.

---

## W141 — W127/W128 CANLIDA DOĞRULANDI: bayat tanıtıcı yine oldu, 350 ms'de iyileşti

Sıfır tahsilatlı fişin iptali (2026-09-11 02:38:57 → 02:39:00). Yayımdan sonraki
ilk iptal.

```
02:38:57.019  FP3_OptionFlags → 2317 APP_ERR_GMP3_INVALID_HANDLE   ← BAYAT
02:38:57.369  [gmp] tanıtıcı bayat (2317) — yenileniyor { eski = 7684097394182473636 }
02:38:57.369  FP3_Start → 2080 ALREADY_DONE                        ← taze tanıtıcı
02:38:57.705  FP3_OptionFlags → OK
02:38:58.058  FP3_GetTicket   → OK
02:38:58.432  FP3_VoidAll     → TRAN_RESULT_OK   (1,56 sn)
02:38:59.994  FP3_Close       → OK
```

**Bu, 2026-09-10'da 12 saat boyunca iptali kilitleyen arızanın ta kendisi** —
aynı kod, aynı yol. Fark: W128'in tek kapısı devreye girdi ve **350
milisaniyede** çözdü. Karşı taraf "bayat tanıtıcı sorunu yok" diye okudu; sorun
VARDI, düzeltme çalıştı.

W127 de doğrulandı: denetim izi iptalden ÖNCE yazıldı
(`toplam=2380, tahsil=0, odemeSayisi=0, bankaBacagi=False, sahibi=ed52b1c1…`).
Okuma patlasaydı `VoidAll` hiç çağrılmayacaktı.

W128'in ikinci parçası (`kod` alanı) da göründü: `kod = (yok)` — işlem başarılı
olduğu için boş. Asıl değerini bir sonraki başarısızlıkta gösterecek.

### ⚠️ Kayda geçen bir düzeltme: mali numara TÜKETİLDİ

Karşı taraf "fiş iptal edildiğine göre mali numara tüketilmedi, doğru mu?" diye
sordu. **Yanlış.** Baskı bitlerinin set edilmemesi doğru (`0x00020002` yalnız
başlık), ama cihaz **açılıp yazdırılmadan iptal edilen fiş için de mali fiş
numarasını harcıyor** (daha önce ölçüldü). İptal numarayı geri vermiyor.

O boşluk **kayıp değil**, beklenen bir şey — ama "iptal ettik, numara yanmadı"
diye kaydedilirse ileride sayaç denetiminde açıklanamayan bir atlama olarak
görünür.

⚠️ Bu turda YENİDEN ölçülmedi: FNo okuma ucu hâlâ bağlı değil
(`FP3_GetCurrentFiscalCounters` interop'ta var, `IGmp`'ye bağlanmamış). Önceki
ölçüme dayanıyor. Üretici belgesi bu iş için `FP3_ECHO`'yu öneriyor.

---

## W142 — FNo ucu BAĞLANDI: sayaçlar zaten geliyordu, biz düşürüyorduk

Peer'e iki kez "FNo kanıtı üretemem, sayaç ucu bağlı değil" demiştim. **Yanlıştı.**
Sayaçlar her `FP3_Echo` yanıtında geliyordu; `ST_ECHO` sınıfında karşılıkları
olmadığı için Newtonsoft onları **sessizce düşürüyordu.**

Ham JSON, cihazın DLL izinden birebir (2026-09-11 00:11:26):

```json
{"retcode":0,"status":29,"kvc":"…","ecrMode":1,"mtuSize":8192,
 "szEcrVersion":"2001010000","szEcrNewVersion":"215132107T",
 "EkuNo":1,"ZNo":91,"FisNo":2,"Handle":0,"UniqueID":"…"}
```

Sınıfta yalnız `retcode, status, kvc, ecrMode, mtuSize, ecrVersion, date, time,
activeCashier` vardı. Dördü de yoktu.

### Eklenenler

`ST_ECHO` → `ZNo`, `FisNo`, `EkuNo`, `Handle` (adlar cihazınkiyle **birebir**).
Sözleşmeye `EchoDurum(Status, ZNo, FisNo, EkuNo, Handle)`; `IGmp.Echo` artık onu
döndürüyor. Hem eşleşme açılışında hem `[gmp] echo` satırında kayda geçiyor.

**Bedava gelen:** `Handle` — cihazda asılı işlem tanıtıcısı, `0` = açık fiş yok.
Üretici belgesi asılı-handle kontrolü için `FP3_Start` yerine tam da bunu
öneriyor (*"performans kaybı oluşturan FP3_START yerine
FP3_GETCURRENTHANDLE"*), ve `FP3_Start` açık fiş YOKKEN yeni fiş açıp **mali
numara tüketirken** bu bilgi tüketmiyor. W131'deki kalıntıyı kapatmanın yolu da
bu.

⚠️ **Sıfır, "sayaç sıfır" demek değil.** Bloke cihaz `retcode 0` ile tamamen
sıfır dönebiliyor (2026-09-10 02:05:48). Hiçbir karar bu alanlara bağlanmadı;
yalnız taşınıyor ve günlüğe yazılıyor.

### Yeni test projesi — çünkü bu köprü HİÇ test edilmemişti

Interop `net8.0-windows`, Core testleri `net8.0`. Referans alınamadığı için
JSON→`ST_ECHO`→`EchoDurum` yolu bugüne kadar test edilmemişti — kusurun tam
olarak orada saklanabilmesinin sebebi bu.

`tests/Restomenum.Agent.Gmp.Tests` eklendi ve testler **cihazın gerçek
yanıtına** karşı koşuyor; uydurma gövdeye karşı test yazmak kaçırdığımız kusuru
yeniden üretirdi.

Eşleme satır içinden `GmpWrapper.Cevir(ST_ECHO)` yordamına çıkarıldı — satır içi
kaldığı sürece test edilemezdi.

### Mutasyon

| mutasyon | sonuç |
|---|---|
| sayaçları loga koyma | KIRMIZI |
| ZNo ↔ FisNo karıştır (log) | KIRMIZI |
| `Cevir` sayaçları taşımasın | KIRMIZI |
| `Cevir`de ZNo ↔ FisNo değiştir | KIRMIZI |
| **JSON eşlemesini sessizce boz** (`[JsonProperty("Zed")]`) | **KIRMIZI** |

Sonuncusu en önemlisi: derlenen ama sessizce sıfır üreten mutasyon — kusurun
gerçek şekli buydu ve artık yakalanıyor.

648/648 (Core) + 3/3 (interop) yeşil.

### W142 — SAHADA, ilk okuma

Yayım 2026-09-12 00:51 (ikili damgası 00:51, GMP.XML ve GMPSmartDLL.dll hash'leri
değişmedi, appsettings korundu).

```
00:51:49  [Pairing] mali sayaçlar (HAM): ZNo=92 FişNo=3 EküNo=1 asılıTanıtıcı=0
```

**İlk kez mali sayaçları okuyabiliyoruz.** Karşılaştırma, dün gece DLL izinden
çıkardığım ham JSON ile:

| | ZNo | FisNo | EkuNo |
|---|---|---|---|
| 11 Eylül 00:11 (DLL izi) | 91 | 2 | 1 |
| 12 Eylül 00:51 (canlı log) | **92** | 3 | 1 |

`ZNo` 91→92: arada bir **Z raporu** alınmış (gün sonu). `FisNo` o yüzden
sıfırlanıp yeniden saymaya başlamış — iki nokta arasındaki fiş sayısı bu iki
okumadan **hesaplanamaz**. Bundan sonraki öncesi/sonrası karşılaştırmalar aynı
Z dönemi içinde anlamlı olacak.

`asılıTanıtıcı=0` — cihazda asılı fiş yok. Bu sağlık bilgisi eskiden yalnız
`FP3_Start` ile alınabiliyordu ve o çağrı açık fiş YOKKEN yeni fiş açıp **mali
numara tüketiyordu**. Artık bedava.

---

## W143 — FNo turu: yoklama fişi mali numara TÜKETMEDİ (ve bir iddiam çürüdü)

İlk kez sayaç okunabildiği için "boş fiş mali numara tüketir mi" sorusu ölçüldü.

### Tasarım: önce KONTROL

Sayaçlar yalnız açılışta okunuyor (`EchoAsync` üretimde hiç çağrılmıyor), yani
okumak için yeniden başlatmak gerekiyor. O hâlde önce **yeniden başlatmanın
kendisinin** sayaç tüketip tüketmediği ölçülmeli — kontrol olmadan sonuç
yorumlanamaz.

```
00:51:49  ZNo=92 FişNo=3      ← kontrol 1
00:57:29  ZNo=92 FişNo=3      ← kontrol 2
00:57:53  ZNo=92 FişNo=3      ← kontrol 3
```

Üç yeniden başlatma, değişim yok. **Yeniden başlatma nötr.**

### Deney

Açık fiş YOKKEN `TicketFinish` gönderildi; `FP3_Start` yoklama fişi açmak
zorunda kaldı (DLL izi):

```
00:58:36.743  FP3_Start → TRAN_RESULT_OK     ← 2080 DEĞİL: YENİ fiş açıldı
00:58:37.226  FP3_Close → OK                 ← hemen kapatıldı
00:58:56.580  ZNo=92 FişNo=3                 ← DEĞİŞMEDİ
```

### Sonuç ve çürüyen iddia

**Bu yolda mali numara tüketilmiyor.** W131'de "`finishId` başına en fazla bir
FNo kaybı" diye not ettiğim kalıntı, en azından bu yol için **yok**.

⚠️ Ve 2026-09-11 02:39'daki iptal için karşı tarafa **"mali numara TÜKETİLDİ,
kayda öyle geçirin"** demiştim. O iddia bir ölçüme değil, önceki bir nota
dayanıyordu ve bu ölçüm onu desteklemiyor. Geri çekiyorum.

### ⚠️ Ama sonuç HENÜZ KESİN DEĞİL — eksik kontrol

İki açıklama da verilere uyuyor:

1. Yoklama fişi gerçekten numara tüketmiyor.
2. `FisNo` sandığım şeyi saymıyor (ham bir alan; anlamı **kanıtlanmadı**).

Ayırmanın tek yolu **pozitif kontrol**: kesinlikle mali fiş kesen bir işlem
yapıp `FisNo`nun arttığını görmek. Artıyorsa alan fiş sayıyor ve yukarıdaki
sonuç sağlam; artmıyorsa alan başka bir şey ve ölçüm baştan geçersiz.

O kontrol bir **gerçek satış** gerektiriyor (para hareket eder), bu yüzden
kullanıcı kararına bırakıldı. **Pozitif kontrol yapılmadan bu sonuç sözleşmeye
yazılmamalı.**

### W143 — POZİTİF KONTROL GEÇTİ: `FisNo` = SONRAKİ fiş numarası

```
01:05:06  ZNo=92 FişNo=3            ← taban
01:06:47  Payment → fiş KAPANDI (4950/4950)
          kâğıttaki fiş numarası: 3          (kullanıcı okudu)
01:08:13  ZNo=92 FişNo=4            ← ARTTI
```

Tek mali fiş kesildi, alan **tam bir** arttı.

**İki sonuç birden kapandı:**

1. **`FisNo` gerçekten mali fiş sayıyor** — ve anlamı **SONRAKİ** numara, sonuncu
   değil. Kanıt üçlü: satıştan önce 3 okundu, basılan fişin numarası 3 çıktı,
   satıştan sonra 4 okundu. Sözleşmede "son kesilen fiş" diye kullanılırsa **bir
   fazla** raporlanır.

2. **Yoklama fişi mali numara TÜKETMİYOR** — dünkü ölçüm artık sağlam. Alan
   sayıyor olduğuna göre, `FP3_Start`→`FP3_Close` boyunca 3'te kalması gerçek bir
   olgu. W131'deki "`finishId` başına en fazla bir FNo kaybı" kalıntısı **yok**.

`EkuNo` üç ölçümde de 1 — EKÜ numarası, nadiren değişiyor.

### Hâlâ ölçülmemiş olan

**Kalemleri olan ve `VoidAll` ile iptal edilen** bir fiş numara tüketiyor mu —
bu AYRI bir yol ve ölçülmedi. 2026-09-11 02:39'daki iptal için karşı tarafa
"tüketildi" demiştim; o iddia geri çekildi ve yerine **"ölçülmedi"** yazıldı.
Artık güvenilir bir aletimiz var; istenirse tek turda ölçülebilir.
