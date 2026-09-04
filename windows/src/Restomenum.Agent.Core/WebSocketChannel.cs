using System.Net.WebSockets;
using System.Text;

namespace Restomenum.Agent.Core;

/// <summary>
/// <see cref="IAgentChannel"/>'ın gerçek WSS uygulaması (<see cref="ClientWebSocket"/>).
///
/// <para><b>Neden ayrı sınıf:</b> protokol mantığı (<see cref="AgentSession"/>) soketi bilmez, bu
/// yüzden el sıkışma ve ACK sırası gerçek ağ olmadan test edilebilir. Buradaki kod ise yalnız
/// çerçeveleme yapar; hiçbir para kararı içermez.</para>
///
/// <para><b>Parçalı frame birleştirilir:</b> WebSocket bir mesajı birden çok parçada teslim
/// edebilir. İlk parçayı tam mesaj sanmak, uzun bir komutun JSON'unu ortadan bölüp sessizce
/// düşürmek olurdu.</para>
/// </summary>
public sealed class WebSocketChannel : IAgentChannel
{
    /// <summary>
    /// Tek bir frame için üst sınır. Sınırsız büyüyen bir tampon, bozuk ya da kötü niyetli bir
    /// karşı tarafın agent'ın belleğini tüketmesine izin verirdi.
    /// </summary>
    private const int MaxFrameBytes = 512 * 1024;

    private readonly ClientWebSocket _ws = new();
    private readonly byte[] _tampon = new byte[16 * 1024];

    /// <summary>Aynı sokete iki eşzamanlı gönderim <see cref="ClientWebSocket"/>'te yasaktır.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public int? CloseCode { get; private set; }

    public WebSocketChannel(TimeSpan? keepAlive = null)
    {
        // Ara katmanlar (Cloud Run, yük dengeleyici) sessiz soketi kapatır. Ping olmadan agent
        // "bağlıyım" sanır ve komutlar ölü sokete gider.
        _ws.Options.KeepAliveInterval = keepAlive ?? TimeSpan.FromSeconds(30);
    }

    public Task ConnectAsync(Uri uri, CancellationToken ct = default) => _ws.ConnectAsync(uri, ct);

    public async Task SendAsync(string json, CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally { _sendGate.Release(); }
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try
            {
                r = await _ws.ReceiveAsync(_tampon, ct);
            }
            catch (WebSocketException)
            {
                // Kopma normaldir; çağıran yeniden bağlanır. Kapanma kodu yoksa `null` kalır.
                CloseCode ??= (int?)_ws.CloseStatus;
                return null;
            }

            if (r.MessageType == WebSocketMessageType.Close)
            {
                CloseCode = (int?)_ws.CloseStatus;
                return null;
            }

            ms.Write(_tampon, 0, r.Count);
            if (ms.Length > MaxFrameBytes)
            {
                await CloseAsync(1009, "frame too large", ct);
                return null;
            }
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async Task CloseAsync(int code, string reason, CancellationToken ct = default)
    {
        try { await _ws.CloseAsync((WebSocketCloseStatus)code, reason, ct); }
        catch (WebSocketException) { /* zaten kapalı */ }
        catch (ObjectDisposedException) { /* zaten atılmış */ }
    }

    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
