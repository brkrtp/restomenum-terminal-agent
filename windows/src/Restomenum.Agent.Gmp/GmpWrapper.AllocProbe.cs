using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Restomenum.Agent.Gmp.Interop;

namespace Restomenum.Agent.Gmp;

/// <summary>
/// <b>W42b çökme izolasyonu</b> — 2026-09-08 20:28:30'da `--probe-multi --onayla` süreci
/// <c>Json_prepare_ItemSale</c> çağrısında <c>0xC0000374 STATUS_HEAP_CORRUPTION</c> ile öldü.
/// Cihaza tek bayt gitmemişti (GMP izinde <c>SendToOkc</c> yalnız Echo için vardı).
///
/// <para><b>Bu sınıf CİHAZA HİÇ DOKUNMAZ.</b> Ne <c>FP3_Echo</c> ne başka bir <c>FP3_*</c>
/// çağrılır. Yalnız yerel tampon kurucuları ve bellek kontrolü.</para>
///
/// <para><b>Yöntem — tahmin değil ölçüm:</b> tamponlar <see cref="Marshal.AllocHGlobal(int)"/> ile
/// KENDİMİZ ayrılıyor ve önüne/arkasına <b>nöbetçi</b> (guard) bayt konuyor. Çağrıdan sonra
/// nöbetçiler bozulmuşsa native taraf verdiğimiz sınırın DIŞINA yazmış demektir — ve hangi
/// tamponun dışına yazdığını da söyler. Satıcının <c>byte[]</c> sarmalayıcısı bu soruyu
/// soramıyordu: marshalling ara kopya yaptığı için taşma görünmez oluyordu.</para>
/// </summary>
public static class GmpAllocProbe
{
    // Kendi P/Invoke'umuz: satıcının `byte[]` imzası yerine HAM İŞARETÇİ. Böylece belleği biz
    // ayırıyor, sınırlarını biz koyuyor ve taşmayı biz görebiliyoruz.
    [DllImport("GmpSmartDLL.dll", EntryPoint = "Json_prepare_ItemSale",
        CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Json_prepare_ItemSale_Ptr(IntPtr buffer, int maxSize,
        IntPtr jsonItem, IntPtr jsonItemOut, int jsonItemOutLen);

    private const byte Nobetci = 0xA5;
    private const int NobetciBoyu = 256;

    /// <summary>Nöbetçili native tampon. <see cref="Veri"/> çağrılana verilen işaretçi.</summary>
    private sealed class Korumali : IDisposable
    {
        private readonly IntPtr _kok;
        public readonly IntPtr Veri;
        public readonly int Boy;

        public Korumali(int boy)
        {
            Boy = boy;
            _kok = Marshal.AllocHGlobal(boy + 2 * NobetciBoyu);
            for (var i = 0; i < boy + 2 * NobetciBoyu; i++) Marshal.WriteByte(_kok, i, Nobetci);
            Veri = _kok + NobetciBoyu;
            for (var i = 0; i < boy; i++) Marshal.WriteByte(Veri, i, 0);
        }

        /// <summary>Bozulan ilk nöbetçinin göreli konumu; <c>null</c> = taşma yok.</summary>
        public string? Bozuk()
        {
            for (var i = 0; i < NobetciBoyu; i++)
                if (Marshal.ReadByte(_kok, i) != Nobetci) return $"ÖNDE -{NobetciBoyu - i} bayt";
            for (var i = 0; i < NobetciBoyu; i++)
                if (Marshal.ReadByte(_kok, NobetciBoyu + Boy + i) != Nobetci) return $"ARKADA +{i} bayt";
            return null;
        }

        public void Dispose() => Marshal.FreeHGlobal(_kok);
    }

    private static IntPtr AsciiZ(string s, out int uzunluk)
    {
        var b = Encoding.ASCII.GetBytes(s);
        uzunluk = b.Length + 1;
        var p = Marshal.AllocHGlobal(uzunluk);
        Marshal.Copy(b, 0, p, b.Length);
        Marshal.WriteByte(p, b.Length, 0);
        return p;
    }

    /// <summary>
    /// Tek senaryo: <paramref name="tekrar"/> kez <c>Json_prepare_ItemSale</c> çağırır ve HER
    /// çağrıdan sonra iki tamponun nöbetçilerini kontrol eder. İlk taşmada durur.
    /// </summary>
    /// <param name="cikisTamponu">
    /// <c>szJsonItem_Out</c> için ayrılan boyut. Satıcı sarmalayıcısı burada HER ÇAĞRIDA 200.000
    /// bayt ayırıyor (GmpInterop.cs:2969); küçük bir değerle native tarafın gerçekten ne kadar
    /// yazdığını ölçüyoruz.
    /// </param>
    public static string Senaryo(string ad, int tekrar, int paketBoyu, int cikisTamponu,
        Action<string> yaz)
    {
        var item = new ST_ITEM
        {
            type = 1, subType = 0, deptIndex = 10, taxRate = 0, unitType = 0,
            amount = 100, currency = 949, count = 1, flag = 0, countPrecition = 0,
            pluPriceIndex = 0, name = "PROVA", barcode = "",
        };
        var json = JsonConvert.SerializeObject(item);
        var pJson = AsciiZ(json, out var jsonLen);

        try
        {
            for (var i = 1; i <= tekrar; i++)
            {
                using var paket = new Korumali(paketBoyu);
                using var cikis = new Korumali(cikisTamponu);

                var r = Json_prepare_ItemSale_Ptr(paket.Veri, paketBoyu, pJson, cikis.Veri, cikisTamponu);

                var bp = paket.Bozuk();
                var bc = cikis.Bozuk();
                if (bp is not null || bc is not null)
                {
                    var m = $"{ad}: {i}. çağrıda TAŞMA — paket:{bp ?? "temiz"} · çıkış:{bc ?? "temiz"} " +
                            $"(dönüş={r}, girişJson={jsonLen} B, çıkışTamponu={cikisTamponu} B)";
                    yaz(m);
                    return m;
                }

                // Çıkış tamponunun gerçekten kaç baytı dolduruldu? (Sınır ölçümü.)
                if (i == 1)
                {
                    var dolu = 0;
                    for (var k = cikisTamponu - 1; k >= 0; k--)
                        if (Marshal.ReadByte(cikis.Veri, k) != 0) { dolu = k + 1; break; }
                    yaz($"{ad}: dönüş={r} · çıkış tamponuna yazılan={dolu} B (ayrılan {cikisTamponu} B)");
                }
            }
            var ok = $"{ad}: {tekrar} çağrı, TAŞMA YOK (paket {paketBoyu} B, çıkış {cikisTamponu} B)";
            yaz(ok);
            return ok;
        }
        finally { Marshal.FreeHGlobal(pJson); }
    }

    [DllImport("GmpSmartDLL.dll", EntryPoint = "prepare_Start",
        CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int prepare_Start_Ptr(IntPtr buffer, int maxSize,
        IntPtr uid, int uidLen, IntPtr sign, int signLen, IntPtr userData, int userDataLen);

    /// <summary>
    /// <c>prepare_Start</c>'ı <b>imza işaretçisi NULL</b> ve <b>gerçek tampon</b> hâlleriyle ayrı
    /// ayrı dener (W42b çökme hipotezi 3).
    ///
    /// <para><b>Şüphe:</b> çöken kodda imza için <c>null</c> + uzunluk 0 geçilmişti
    /// (<c>GmpWrapper.Start</c> de <c>FP3_Start</c>'a öyle geçiyor). <c>FP3_Start</c> bunu yıllardır
    /// sorunsuz kabul ediyor ama <c>prepare_Start</c> AYRI bir native fonksiyon — işaretçiyi
    /// koşulsuz dereference ediyorsa bozulma buradan gelir.</para>
    /// </summary>
    public static string StartSenaryosu(string ad, bool imzaNull, int tekrar, Action<string> yaz)
    {
        const int PaketBoyu = 24564;
        var pUid = Marshal.AllocHGlobal(24);
        for (var i = 0; i < 24; i++) Marshal.WriteByte(pUid, i, 0);
        var pUser = AsciiZ("testdata", out _);
        var pSign = imzaNull ? IntPtr.Zero : Marshal.AllocHGlobal(64);
        if (!imzaNull) for (var i = 0; i < 64; i++) Marshal.WriteByte(pSign, i, 0);

        try
        {
            for (var i = 1; i <= tekrar; i++)
            {
                using var paket = new Korumali(PaketBoyu);
                var r = prepare_Start_Ptr(paket.Veri, PaketBoyu, pUid, 24,
                    pSign, imzaNull ? 0 : 64, pUser, 8);
                var b = paket.Bozuk();
                if (b is not null)
                {
                    var m = $"{ad}: {i}. çağrıda TAŞMA — paket:{b} (dönüş={r})";
                    yaz(m);
                    return m;
                }
                if (i == 1) yaz($"{ad}: dönüş={r} (imza={(imzaNull ? "NULL" : "64 B tampon")})");
            }
            var ok = $"{ad}: {tekrar} çağrı, TAŞMA YOK (imza={(imzaNull ? "NULL" : "64 B tampon")})";
            yaz(ok);
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(pUid);
            Marshal.FreeHGlobal(pUser);
            if (!imzaNull) Marshal.FreeHGlobal(pSign);
        }
    }

    /// <summary>
    /// Arayüz listesini alır — <b>yerel</b> bir çağrıdır, cihaza gitmez. Kanıt: GMP izinde
    /// <c>FP3_GetInterfaceHandleList</c> giriş/çıkışı aynı milisaniyede ve arkasından
    /// <c>SendToOkc</c> YOK (20:28:30.197).
    /// </summary>
    public static uint ArayuzAl()
    {
        var list = new uint[20];
        uint count = GMPSmartDLL.FP3_GetInterfaceHandleList(list, (uint)list.Length);
        return count > 0 ? list[0] : 0;
    }
}
