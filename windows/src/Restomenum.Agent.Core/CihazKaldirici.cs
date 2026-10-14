namespace Restomenum.Agent.Core;

/// <param name="Bildirim">Platforma henüz gönderilmemiş sonuçlar (<c>Outbox.Depth()</c>).</param>
/// <param name="Reddedilen">Platformun KALICI reddettiği bildirimler (<c>Outbox.RejectedCount()</c>) — defter eksik olabilir.</param>
/// <param name="Yarim">Sonucu kesinleşmemiş satışlar, YAŞ SINIRI OLMADAN (<c>CommandStore.Pending()</c>).</param>
public sealed record BekleyenIsler(int Bildirim, int Reddedilen, int Yarim);

/// <summary>
/// <b>RM-0389 — cihazı kaldırmanın dosya adımları.</b> Kilide ve platforma dokunmaz (onlar çağıranın
/// işi); yolları ve saati dışarıdan alır, bu yüzden her durumu testte ölçülebilir.
///
/// <para><b>Bekleyen iş:</b> sayım bir FONKSİYONDUR ve çağıran cihaz kilidini aldıktan SONRA
/// çağrılır — sayı önceden okunsaydı sayımla kilit arasına bir sonuç düşebilirdi. Kimlik sökülürse
/// bunların hiçbiri platforma bildirilemez (oturum boş kimlikle açılamaz); proje kuralı uçuştaki işi
/// asla kaybetmemektir. Yaş sınırı YOK: açılış kurtarmasının 24 saatten eski kayıtları yoklamaması
/// onların çözüldüğü anlamına gelmez.</para>
///
/// <para><b>Sıra ve kurtarma:</b> önce eklenti dizesi silinir, SONRA kimlik taşınır. Her adım
/// tekrarlanabilir (dosya yoksa atlanır), dolayısıyla yarım kalan bir kaldırma ayrı bir durum
/// kaydına gerek olmadan YENİDEN DENENEREK tamamlanır: eklenti silinip kimlik taşınamadıysa cihaz
/// hâlâ kayıtlı görünür, kart sayfada kalır, ikinci deneme yalnız kimliği taşır.</para>
/// </summary>
public sealed class CihazKaldirici(string connectorYolu, string? eklentiYolu, Func<DateTime> saat)
{
    public KurulumSonucu Uygula(Func<BekleyenIsler> say)
    {
        var b = say();
        if (b.Bildirim > 0)
            return KurulumSonucu.Hata($"Platforma henüz bildirilmemiş {b.Bildirim} satış sonucu var. Ajan bunları " +
                                      "göndermeyi sürdürüyor; internet bağlantısını kontrol edin. Gönderilince kaldırma yapılabilir.");
        if (b.Reddedilen > 0)
            return KurulumSonucu.Hata($"Platformun kalıcı olarak reddettiği {b.Reddedilen} bildirim var — defter platformda " +
                                      "eksik olabilir. Bunlar kendiliğinden gönderilmez: önce operatörün yeniden göndermesi ya da " +
                                      "mutabakatla kapatması gerekir.");
        if (b.Yarim > 0)
            return KurulumSonucu.Hata($"Sonucu kesinleşmemiş {b.Yarim} satış kaydı var. 24 saatten yeni olanları ajan " +
                                      "terminale soruyor; daha eskileri kendiliğinden çözülmez ve operatör mutabakatı gerekir.");

        var eklentiVar = eklentiYolu is not null && File.Exists(eklentiYolu);
        var kimlikVar = File.Exists(connectorYolu);
        if (!eklentiVar && !kimlikVar)
            return KurulumSonucu.Tamam("Kaldırılacak bir kurulum yok — cihaz zaten kurulmamış durumda.");

        if (eklentiVar)
        {
            try { File.Delete(eklentiYolu!); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return KurulumSonucu.Hata("Eklenti bağlantı dosyası silinemedi; hiçbir şey değişmedi. " +
                                          "Dosya başka bir program tarafından açık olabilir ya da ajan yetkisiz çalışıyor " +
                                          "(servis olarak çalıştırın). Sonra tekrar deneyin.");
            }
        }

        if (kimlikVar)
        {
            try { File.Move(connectorYolu, connectorYolu + $".kaldirildi-{saat():yyyyMMdd-HHmmss}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return KurulumSonucu.Hata("Eklenti bağlantısı silindi ama platform kaydı bu bilgisayardan kaldırılamadı. " +
                                          "Tekrar deneyin — işlem kaldığı yerden devam eder.");
            }
        }

        return KurulumSonucu.Tamam(
            "Cihaz kaldırıldı. Yeniden kurmak için eklentiden yeni eşleştirme kodu ve kurulum dizesi alın.",
            yenidenBaslat: true);
    }
}
