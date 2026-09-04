# Uygunluk vektörleri — iki agent, tek davranış

TR pazarı **Windows**, DE pazarı **Android** agent'ı kullanır (§12.1). Yani §12.2'nin dokuz
değişmezi **iki kez** uygulanacak: bir kez Kotlin'de, bir kez Windows tarafında.

**Risk:** ayrışan bir dedupe mantığının bedeli **çift tahsilattır**. Ve iki agent'ı aynı anda kimse
test etmeyeceği için sapma aylarca görünmez kalabilir — bu depoda bu hata sınıfından beş örnek
yaşandı (aynı kuralın iki kopyası her seferinde ayrıştı ya da ayrışmaya hazırdı).

**Önlem:** her iki uygulama da bu klasördeki vektörleri okur ve **aynı** sonucu üretmek zorundadır.
Sapma sessizce değil, testte patlar. Aynı desen `gcloud/payment-transport/__tests__/sessionToken.diff.test.js`
içinde platform↔gateway token doğrulaması için zaten kullanılıyor ve orada bir sapmayı yakaladı.

## Dosyalar

| Dosya | Ne doğrular |
|-------|-------------|
| `signing.json` | Session imzasının **kanonik dizesi** — uzunluk önekli kodlama |
| `state-transitions.json` | Komut durum makinesi: hangi geçiş yasal, hangisi değil |
| `backoff.json` | Yeniden bağlanma basamakları ve jitter bandı |

## Kullanım

Her agent kendi test çerçevesinde bu JSON'ları okur, her satırı çalıştırır ve `expected` ile
karşılaştırır. **Vektörü koda gömmeyin** — gömülen kopya, dosya değiştiğinde güncellenmez.

## Vektör eklerken

Yeni bir değişmez eklerken **önce buraya vektör yazın**, sonra iki tarafı da geçirin. Yalnız bir
tarafta test edilen kural, diğer tarafta yokmuş gibidir.
