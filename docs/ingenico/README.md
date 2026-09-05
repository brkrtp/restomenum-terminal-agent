# Ingenico / Worldline GMP-3 üretici dokümanları

Bu klasör **üreticinin kendi dokümanlarını** taşır — bizim yazdığımız hiçbir şey yok. Sözleşme ve
tasarım kararları için tek kaynak `terminal-plugin-platformu.md`'dir; buradaki belgeler onun
**girdisidir**, yerine geçmez.

## Neden burada

Bugüne kadar GMP-3 davranışını **saha loglarından ve canlı terminal ölçümlerinden** çıkardık. O yol
işe yaradı ama pahalıydı: fiş tipini yanlış gönderdiğimizi ancak gerçek donanımda öğrendik, `2069`'un
kart-özel olduğunu ancak log karşılaştırmasıyla anladık, `VoidPayment` hâlâ ölçülmemiş. Bu belgeler
o boşlukları ölçüm yapmadan kapatabilir.

## Analiz sırası — değer/maliyet oranına göre

| # | Belge | Neden öncelikli |
|---|---|---|
| 1 | `GMP3OlumsuzDurumYönetimi_v1.2.pdf` | **Olumsuz durum yönetimi.** Kurtarma mantığımızın tamamı buradan doğrulanabilir: timeout, `RECV_BUSY`, yarım fiş, ters işlem |
| 2 | `Worldline_GMP3_test_cases_v2.0.xlsx` | **Üreticinin kendi test senaryoları.** Bizim kaçırdığımız dalları söyler; sertifikasyon öncesi kontrol listesi olur |
| 3 | `GMP3_ErrorHandling_EN_v2.docx` | Hata kodlarının **kanonik** anlamı. `2080`, `2069`, `2085/2086`, `0xF01C` gibi tahmin ettiğimiz ya da logdan çıkardığımız kodların doğrulaması |
| 4 | `GMP3_MaliFisteYemekCekiKullanimi_TR.docx` | **`KatkiPayiAmount`'ın kaynağı olabilir** — `IsFullyPaid` formülümüz buna dayanıyor ve canlıda tetikleyemedik |
| 5 | `GMP3_KDVCalculation_TR_v1.docx` + `Dinamik KDV_TR.pdf` | KDV'nin `deptIndex`'ten türetildiğini ölçmüştük; kuralın tamamı burada |
| 6 | `GMP XML TANIMLAMALARI.pdf` | IP/arayüz yapılandırması — `SetIpAddress` kararının dayanağı |
| 7 | `Fiş Limiti Aşılmaz 1.pdf` | `2067` — kalem hatası dalımızı canlıda bu kodla tetikledik |
| 8 | `GMP3_GeneralDataFlow_EN_v1.docx` | Genel akış; sıramızın üreticininkiyle örtüşüp örtüşmediği |
| 9 | `GMP3 Serbest giriş Kapama(Açma.pdf` | Serbest giriş / açma-kapama akışı |
| 10 | `GMP3 OPTIMIZASYONU_TR.pdf` | Süre bütçeleri; ölçtüğümüz 20–32 sn ile karşılaştırma |
| 11 | `GMP3-Workshop.pdf` | Geniş kapsamlı; sona bırakıldı (5,5 MB) |
| 12 | `GMP3+Entegrasyon+Projesi+Süreçleri+20250728.pdf` | Süreç/sertifikasyon adımları — **kesme planı** için ilgili olabilir |

## Analiz ederken

Her belge için **belgenin ne dediği** ile **bizim ne varsaydığımız** ayrı ayrı yazılmalı; çelişki
varsa belge kazanır ama **çelişkinin kendisi kayda geçmeli** — bugüne kadarki en pahalı hatalarımız
sessizce doğru sanılan varsayımlardı.

Özellikle şu üçü aranmalı:
1. **`VoidPayment`'ın gerçek imzası ve davranışı** — hâlâ tahmin, sertifikalı yüzeyde donmuş durumda.
2. **`KatkiPayiAmount`'ı ne tetikliyor** — `IsFullyPaid` formülü buna dayanıyor.
3. **Eşleşmenin tek slotlu olduğunun ve geçiş yolunun** üretici tarafından nasıl tarif edildiği.
