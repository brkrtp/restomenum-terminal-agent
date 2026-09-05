using Restomenum.Agent.Core;
using Restomenum.Agent.Gmp.Interop;

namespace Restomenum.Agent.Gmp;

/// <summary>
/// <see cref="IGmpWrapper"/>'ın gerçek uygulaması: <c>GmpSmartDLL.dll</c>'e P/Invoke.
///
/// <para><b>Mali karar İÇERMEZ.</b> Her metot tek bir <c>FP3_*</c> çağrısıdır, ham dönüş kodunu
/// (ve varsa fiş durumunu) döner, yorumlamaz. Sıra/kurtarma/kilit kararları çağırandadır
/// (<c>GmpTerminalTransport</c>). İki yasak: gizli retry YOK (RECV_BUSY yukarı taşınır), kilit YOK.</para>
///
/// <para>Çağrılar senkrondur çünkü P/Invoke gerçekten bloke eder; <see cref="Payment"/> kartlı
/// işlemde 90 sn'ye kadar bloke eder (GMP.XML <c>CommTimeOut</c>).</para>
///
/// <para><b>Deploy:</b> <c>GmpSmartDLL.dll</c> ve <c>GMP.XML</c> çalışan sürecin dizininde olmalı.</para>
/// </summary>
public sealed class GmpWrapper : IGmpWrapper
{
    private const int TimeoutDefault = 10000;
    private const int TimeoutCard    = 90000;   // FP3_Payment
    private const int TimeoutEcho    = 10000;
    private const int TimeoutPrintMf = 100000;
    private const ushort CurrencyTl = 949;

    // Arayüz alınamazsa (GMP.XML eksik/bozuk ya da DLL arayüzü yükleyemedi) çağırana ayırt
    // edilebilir "cihaz hazır değil" sinyali dönülür: GmpCodes.PortNotOpen (0xF000). Belirsizlik
    // DEĞİL, kesin yapılandırma hatası — çağıran onu geri çekilme/insan kuyruğuna sokmaz.

    // volatile: çift kontrollü kilit deseninde görünürlük garantisi (bedava).
    private volatile uint _hInt;
    private readonly object _initLock = new();

    /// <summary>
    /// Arayüz handle'ı. DLL, GMP.XML'deki <c>Interface1</c>'i yükler; ilk (varsayılan) arayüzü alırız.
    /// Alınamazsa 0 döner — çağıran metotlar bunu <see cref="PortNotOpen"/>'a çevirir, sessizce
    /// geçersiz handle ile FP3 çağrısı YAPMAZ.
    /// </summary>
    private uint AcquireInterface()
    {
        if (_hInt != 0) return _hInt;
        lock (_initLock)
        {
            if (_hInt != 0) return _hInt;
            var list = new uint[20];
            uint count = GMPSmartDLL.FP3_GetInterfaceHandleList(list, (uint)list.Length);
            if (count > 0) _hInt = list[0];
            return _hInt;
        }
    }

    public GmpResult Start(out ulong handle)
    {
        handle = 0;
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;

        ulong hTrx = 0;
        var uniqueId = new byte[24];                                   // sertifikalı kod da sıfır gönderiyor
        // UserData "testdata": sertifikalı StartTicketInternal ile BİREBİR aynı sabit. Cihaza mali
        // anlamı olmayan bir işaret; değiştirmek sertifikalı davranıştan sapmak olur, o yüzden korunur.
        var userData = new byte[] { 0x74, 0x65, 0x73, 0x74, 0x64, 0x61, 0x74, 0x61 };
        uint rc = GMPSmartDLL.FP3_Start(h, ref hTrx, 0, uniqueId, uniqueId.Length,
            null!, 0, userData, userData.Length, TimeoutDefault);
        handle = hTrx;
        return rc;
    }

    public GmpResult TicketHeader(ulong handle, int ticketType)
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        return GMPSmartDLL.FP3_TicketHeader(h, handle, (TTicketType)ticketType, TimeoutDefault);
    }

    public GmpResult OptionFlags(ulong handle, GmpEchoFlags flags)
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        ulong active = 0;
        return GMPSmartDLL.FP3_OptionFlags(h, handle, ref active, (ulong)flags, 0, TimeoutDefault);
    }

    public GmpResult ItemSale(ulong handle, GmpItem item, out GmpTicket ticket)
    {
        ticket = default;
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;

        var st = new ST_ITEM
        {
            type      = 1,                          // ITEM_TYPE_DEPARTMENT (0 geçersiz)
            subType   = 0,
            deptIndex = checked((byte)item.DepartmentNo),
            // taxRate = 0 KASITLI: terminal KDV'yi deptIndex'ten türetir. Canlı doğrulandı (2026-09-05):
            // taxRate=0 dept-0 100 ₺ kalemine 16,67 ₺ KDV (=%20). Ayrı taxRate alanına gerek yok.
            taxRate   = 0,
            unitType  = 0,
            amount    = checked((uint)item.UnitPriceMinor),
            currency  = CurrencyTl,
            count     = checked((uint)item.Quantity),
            flag      = 0,
            countPrecition = 0,
            pluPriceIndex  = 0,
            name      = item.Name,
            barcode   = "",
        };
        var stTicket = new ST_TICKET();
        uint rc = Json_GMPSmartDLL.FP3_ItemSale(h, handle, ref st, ref stTicket, TimeoutDefault);
        ticket = Map(stTicket);
        return rc;
    }

    public GmpResult Payment(ulong handle, GmpPaymentRequest request, out GmpTicket ticket)
    {
        ticket = default;
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;

        var req = new ST_PAYMENT_REQUEST
        {
            typeOfPayment         = checked((uint)request.PaymentType),
            subtypeOfPayment      = 0,
            payAmount             = checked((uint)request.AmountMinor),
            payAmountCurrencyCode = CurrencyTl,
            paymentName           = "",
            transactionFlag       = 0,
        };
        var stTicket = new ST_TICKET();
        // Kartlı işlemde 90 sn'ye kadar bloke eder — bu tek bloklayan çağrı.
        uint rc = Json_GMPSmartDLL.FP3_Payment(h, handle, ref req, ref stTicket, TimeoutCard);
        ticket = Map(stTicket);
        return rc;
    }

    public GmpResult GetTicket(ulong handle, out GmpTicket ticket)
    {
        ticket = default;
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;

        var stTicket = new ST_TICKET();
        uint rc = Json_GMPSmartDLL.FP3_GetTicket(h, handle, ref stTicket, TimeoutDefault);
        ticket = Map(stTicket);
        return rc;
    }

    public GmpResult PrintTotalsAndPayments(ulong handle)
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        return GMPSmartDLL.FP3_PrintTotalsAndPayments(h, handle, TimeoutDefault);
    }

    public GmpResult PrintBeforeMF(ulong handle)
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        return GMPSmartDLL.FP3_PrintBeforeMF(h, handle, TimeoutDefault);
    }

    public GmpResult PrintUserMessage(ulong handle)
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        var msgs = new ST_USER_MESSAGE[1];
        msgs[0] = new ST_USER_MESSAGE();
        var stTicket = new ST_TICKET();
        return Json_GMPSmartDLL.FP3_PrintUserMessage(h, handle, ref msgs, (ushort)msgs.Length, ref stTicket, TimeoutDefault);
    }

    public GmpResult PrintMF(ulong handle)
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        return GMPSmartDLL.FP3_PrintMF(h, handle, TimeoutPrintMf);
    }

    public GmpResult VoidAll(ulong handle, out GmpTicket ticket)
    {
        ticket = default;
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;

        var stTicket = new ST_TICKET();
        uint rc = Json_GMPSmartDLL.FP3_VoidAll(h, handle, ref stTicket, TimeoutDefault);
        ticket = Map(stTicket);
        return rc;
    }

    public GmpResult VoidPayment(ulong handle, int paymentIndex)
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        // ⚠️ İMZA TAHMİNİ: sahada hiç ölçülmedi. ushort Index eşlemesi DLL'in tipli sarmalayıcısıyla uyumlu.
        var stTicket = new ST_TICKET();
        return Json_GMPSmartDLL.FP3_VoidPayment(h, handle, checked((ushort)paymentIndex), ref stTicket, TimeoutDefault);
    }

    public GmpResult Close(ulong handle)
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        return GMPSmartDLL.FP3_Close(h, handle, TimeoutDefault);
    }

    public GmpResult Echo()
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        var echo = new ST_ECHO();
        return Json_GMPSmartDLL.FP3_Echo(h, ref echo, TimeoutEcho);
    }

    // ── Provisioning ─────────────────────────────────────────────────────────
    // Eşleşme SÜRECE bağlı ve TEK-SLOT (canlı ölçüldü): işlem yapacak süreç kendi eşleşmesini
    // kurmalı. Bu yüzden pairing sertifikalı yüzeyde. Dışarıdan tetiklenir (agent "eşleş" der),
    // ama FP3_StartPairingInit çağrısı bu süreçte olur.

    public GmpResult Pair()
    {
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        // Sertifikalı StartPairing sırası: önce Echo (bağlantı), sonra StartPairingInit.
        var echo = new ST_ECHO();
        uint erc = Json_GMPSmartDLL.FP3_Echo(h, ref echo, TimeoutEcho);
        if (erc != GmpCodes.Ok) return erc;
        var pair = new ST_GMP_PAIR
        {
            szExternalDeviceBrand        = "INGENICO",
            szExternalDeviceModel        = "RESTOMENUM",
            szExternalDeviceSerialNumber = "",
            szEcrSerialNumber            = "",
            szProcOrderNumber            = "000001",
            szProcDate                   = DateTime.Now.ToString("yyyyMMdd"),
            szProcTime                   = DateTime.Now.ToString("HHmmss"),
        };
        var resp = new ST_GMP_PAIR_RESP();
        return Json_GMPSmartDLL.FP3_StartPairingInit(h, ref pair, ref resp, TimeoutDefault);
    }

    public GmpResult CheckPairing(out bool paired)
    {
        paired = false;
        uint h = AcquireInterface();
        if (h == 0) return GmpCodes.PortNotOpen;
        // FP3_IsGmpPairingDone dönüşü STANDART retcode DEĞİL: 0 = eşleşme YOK, !=0 = eşleşme VAR
        // (sertifikalı DLLController.CheckPairing semantiği). Çağrının kendisi başarılı sayılır.
        uint rc = GMPSmartDLL.FP3_IsGmpPairingDone(h);
        paired = rc != 0;
        return GmpCodes.Ok;
    }

    /// <summary>
    /// <see cref="ST_TICKET"/> → <see cref="GmpTicket"/>. <b>Yalnız alan taşıma.</b>
    /// <list type="bullet">
    ///   <item>Toplam = <c>TotalReceiptAmount + KatkiPayiAmount</c> — sertifikalı DLLController'ın
    ///   "TicketAmount" hesabıyla BİREBİR (GetPaymentInternal: <c>TicketAmount = TotalReceiptAmount +
    ///   KatkiPayiAmount</c>, sonra <c>TotalReceiptPayment >= TicketAmount</c> ile tam ödeme kontrolü).
    ///   Tahmin değil, üretimde çalışan formülün aynısı.</item>
    ///   <item><b>PaymentCount = totalNumberOfPayments</b> — belirsizlik çözümü tutara değil bu sayaca dayanır.</item>
    ///   <item>LastPaymentType = son ödemenin <c>typeOfPayment</c>'ı (1=nakit, 4=kart, 16=mobil/QR).</item>
    /// </list>
    ///
    /// <para><b>Bozuk okuma koruması:</b> <c>totalNumberOfPayments</c> sabit <c>stPayment</c> dizisinin
    /// (24) sınırını aşarsa bu BOZUK bir okumadır (bellek çöpü). Sessizce geçirmek, gerçekleşmemiş bir
    /// ödemeyi "sayaç arttı → Landed → APPROVED" yaptırır. Bu durumda <c>PaymentCount = -1</c> döner;
    /// çağıran bunu "okunamadı" diye ele almalı, "ödeme yok" diye DEĞİL.</para>
    /// </summary>
    private static GmpTicket Map(ST_TICKET t)
    {
        long total = (long)t.TotalReceiptAmount + t.KatkiPayiAmount;
        long paid  = t.TotalReceiptPayment;
        int  count = t.totalNumberOfPayments;

        var payments = t.stPayment;
        // Bozuk sayaç: dizi yok ya da sayaç dizi kapasitesini aşıyor → PaymentCount = -1 (okunamadı).
        if (count > 0 && (payments is null || count > payments.Length))
            return new GmpTicket(total, paid, -1, 0, null, null);

        int lastType = 0;
        string? rrn = null;
        string? last4 = null;

        if (count > 0)
        {
            var p = payments![count - 1];
            if (p is not null)
            {
                lastType = (int)p.typeOfPayment;
                var bank = p.stBankPayment;
                if (bank is not null)
                {
                    rrn = string.IsNullOrWhiteSpace(bank.rrn) ? null : bank.rrn;
                    var pan = bank.stCard?.pan;
                    if (!string.IsNullOrEmpty(pan))
                    {
                        // PAN maskeli gelir (ör. "492345****1234"); son 4 haneyi al.
                        var digits = new string(pan.Where(char.IsDigit).ToArray());
                        if (digits.Length >= 4) last4 = digits[^4..];
                    }
                }
            }
        }

        return new GmpTicket(total, paid, count, lastType, rrn, last4);
    }
}
