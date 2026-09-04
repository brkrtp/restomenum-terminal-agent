namespace Restomenum.Agent.Core;

/// <summary>
/// Test/geliştirme için terminal taklidi. Gerçek cihaz olmadan orkestrasyonun tüm dallarını
/// yürütmeyi sağlar — özellikle sahada **hiç gözlenmemiş** olanları (`Unknown`, `TicketAlreadyOpen`),
/// ki tam da onlar doğrulanmamış olduğu için tehlikeli.
///
/// Süreler sahadan alındı: kartlı ödeme 20–32 sn, diğer çağrılar 0.3–1.6 sn (§8.3d). Testte
/// beklemek anlamsız olduğu için varsayılan gecikme sıfırdır; gerçekçi zamanlama isteyen
/// <see cref="Delay"/>'i verir.
/// </summary>
public sealed class SimulatorTransport : ITerminalTransport
{
    private readonly Queue<TransportResult> _plan = new();
    private TicketState _ticket = new(HasOpenTicket: false, TotalAmountMinor: 0, PaidAmountMinor: 0);
    private int _busyReads;
    private bool _readThrows;

    /// <summary>Her çağrı öncesi beklenecek süre (gerçekçi zamanlama testleri için).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public List<SaleRequest> SaleCalls { get; } = new();
    public int ReadTicketCalls { get; private set; }
    public bool EchoResult { get; set; } = true;

    /// <summary>Sıradaki satış çağrısının ne döneceğini kuyruğa ekler.</summary>
    public SimulatorTransport Expect(TransportResult result) { _plan.Enqueue(result); return this; }

    /// <summary>Terminaldeki açık fişin durumunu kurar — `UNKNOWN` çözümünün cevabı.</summary>
    public SimulatorTransport WithTicket(TicketState ticket) { _ticket = ticket; return this; }

    /// <summary>
    /// İlk N fiş sorgusunun `RECV_BUSY` ile başarısız olmasını sağlar — sahada ölçülen desen
    /// (ilk sorgu HER ZAMAN meşgul döndü). Geri çekilme mantığını doğrulamak için gerekli.
    /// </summary>
    public SimulatorTransport WithBusyReads(int count) { _busyReads = count; return this; }

    /// <summary>Fiş sorgusunun kalıcı olarak hata vermesini sağlar (terminale ulaşılamıyor).</summary>
    public SimulatorTransport WithUnreachableReads() { _readThrows = true; return this; }

    public async Task<TransportResult> SaleAsync(SaleRequest request, CancellationToken ct = default)
    {
        SaleCalls.Add(request);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        // Plan bittiyse `Unknown` döner: testte "beklenmedik ikinci çağrı" sessizce başarılı
        // görünmesin — en tehlikeli dala düşsün ki fark edilsin.
        return _plan.Count > 0 ? _plan.Dequeue() : new TransportResult(TransportOutcome.Unknown);
    }

    public async Task<TicketState> ReadTicketAsync(CancellationToken ct = default)
    {
        ReadTicketCalls++;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (_readThrows) throw new IOException("terminale ulaşılamıyor");
        if (_busyReads > 0) { _busyReads--; throw new TerminalBusyException(); }
        return _ticket;
    }

    public Task<bool> EchoAsync(CancellationToken ct = default) => Task.FromResult(EchoResult);
}
