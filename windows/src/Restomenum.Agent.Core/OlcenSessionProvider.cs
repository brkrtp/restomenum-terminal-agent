using System.Diagnostics;

namespace Restomenum.Agent.Core;

/// <summary>
/// <see cref="ISessionProvider"/> için <b>yalnız ÖLÇEN</b> sarmalayıcı (W51). Karar vermez,
/// yeniden denemez, hiçbir şeyi değiştirmez — sadece süreyi ve deneme sayısını kaydeder.
///
/// <para><b>Neden gerekti (ölçüm 2026-09-08 22:45:08):</b> bir satış uçtan uca <b>20,2 sn</b>
/// sürdü ama cihaz tarafı yalnız 7,8 sn'ydi. Aradaki <b>10,9 sn</b> cihaza gitmeden önce
/// harcandı ve günlükte o aralığa dair TEK SATIR yoktu — hangi aşamada olduğunu
/// söyleyemedik. Tek sayıyı yanlış okuma riskini 19:59'da (<c>sureMs=3004</c>) zaten
/// yaşamıştık; ölçmeden dokunulmaz.</para>
///
/// <para><b>Ölçüm mantıksal çağrıya özgü:</b> <see cref="AsyncLocal{T}"/> kullanılıyor, çünkü
/// tutar çekimi terminal kilidinden ÖNCE koşuyor ve iki satış aynı anda buradan geçebilir.
/// Paylaşılan bir alan iki ölçümü birbirine karıştırırdı.</para>
/// </summary>
public sealed class OlcenSessionProvider : ISessionProvider
{
    /// <param name="ToplamMs">Bu mantıksal çağrıda oturum alımına harcanan toplam süre.</param>
    /// <param name="Cagri">Kaç kez <see cref="AcquireAsync"/> çağrıldı.</param>
    /// <param name="HttpDeneme">
    /// Kaç HTTP oturum isteği gitti. <c>Cagri</c>'dan BÜYÜKSE içeride tekrar olmuş demektir
    /// (saat penceresi kaçtığında <see cref="HttpSessionProvider"/> bir kez daha deniyor).
    /// İç uygulama sayaç vermiyorsa <c>-1</c> — "ölçülmedi", "0" DEĞİL.
    /// </param>
    /// <param name="OmurSn">
    /// Sunucunun bildirdiği jeton ömrü (<c>expiresInSec</c>) — W53. <c>-1</c> = bu çağrıda taze
    /// jeton alınmadı (hepsi önbellekten), dolayısıyla ölçülmedi.
    ///
    /// <para><b>Neden gerekti:</b> ömrü hiçbir yere yazmıyorduk ve sözleşme metnine <b>900</b>
    /// diye geçmiştim — kaynaksız bir varsayımdı. Önbellek davranışından ~300 olduğu ÇIKARILDI
    /// (2026-09-09: 10,3 dk arayla iki taze alım, 2,0 dk arayla önbellek). Çıkarım yerine ölçüm.</para>
    ///
    /// <para>⚠️ Jetonun KENDİSİ değil, yalnız ÖMRÜ. Sır değildir.</para>
    /// </param>
    public sealed record Olcum(long ToplamMs, int Cagri, int HttpDeneme, int OmurSn);

    // ⚠️ `AsyncLocal` AŞAĞI akar, YUKARI DEĞİL: `await` edilen metot içinde `.Value` atamak
    // çağırana GÖRÜNMEZ (bağlam kopyası atılır). Bu yüzden kutu ÖNCEDEN kurulup İÇİ mutasyona
    // uğratılıyor — mutasyon yukarı görünür. İlk denemede alan ataması yapmıştım ve üç test
    // kırıldı; testler tam da bunu yakaladı.
    private sealed class Kutu
    {
        public long ToplamMs;
        public int Cagri;
        public int HttpDeneme = -1;      // -1 = ölçülmedi (0 ile KARIŞTIRILMAZ)
        public int OmurSn = -1;          // -1 = taze jeton alınmadı
    }

    private static readonly AsyncLocal<Kutu?> _kutu = new();

    /// <summary>Bu mantıksal çağrıdaki ölçüm; hiç oturum alınmadıysa <c>null</c>.</summary>
    public static Olcum? Son => _kutu.Value is { Cagri: > 0 } k
        ? new Olcum(k.ToplamMs, k.Cagri, k.HttpDeneme, k.OmurSn) : null;

    /// <summary>
    /// Yeni bir satışa girerken ölçüm kutusunu KURAR. Çağrılmazsa ölçüm yapılmaz (<see cref="Son"/>
    /// <c>null</c> kalır) — sessizce eski satışın sayılarını taşımaktansa hiç ölçmemek doğru.
    /// </summary>
    public static void Sifirla() => _kutu.Value = new Kutu();

    private readonly ISessionProvider _ic;

    public OlcenSessionProvider(ISessionProvider ic) => _ic = ic;

    /// <summary>Geçersiz kılmayı olduğu gibi geçirir (W52) — ölçüm sarmalayıcısı karar vermez.</summary>
    public void Invalidate() => _ic.Invalidate();

    public async Task<SessionToken> AcquireAsync(CancellationToken ct = default)
    {
        var kutu = _kutu.Value;
        var oncekiHttp = (_ic as HttpSessionProvider)?.HttpDenemeSayisi ?? -1;
        var kron = Stopwatch.StartNew();
        SessionToken? jeton = null;
        try
        {
            jeton = await _ic.AcquireAsync(ct);
            return jeton;
        }
        finally
        {
            kron.Stop();
            if (kutu is not null)
            {
                kutu.ToplamMs += kron.ElapsedMilliseconds;
                kutu.Cagri++;
                var yeniHttp = (_ic as HttpSessionProvider)?.HttpDenemeSayisi ?? -1;
                if (oncekiHttp >= 0 && yeniHttp >= 0)
                {
                    var fark = yeniHttp - oncekiHttp;
                    kutu.HttpDeneme = (kutu.HttpDeneme < 0 ? 0 : kutu.HttpDeneme) + fark;
                    // W53: kullanılan jetonun ömrü. Önbellekten gelse de aynı değerdir (sunucu
                    // onu bir kez bildirdi), o yüzden "yalnız taze alımda yaz" diye bir koşul
                    // KOYMUYORUZ — koysaydık önbellekli satışlarda alan boş kalır ve pencere
                    // tartışması yine kapanmazdı. `-1` tek bir şey demek: OKUNAMADI.
                    if (jeton is not null) kutu.OmurSn = jeton.ExpiresInSec;
                }
            }
        }
    }
}
