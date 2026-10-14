namespace Restomenum.Agent.Core;

/// <summary>Kurulum isteği, HTTP'den bağımsız. <paramref name="AltYol"/> <c>/setup/</c>'tan sonraki kısım ("" = sayfa).</summary>
public sealed record KurulumIstegi(string Yontem, string AltYol, bool YerelIstemci, string? Host, string? Origin,
    string? IcerikTuru, string? Anahtar, string Govde);

/// <param name="YenidenBaslat">Yanıt yazıldıktan sonra ajan yeniden başlatılmalı (yanıtın ulaşması koşul DEĞİL).</param>
public sealed record KurulumYaniti(int Durum, string Govde, bool Html = false, bool YenidenBaslat = false, string? Log = null);

/// <summary>
/// <b>RM-0389 — kurulum isteklerinin yönlendirilmesi.</b> AgentWorker'dan ayrıldı: HTTP ayrıntısı
/// orada, karar burada — böylece her yol testte ölçülür.
///
/// <para><b>Sıra:</b> (1) yöntem — yalnız GET (sayfa/durum/tarama) ve POST (işlemler); diğer her şey
/// 405 ve kapıya dokunmaz. (2) güvenlik (<see cref="KurulumGuvenlik"/>). (3) gövde DOĞRULAMASI —
/// kapı kapanmadan önce; geçersiz girdi bakım başlatmaz. (4) yeniden başlatma gerektiren işlemlerde
/// önce SCM engeli, sonra bakım kapısı. (5) işlem. Bakım sahipliği <c>using</c> ile tutulur: hata,
/// istisna ya da erken dönüşte kapı AÇILIR; yalnız değişiklik başarıyla yapılıp yeniden başlatma
/// bekleniyorsa ya da işlem istisnayla yarıda kaldıysa (değişikliğin yapılıp yapılmadığı belirsiz →
/// temiz açılış şart) kapalı tutulur.</para>
/// </summary>
public sealed class KurulumIstekIsleyici(
    IKurulumIslemleri islem, BakimKapisi bakim, string anahtar, int port,
    Func<BekleyenIsler> bekleyenIsler, TimeSpan bakimBeklemesi)
{
    private const string NotFound = "{\"error\":\"not found\"}";

    public async Task<KurulumYaniti> IsleAsync(KurulumIstegi i, CancellationToken ct)
    {
        var get = KurulumGuvenlik.GetMi(i.Yontem);
        if (!get && !KurulumGuvenlik.PostMu(i.Yontem))
            return new(405, KurulumIstek.Json(KurulumSonucu.Hata($"yöntem desteklenmiyor ({i.Yontem})")),
                Log: $"yöntem reddedildi: {i.Yontem} /setup/{i.AltYol}");

        var red = KurulumGuvenlik.Denetle(i.Yontem, i.YerelIstemci, i.Host, i.Origin, i.IcerikTuru, i.Anahtar, anahtar, port);
        if (red is not null)
            return new(403, KurulumIstek.Json(KurulumSonucu.Hata(red)), Log: $"istek reddedildi: {red} ({i.Yontem} /setup/{i.AltYol})");

        if (get)
        {
            switch (i.AltYol)
            {
                case "": return new(200, KurulumSayfasi.Html(islem.Durum(), anahtar), Html: true);
                case "status": return new(200, KurulumIstek.Json(islem.Durum()));
                case "scan":
                {
                    // W152: ağ taraması cihaza GMP çağrısı yapmaz (bağlan-kapat), o yüzden GET güvenli.
                    var adaylar = await islem.TerminalleriTaraAsync(ct);
                    var ag = adaylar.Count > 0 ? adaylar[0].Ip[..(adaylar[0].Ip.LastIndexOf('.') + 1)] + "0/24" : null;
                    return new(200, KurulumIstek.JsonTarama(adaylar, ag));
                }
                case "enroll" or "terminal" or "pair" or "plugin" or "remove":
                    return new(405, KurulumIstek.Json(KurulumSonucu.Hata("bu işlem yalnız POST ile yapılır")));
                default: return new(404, NotFound);
            }
        }

        // ── POST: önce doğrula (kapı açıkken), sonra işle ─────────────────────────────────
        Func<Task<KurulumSonucu>> yap;
        bool bakimli;
        switch (i.AltYol)
        {
            case "enroll":
            {
                var kod = KurulumIstek.Kod(i.Govde, out var neden);
                if (kod is null) return Tamam(KurulumSonucu.Hata(neden ?? "kod okunamadı"));
                yap = () => islem.KaydolAsync(kod, ct); bakimli = false; break;
            }
            case "pair":
                yap = () => Task.FromResult(islem.Esles()); bakimli = false; break;
            case "terminal":
            {
                var t = KurulumIstek.Terminal(i.Govde, out var neden);
                if (t is null) return Tamam(KurulumSonucu.Hata(neden ?? "adres okunamadı"));
                yap = () => Task.FromResult(islem.TerminalAyarla(t.Value.Ip, t.Value.Port)); bakimli = true; break;
            }
            case "plugin":
            {
                var ayar = KurulumIstek.Eklenti(i.Govde, out var neden);
                if (ayar is null) return Tamam(KurulumSonucu.Hata(neden ?? "kurulum dizesi okunamadı"));
                yap = () => islem.EklentiAyarlaAsync(ayar, ct); bakimli = true; break;
            }
            case "remove":
            {
                if (!KurulumIstek.KaldirmaOnayi(i.Govde, out var neden))
                    return Tamam(KurulumSonucu.Hata(neden ?? "onay okunamadı"));
                yap = () => Task.FromResult(islem.Kaldir(bekleyenIsler)); bakimli = true; break;
            }
            default:
                return new(404, NotFound);
        }

        if (!bakimli)
            return Tamam(await yap(), $"{i.AltYol} denemesi");

        var engel = islem.YenidenBaslatmaEngeli();
        if (engel is not null)
            return Tamam(KurulumSonucu.Hata(engel), $"KRİTİK: {i.AltYol} reddedildi — ajan yeniden başlatılamaz: {engel}");

        using var sahiplik = await bakim.BakimaAlAsync(bakimBeklemesi, ct);
        if (sahiplik is null)
            return Tamam(KurulumSonucu.Hata(
                "Şu an bir satış ya da arka plan işi sürüyor veya başka bir kurulum işlemi yapılıyor. Biraz sonra tekrar deneyin."));

        KurulumSonucu sonuc;
        try { sonuc = await yap(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Değişiklik yapıldı mı bilinmiyor: süreç satış almasın, temiz açılışla durum diskten okunsun.
            sahiplik.KapaliTut();
            return new(200, KurulumIstek.Json(KurulumSonucu.Hata(
                "İşlem beklenmedik bir hatayla yarıda kaldı. Güvenlik için ajan yeniden başlatılıyor; " +
                "açıldıktan sonra sayfayı yenileyip durumu kontrol edin.")),
                YenidenBaslat: true, Log: $"{i.AltYol} istisnayla yarıda kaldı: {e.GetType().Name} — güvenli yeniden başlatma");
        }

        var yenidenBaslat = sonuc.Basarili && sonuc.YenidenBaslatmaGerekli;
        if (yenidenBaslat) sahiplik.KapaliTut();
        return new(200, KurulumIstek.Json(sonuc), YenidenBaslat: yenidenBaslat,
            Log: $"{i.AltYol} denemesi: {(sonuc.Basarili ? "başarılı" : "başarısız")} — {sonuc.Mesaj}");
    }

    private static KurulumYaniti Tamam(KurulumSonucu s, string? log = null) => new(200, KurulumIstek.Json(s), Log: log);
}
