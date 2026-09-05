using Restomenum.Agent.Core;
using Restomenum.Agent.Gmp.Interop;

namespace Restomenum.Agent.Gmp;

/// <summary>
/// <see cref="IGmpWrapper"/>'ın gerçek uygulaması: <c>GmpSmartDLL.dll</c>'e P/Invoke.
///
/// <para><b>Mali karar İÇERMEZ.</b> Her metot tek bir <c>FP3_*</c> çağrısıdır, ham dönüş kodunu
/// (ve varsa fiş durumunu) döner, yorumlamaz. Sıra/kurtarma/kilit kararları çağırandadır
/// (<c>GmpTerminalTransport</c>). İki yasak sözleşmedeki gibi korunur:</para>
/// <list type="number">
///   <item><b>Gizli retry YOK</b> — hiçbir metot kendi başına tekrar çağırmaz; <c>RECV_BUSY</c>
///   dahil her kod olduğu gibi yukarı taşınır.</item>
///   <item><b>Kilit YOK</b> — seri hâle getirme agent'ın işi.</item>
/// </list>
///
/// <para>Çağrılar senkrondur çünkü P/Invoke gerçekten bloke eder; <see cref="Payment"/> kartlı
/// işlemde 90 sn'ye kadar bloke eder (GMP.XML <c>CommTimeOut</c>).</para>
///
/// <para><b>Deploy:</b> <c>GmpSmartDLL.dll</c> ve <c>GMP.XML</c> çalışan sürecin dizininde olmalı.
/// DLL, GMP.XML'den arayüzü yükler; bu sınıf <c>FP3_GetInterfaceHandleList</c> ile arayüz handle'ını alır.</para>
/// </summary>
public sealed class GmpWrapper : IGmpWrapper
{
    // Timeout sabitleri — sertifikalı EXE 1'deki değerlerle birebir (Defines.TIMEOUT_*).
    private const int TimeoutDefault = 10000;   // 10 sn
    private const int TimeoutCard    = 90000;   // FP3_Payment — DLLController da 90000 kullanıyor
    private const int TimeoutEcho    = 10000;   // 10 sn
    private const int TimeoutPrintMf = 100000;  // 100 sn (TIMEOUT_CARD_TRANSACTIONS mertebesi)

    private const ushort CurrencyTl = 949;      // TL (ISO 4217)

    private uint _hInt;
    private readonly object _initLock = new();

    /// <summary>
    /// Arayüz handle'ı. DLL, GMP.XML'deki <c>Interface1</c>'i yükler; ilk (varsayılan) arayüzü alırız.
    /// Tembel + tek sefer: handle süreç ömrü boyunca sabittir.
    /// </summary>
    private uint Interface
    {
        get
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
    }

    public GmpResult Start(out ulong handle)
    {
        handle = 0;
        ulong hTrx = 0;
        // uniqueId sıfır 24 byte — sertifikalı kod da böyle gönderiyor (TransactionUniqueIdList hiç doldurulmuyor).
        var uniqueId = new byte[24];
        // UserData "testdata" — sertifikalı StartTicketInternal ile aynı.
        var userData = new byte[] { 0x74, 0x65, 0x73, 0x74, 0x64, 0x61, 0x74, 0x61 };
        uint rc = GMPSmartDLL.FP3_Start(
            Interface, ref hTrx, 0,
            uniqueId, uniqueId.Length,
            null!, 0,                               // pUniqueIdSign: TSM imzası yok → null (P/Invoke null pointer)
            userData, userData.Length,
            TimeoutDefault);
        handle = hTrx;
        return rc;
    }

    public GmpResult TicketHeader(ulong handle, int ticketType)
        => GMPSmartDLL.FP3_TicketHeader(Interface, handle, (TTicketType)ticketType, TimeoutDefault);

    public GmpResult OptionFlags(ulong handle, GmpEchoFlags flags)
    {
        ulong active = 0;
        return GMPSmartDLL.FP3_OptionFlags(Interface, handle, ref active, (ulong)flags, 0, TimeoutDefault);
    }

    public GmpResult ItemSale(ulong handle, GmpItem item, out GmpTicket ticket)
    {
        var st = new ST_ITEM
        {
            type      = 1,                          // ITEM_TYPE_DEPARTMENT (0 geçersiz)
            subType   = 0,
            deptIndex = checked((byte)item.DepartmentNo),
            // taxRate = 0 KASITLI: terminal KDV'yi deptIndex'ten türetir. Canlı doğrulandı
            // (2026-09-05): taxRate=0 gönderilen 100 ₺'lik dept-0 kalemine terminal 16,67 ₺ KDV
            // hesapladı (= %20, dept 0'ın oranı). Yani GmpItem'ın taxRate taşımaması doğru tasarım;
            // departman vergi kaynağıdır, ayrı taxRate alanına gerek yok.
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
        uint rc = Json_GMPSmartDLL.FP3_ItemSale(Interface, handle, ref st, ref stTicket, TimeoutDefault);
        ticket = Map(stTicket);
        return rc;
    }

    public GmpResult Payment(ulong handle, GmpPaymentRequest request, out GmpTicket ticket)
    {
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
        uint rc = Json_GMPSmartDLL.FP3_Payment(Interface, handle, ref req, ref stTicket, TimeoutCard);
        ticket = Map(stTicket);
        return rc;
    }

    public GmpResult GetTicket(ulong handle, out GmpTicket ticket)
    {
        var stTicket = new ST_TICKET();
        uint rc = Json_GMPSmartDLL.FP3_GetTicket(Interface, handle, ref stTicket, TimeoutDefault);
        ticket = Map(stTicket);
        return rc;
    }

    public GmpResult PrintTotalsAndPayments(ulong handle)
        => GMPSmartDLL.FP3_PrintTotalsAndPayments(Interface, handle, TimeoutDefault);

    public GmpResult PrintBeforeMF(ulong handle)
        => GMPSmartDLL.FP3_PrintBeforeMF(Interface, handle, TimeoutDefault);

    public GmpResult PrintUserMessage(ulong handle)
    {
        var msgs = new ST_USER_MESSAGE[1];
        msgs[0] = new ST_USER_MESSAGE();
        var stTicket = new ST_TICKET();
        return Json_GMPSmartDLL.FP3_PrintUserMessage(
            Interface, handle, ref msgs, (ushort)msgs.Length, ref stTicket, TimeoutDefault);
    }

    public GmpResult PrintMF(ulong handle)
        => GMPSmartDLL.FP3_PrintMF(Interface, handle, TimeoutPrintMf);

    public GmpResult VoidAll(ulong handle, out GmpTicket ticket)
    {
        var stTicket = new ST_TICKET();
        uint rc = Json_GMPSmartDLL.FP3_VoidAll(Interface, handle, ref stTicket, TimeoutDefault);
        ticket = Map(stTicket);
        return rc;
    }

    public GmpResult VoidPayment(ulong handle, int paymentIndex)
    {
        // ⚠️ İMZA TAHMİNİ (sözleşmedeki uyarı): dokuz saha logunda hiç geçmedi, canlı terminalde
        // fiziksel kart olmadan tetiklenemedi. Buradaki eşleme (ushort Index) DLL'in tipli
        // FP3_VoidPayment sarmalayıcısıyla uyumlu, ama davranış ölçülene kadar doğrulanmamıştır.
        var stTicket = new ST_TICKET();
        return Json_GMPSmartDLL.FP3_VoidPayment(Interface, handle, checked((ushort)paymentIndex), ref stTicket, TimeoutDefault);
    }

    public GmpResult Close(ulong handle)
        => GMPSmartDLL.FP3_Close(Interface, handle, TimeoutDefault);

    public GmpResult Echo()
    {
        var echo = new ST_ECHO();
        return Json_GMPSmartDLL.FP3_Echo(Interface, ref echo, TimeoutEcho);
    }

    /// <summary>
    /// <see cref="ST_TICKET"/> → <see cref="GmpTicket"/>. <b>Yalnız alan taşıma, yorum yok.</b>
    /// <list type="bullet">
    ///   <item>Toplam = <c>TotalReceiptAmount + KatkiPayiAmount</c> (sertifikalı kodun "TicketAmount"ı).</item>
    ///   <item>Ödenen = <c>TotalReceiptPayment</c>.</item>
    ///   <item><b>PaymentCount = totalNumberOfPayments</b> — belirsizlik çözümü tutara değil bu sayaca dayanır
    ///   (20 ₺ + 20 ₺ ayırt edilemez), o yüzden ayrı alan olarak taşınır.</item>
    ///   <item>LastPaymentType = son ödemenin <c>typeOfPayment</c>'ı (1=nakit, 4=kart, 16=mobil/QR).</item>
    ///   <item>Rrn / CardLast4 = son ödemenin banka bacağından; PAN terminalce maskeli gelir.</item>
    /// </list>
    /// </summary>
    private static GmpTicket Map(ST_TICKET t)
    {
        long total = (long)t.TotalReceiptAmount + t.KatkiPayiAmount;
        long paid  = t.TotalReceiptPayment;
        int  count = t.totalNumberOfPayments;

        int lastType = 0;
        string? rrn = null;
        string? last4 = null;

        if (count > 0 && t.stPayment is { Length: > 0 } payments && count <= payments.Length)
        {
            var p = payments[count - 1];
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
