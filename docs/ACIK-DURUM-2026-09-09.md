# AÇIK DURUM — 2026-09-09 18:30 (devralan okusun)

Bu belge **çözülmemiş bir saha olayını** ve **commit edilmemiş bir yamayı** anlatır.
Kapanınca silinmeli.

## 1. ÇÖZÜLMEMİŞ OLAY — fiş 88/11, gerçek para, akıbeti bilinmiyor

**Cihazda AÇIK fiş:** `ZNo 88 / FNo 11`, `tkt_ec5f7893979b4b68b6c7520e6a51bd8a`,
oturum `cf6ee42e-decd-4cb8-bc03-20f2816b149f`.
Toplam **2331**, tahsil **941** (kart, GARANTİ BBVA), kalan **1390**.

**Ne oldu:** 18:06'da kart bacağı onaylandı (990 istendi, banka indirimi ile **941** tahsil
edildi). 18:11'de kasadan fiş iptali geldi; `VoidAll` **2069** ("ödeme var") döndü, ters işlem
döngüsü ilk kez çalıştı ve **`VoidPayment(0)` 10 114 ms'de zaman aşımına uğradı**
(`0xF003`). Fiş kapatılmadı.

**BİLİNMEYEN:** ters işlemin **bankaya ulaşıp ulaşmadığı**. Kullanıcının cihaz ekranı
okuması (23,31 / kalan 13,90) ters işlemin **fişe işlemediğini** gösteriyor, ama bankadaki
provizyonun durumu ayrı bir kanaldır ve **doğrulanmadı**.

**YAPILMAMASI GEREKENLER:**
- ❌ Fiş iptalini tekrar deneme — ilk ters işlem bankaya ulaşmış olabilir → **çift iade**.
- ❌ `--replay-ticket-cancel` — outbox'taki gövde çelişkili, yine 400 alır (aşağıya bak).
- ❌ Fişi **cihazın kendi menüsünden** iptal etme — bkz. W91, defteri bozar.
- ❌ Aynı masada yeni satış açma.

**KALANI NAKİT ÖDEYİP KAPATMA (b) DENENDİ Mİ:** hayır, **çalışmaz**. `DevamDogrula`
(`GmpTerminalTransport.Fis.cs:44`) cihazın fiş toplamını (2331) platformun
`SaleTotalAmountMinor`'i (2380) ile birebir kıyaslıyor → **`TICKET_SALE_MISMATCH`**.
Çıkış: platform devam bacağını **2331** ile kurarsa çalışır (ret gövdesi `deviceTicketTotalMinor`
ile bu sayıyı zaten taşıyor).

**Defter:** `closed_tickets 39`, `open_ticket 1`, **`outbox 1` ve DAMGALI** (400
`plugin.payment.invalidResult`). Damgayı kaldıran/kaydı kapatan bir yol **YOK** (W89).

## 2. COMMIT EDİLMEMİŞ YAMA — W87

`src/Restomenum.Agent.Gmp/GmpWrapper.cs` çalışma ağacında **değiştirilmiş, commit YOK**:
`TimeoutVoid = TimeoutCard` (90 sn) eklendi; `FP3_VoidAll` ve `FP3_VoidPayment` artık onu
kullanıyor (eskiden `TimeoutDefault` 10 sn). Derleme temiz, 496/496 test yeşil.
**Kullanıcı onayı bekliyor.** Onay gelmezse `git checkout -- windows/src/Restomenum.Agent.Gmp/GmpWrapper.cs`.
⚠️ Yama **çift iade riskini azaltmaz**, yalnız ikinci denemenin tamamlanma ihtimalini artırır.
⚠️ Anlamlı birim testi **yok ve yazılamaz**: değer P/Invoke katmanında, test projesi orayı
görmüyor. Gerçek kanıt sahada bir ters işlemin 10 sn'yi aşıp tamamlanmasıdır. (W87b: süreyi
Core'a politika olarak taşımak — ayrı iş.)

## 3. AÇIK KUSURLAR (bugün ölçüldü, kod yazılmadı)

| no | özet | çapa |
|---|---|---|
| **P138/W79** | Banka kartı fişin mali toplamını ~%5 düşürüyor; ajan bunu bildirmiyor, defter şişik kalıyor | `GmpTerminalTransport.cs:338, 248, 245` |
| **P147/W90** | İndirimli kart bacağından sonra **tam ödenmemiş fiş TAMAMLANAMIYOR** (`TICKET_SALE_MISMATCH`). Öneri: `fis.Total + fis.Discount == satisToplam` da kabul edilsin | `GmpTerminalTransport.Fis.cs:44` |
| **W88** | Ters işlem başarısızken gövde `voidedAmountMinor`ı **niyet** olarak taşıyor → platform 400 | `GmpTerminalTransport.Iptal.cs:173` |
| **W89** | Damgalı outbox kaydını kapatma yolu yok; kalıcı sahte "defter boşluğu" | `Outbox.cs` (genel arayüzde yok) |
| **W91** | Cihaz menüsünden iptal → bağ bayat kalır, sonraki satış **eski `ticket_id`'yi devralır** → hayalet ödeme satırı | `CommandStore.cs:425-440` |
| **W84** | `[iptal] fiş sonucu` günlüğünde `ticketId`/`terminalId` yok, ama `--replay-ticket-cancel` onları istiyor ve "o satırda" diyor | `LocalSaleHandler.cs:389`, `WindowsReplayTicketCancel.cs:26` |
| **W86** | Ödeme satırı hiç oluşmadığında `reason` `??` varsayılanıyla `NOT_LANDED` ("başlamadı") diye kesin konuşuyor; kanıt yalnız mali taraf | `AgentOrchestrator.cs:256` |
| **W80** | `süre dökümü`nde `bekleMs` negatif çıkıyor (−2, −10, −20, −69, −104) | — |
