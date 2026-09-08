using Restomenum.Agent.Gmp;

namespace Restomenum.Agent.Host.Windows;

/// <summary>
/// <c>--probe-alloc</c> — W42b çökmesinin izolasyonu. <b>CİHAZA HİÇ DOKUNMAZ</b> ve
/// <b>DI/host kurmaz</b>: tam kısa devre, yönetici bile gerekmez.
///
/// <para>İki hipotez AYRI AYRI ölçülür (tahmin yok):</para>
/// <list type="number">
/// <item><b>H1</b> — bozulma Faz A'da da oluyordu, süreç erken bittiği için görülmedi.</item>
/// <item><b>H2</b> — bozulma yalnız arayüz ALINDIKTAN sonra oluyor.</item>
/// </list>
/// </summary>
public static class WindowsAllocProbe
{
    public static bool Run()
    {
        void Yaz(string m) => Console.WriteLine(m);

        Yaz("── W42b çökme izolasyonu — CİHAZA TEK ÇAĞRI YOK ──");
        Yaz("");

        // ── S1: tek çağrı, satıcının kullandığı 200 KB çıkış tamponu ────────────
        Yaz("[S1] tek çağrı · çıkış tamponu 200000 B (satıcı sarmalayıcısının kullandığı boyut)");
        var s1 = GmpAllocProbe.Senaryo("S1", tekrar: 1, paketBoyu: 24564, cikisTamponu: 200_000, Yaz);
        Yaz("");

        // ── S2: KÜÇÜK çıkış tamponu — native taraf sınırı aşıyor mu? ────────────
        // Satıcı her çağrıda 200 KB ayırdığı için taşma GÖRÜNMÜYOR olabilir; küçük tamponla
        // native tarafın gerçekten ne kadar yazdığı ölçülür.
        Yaz("[S2] tek çağrı · çıkış tamponu 4096 B (taşmayı görünür kılmak için KÜÇÜK)");
        var s2 = GmpAllocProbe.Senaryo("S2", tekrar: 1, paketBoyu: 24564, cikisTamponu: 4096, Yaz);
        Yaz("");

        // ── S3 (H1): Faz A'nın 335 çağrısı ────────────────────────────────────
        Yaz("[S3/H1] 400 çağrı ardı ardına · arayüz ALINMADAN (Faz A ile aynı koşullar)");
        var s3 = GmpAllocProbe.Senaryo("S3", tekrar: 400, paketBoyu: 24564, cikisTamponu: 200_000, Yaz);
        Yaz("");

        // ── S4 (H2): arayüz ALINDIKTAN sonra ──────────────────────────────────
        // `FP3_GetInterfaceHandleList` YEREL bir çağrıdır (GMP izinde arkasından SendToOkc yok).
        Yaz("[S4/H2] arayüz alındıktan SONRA 400 çağrı (Echo YOK, cihaza gidilmez)");
        var h = GmpAllocProbe.ArayuzAl();
        Yaz($"        arayüz handle = 0x{h:X8}");
        var s4 = GmpAllocProbe.Senaryo("S4", tekrar: 400, paketBoyu: 24564, cikisTamponu: 200_000, Yaz);
        Yaz("");

        // ── S5/S6 (H3): prepare_Start'a NULL imza işaretçisi ────────────────────
        // Çöken kodda imza için `null` + uzunluk 0 geçilmişti. `FP3_Start` bunu yıllardır kabul
        // ediyor ama `prepare_Start` AYRI bir native fonksiyon.
        Yaz("[S5/H3] prepare_Start · imza işaretçisi NULL · 200 çağrı");
        var s5 = GmpAllocProbe.StartSenaryosu("S5", imzaNull: true, tekrar: 200, Yaz);
        Yaz("");
        Yaz("[S6/H3] prepare_Start · imza 64 B GERÇEK tampon · 200 çağrı");
        var s6 = GmpAllocProbe.StartSenaryosu("S6", imzaNull: false, tekrar: 200, Yaz);
        Yaz("");

        Yaz("── ÖZET ──");
        foreach (var r in new[] { s1, s2, s3, s4, s5, s6 }) Yaz("  " + r);
        var tasma = new[] { s1, s2, s3, s4, s5, s6 }.Any(r => r.Contains("TAŞMA —"));
        Yaz(tasma
            ? "SONUÇ: sınır AŞILIYOR — çökmenin kaynağı bu; ayrıntı yukarıda."
            : "SONUÇ: bu senaryolarda taşma YOK. Çökme başka bir yolda (bkz. rapor).");
        return !tasma;
    }
}
