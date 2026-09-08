using System.Diagnostics;
using Restomenum.Agent.Gmp.Interop;

namespace Restomenum.Agent.Gmp;

/// <summary>Toplu komut provasının ölçümleri (W42b adım 1). Gövde/JSON TAŞIMAZ — yalnız sayılar.</summary>
public sealed record BatchProbeOlcum(
    IReadOnlyList<(int Kalem, int Donus, int Dolu)> TamponAdimlari,
    string PrepareDonusAnlami,
    int OnekBayt,
    double KalemBasinaBayt,
    int TamponaSiganKalem,
    bool CihazaGidildi,
    uint MultiRc,
    long SureMs,
    int GonderilenBayt,
    ushort IndexOfReturnCodes,
    IReadOnlyList<(uint SubCommand, uint RetCode, uint Tag, ushort Index, ushort Len)> DonusKodlari,
    int FisKalemSayisi,
    long FisToplamMinor,
    int FisOdemeSayisi,
    string Temizlik);

public sealed partial class GmpWrapper
{
    /// <summary>
    /// <b>W42b adım 1 provası</b> — <c>prepare_*</c> + <c>FP3_MultipleCommand</c> cihazda çalışıyor mu?
    ///
    /// <para><b>Üretim yoluna DOKUNMAZ.</b> Bu metot <c>ITicketSnapshotStore</c> ALMIYOR; hiçbir
    /// tabloya yazamaz, <c>open_ticket</c> bağı yapısal olarak kirlenemez. <c>FP3_Payment</c>
    /// ÇAĞRILMAZ, dolayısıyla fişte para olmaz ve temizleme güvenlidir.</para>
    ///
    /// <para><b>Faz A cihaza hiç dokunmaz:</b> <c>prepare_*</c> yerel tampon kurucularıdır (GMP
    /// izinde giriş/çıkış aynı milisaniyede; asıl I/O ayrı <c>SendToOkc</c> satırıdır). Kalem
    /// başına bayt bu yüzden SIFIR riskle ölçülebiliyor.</para>
    /// </summary>
    /// <param name="cihazaGit"><c>false</c> ise YALNIZ Faz A koşar — tek bir cihaz çağrısı bile yok.</param>
    /// <param name="deptIndex">Prova kaleminin departmanı. Cihazda TANIMLI olmalı (10 = %10).</param>
    public BatchProbeOlcum ProbeMulti(bool cihazaGit, int deptIndex = 10, Action<string>? log = null)
    {
        void Yaz(string m) => log?.Invoke(m);

        // Tampon 24564: GMP izinde DLL'in tek-komut yolunda KULLANDIĞI değer
        // ("prepare_OptionFlags(MaxSize: 24564)"). Sahada gözlenen tek değer bu; daha büyüğünü
        // cihazın kabul edip etmediği ÖLÇÜLMEDİ, o yüzden uydurmuyoruz.
        const int TamponBoyu = 24564;
        var adimlar = new List<(int, int, int)>();
        var tampon = new byte[TamponBoyu];
        var uniqueId = new byte[24];
        var userData = new byte[] { 0x74, 0x65, 0x73, 0x74, 0x64, 0x61, 0x74, 0x61 };   // "testdata"

        var rStart = GMPSmartDLL.prepare_Start(tampon, TamponBoyu, uniqueId, uniqueId.Length,
            null!, 0, userData, userData.Length);
        adimlar.Add((-3, rStart, SonDolu(tampon)));

        var rHeader = GMPSmartDLL.prepare_TicketHeader(tampon, TamponBoyu, (TTicketType)1);
        adimlar.Add((-2, rHeader, SonDolu(tampon)));

        var rFlags = GMPSmartDLL.prepare_OptionFlags(tampon, TamponBoyu, 7, 0);
        adimlar.Add((-1, rFlags, SonDolu(tampon)));

        var onek = SonDolu(tampon);

        // Kalemleri BİRER BİRER ekleyip uzunluğun nasıl büyüdüğünü ölç; tampon dolunca dur.
        int kalem = 0, sigan = 0;
        while (kalem < 300)
        {
            var st = ProvaKalemi(deptIndex, kalem + 1);
            var onceki = SonDolu(tampon);
            var r = Json_GMPSmartDLL.prepare_ItemSale(tampon, TamponBoyu, ref st);
            var sonra = SonDolu(tampon);
            if (sonra <= onceki)
            {
                Yaz($"[fazA] prepare_ItemSale tamponu BUYUTMEDI (kalem {kalem + 1}, donus={r}) — sinir burada");
                break;
            }
            kalem++; sigan = kalem;
            if (kalem <= 3 || kalem is 5 or 10 or 20 or 50 or 100 or 200) adimlar.Add((kalem, r, sonra));
        }

        var dolu = SonDolu(tampon);
        var kalemBasina = kalem > 0 ? (dolu - onek) / (double)kalem : 0;

        // `prepare_*` dönüşünün ANLAMI: satıcı sarmalayıcısı 0'ı başarı sayıyor (GmpInterop.cs:2972)
        // ama GMP izinde native fonksiyon UZUNLUK döndürüyor ("prepare_OptionFlags (return:33)").
        // İkisi tutarsız — hangisi olduğunu TAHMİN ETMİYORUZ, ölçümden söylüyoruz.
        var donusler = adimlar.Select(a => a.Item2).ToList();
        var anlam = donusler.All(x => x == 0) ? "hep 0 -> 'basari' bayragi"
            : donusler.SequenceEqual(donusler.OrderBy(x => x)) && donusler.Distinct().Count() > 1
                ? "artiyor -> BIRIKIMLI tampon uzunlugu"
                : "karisik -> adim basina uzunluk (tabloya bakin)";
        Yaz($"[fazA] onek={onek} bayt · kalem basina~{kalemBasina:F1} bayt · {TamponBoyu} bayta {sigan} kalem sigdi · donus anlami: {anlam}");

        var bosKodlar = Array.Empty<(uint, uint, uint, ushort, ushort)>();
        if (!cihazaGit)
            return new BatchProbeOlcum(adimlar, anlam, onek, kalemBasina, sigan,
                false, 0, 0, 0, 0, bosKodlar, 0, 0, 0, "cihaza gidilmedi");

        uint h = AcquireInterface();
        if (h == 0)
            return new BatchProbeOlcum(adimlar, anlam, onek, kalemBasina, sigan,
                false, 0xF000, 0, 0, 0, bosKodlar, 0, 0, 0, "arayuz alinamadi — cihaza dokunulmadi");

        // ── FAZ B: TEK KALEM ─────────────────────────────────────────────────────
        // Tampon SIFIRDAN kurulur; yukarıdaki 300 kalemlik ölçüm tamponu GÖNDERİLMEZ.
        var gonder = new byte[TamponBoyu];
        GMPSmartDLL.prepare_Start(gonder, TamponBoyu, uniqueId, uniqueId.Length, null!, 0, userData, userData.Length);
        GMPSmartDLL.prepare_TicketHeader(gonder, TamponBoyu, (TTicketType)1);
        GMPSmartDLL.prepare_OptionFlags(gonder, TamponBoyu, 7, 0);
        var tek = ProvaKalemi(deptIndex, 1);
        Json_GMPSmartDLL.prepare_ItemSale(gonder, TamponBoyu, ref tek);
        var gonderLen = SonDolu(gonder);

        ulong hTrx = 0;
        var kodlar = Array.Empty<ST_MULTIPLE_RETURN_CODE>();
        ushort indexOf = 0;
        var stTicket = new ST_TICKET();

        var kron = Stopwatch.StartNew();
        uint rc = Json_GMPSmartDLL.FP3_MultipleCommand(h, ref hTrx, ref kodlar, ref indexOf,
            gonder, checked((ushort)gonderLen), ref stTicket, TimeoutDefault);
        kron.Stop();
        Yaz($"[fazB] FP3_MultipleCommand rc={rc} sure={kron.ElapsedMilliseconds} ms gonderilen={gonderLen} bayt indexOfReturnCodes={indexOf}");

        var kodTablosu = (kodlar ?? Array.Empty<ST_MULTIPLE_RETURN_CODE>())
            .Select(k => (k.subCommand, k.retcode, k.tag, k.indexOfSubCommand, k.lengthOfData))
            .ToList();

        // Kalem GERÇEKTEN fişe girdi mi? Cihazın KENDİ defterinden AYRI bir çağrıyla sor —
        // MultipleCommand'ın kendi yankısına güvenmiyoruz (yankı ile defter ayrışabilir; bunu
        // ödeme satırlarında zaten yaşadık, bkz. PaymentsAreComplete).
        int kalemSayisi = 0, odemeSayisi = 0; long fisToplam = 0;
        if (hTrx != 0)
        {
            var kontrol = new ST_TICKET();
            if (Json_GMPSmartDLL.FP3_GetTicket(h, hTrx, ref kontrol, TimeoutDefault) == 0)
            {
                kalemSayisi = kontrol.totalNumberOfItems;
                odemeSayisi = kontrol.totalNumberOfPayments;
                fisToplam = kontrol.TotalReceiptAmount;
                Yaz($"[fazB] GetTicket: kalem={kalemSayisi} toplam={fisToplam} odeme={odemeSayisi}");
            }
            else Yaz("[fazB] GetTicket BASARISIZ — kalem dogrulanamadi");
        }

        var temizlik = Temizle(h, hTrx, Yaz);
        return new BatchProbeOlcum(adimlar, anlam, onek, kalemBasina, sigan,
            true, rc, kron.ElapsedMilliseconds, gonderLen, indexOf, kodTablosu,
            kalemSayisi, fisToplam, odemeSayisi, temizlik);
    }

    /// <summary>Tamponda son dolu baytın indeksi+1. <c>prepare_*</c> dönüşü belirsiz olduğu için ölçüm buradan.</summary>
    private static int SonDolu(byte[] b)
    {
        for (var i = b.Length - 1; i >= 0; i--) if (b[i] != 0) return i + 1;
        return 0;
    }

    private static ST_ITEM ProvaKalemi(int deptIndex, int sira) => new()
    {
        type = 1,
        subType = 0,
        deptIndex = checked((byte)deptIndex),
        taxRate = 0,
        unitType = 0,
        amount = 100,
        currency = CurrencyTl,
        count = 1,
        flag = 0,
        countPrecition = 0,
        pluPriceIndex = 0,
        name = $"PROVA {sira}",
        barcode = "",
    };

    /// <summary>
    /// Provadan kalan fişi <b>KANITLA</b> temizler — <c>BayatFisiTemizle</c> ile aynı sıra:
    /// oku → ödeme sayacı 0 mı → VoidAll → Close → yeni Start ile doğrula.
    ///
    /// <para><b>Ödeme sayacı 0 değilse DOKUNMAZ.</b> Provada para olmaması gerekir; olduysa
    /// varsayımımız yanlış demektir ve üzerinde tahsilat olan bir fişi yok etmek geri alınamaz
    /// bir para kaybı olurdu. Silme kararını biz değil, cihazın kendi defteri veriyor.</para>
    /// </summary>
    private string Temizle(uint h, ulong hTrx, Action<string> yaz)
    {
        if (hTrx == 0) return "tanitici yok — temizlenecek fis de yok";

        ulong aktif = 0;
        GMPSmartDLL.FP3_OptionFlags(h, hTrx, ref aktif, 7, 0, TimeoutDefault);

        var oku = new ST_TICKET();
        if (Json_GMPSmartDLL.FP3_GetTicket(h, hTrx, ref oku, TimeoutDefault) != 0)
            return "ALARM: fis OKUNAMADI — DOKUNULMADI, `--cancel-ticket` ile elle temizleyin";

        if (oku.totalNumberOfPayments != 0)
        {
            yaz($"ALARM: provada odeme sayaci {oku.totalNumberOfPayments} — VoidAll CAGRILMADI");
            return $"ALARM: odeme sayaci {oku.totalNumberOfPayments} != 0 — fise DOKUNULMADI";
        }

        var v = VoidAll(hTrx, out _);
        var c = Close(hTrx);
        // Doğrulama: yeni bir Start 2080 (ALREADY_DONE) DÖNMEMELİ.
        var s = Start(out var yeni);
        var kalanVar = s.Code == 2080;
        if (!kalanVar && yeni != 0) Close(yeni);      // doğrulama için açılan BOŞ fişi kapat
        var sonuc = $"VoidAll={v} Close={c} dogrulama={(kalanVar ? "ALARM: fis HALA ACIK" : "temiz")}";
        yaz("[temizlik] " + sonuc);
        return sonuc;
    }
}
