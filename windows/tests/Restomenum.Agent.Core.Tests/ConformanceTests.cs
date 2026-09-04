using System.Text.Json;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// **Uygunluk vektörleri** — <c>conformance</c> klasöründeki JSON dosyaları.
///
/// Android agent'ı da AYNI dosyaları okuyup aynı sonucu üretir
/// (<c>android/core/.../ConformanceTest.kt</c>). İki uygulama ayrışırsa bu testler patlar.
///
/// **Neden bu kadar önemli:** .NET-only terminal SDK'ları yüzünden iki agent zorunlu; yani §12.2'nin
/// dokuz değişmezi iki kez uygulanıyor. Ayrışan bir dedupe/durum mantığının bedeli ÇİFT TAHSİLAT ve
/// iki agent'ı aynı anda kimse test etmeyeceği için sapma aylarca görünmez kalabilir.
///
/// Vektörler koda GÖMÜLMEZ; dosyadan okunur. Gömülen kopya, dosya değiştiğinde güncellenmez.
/// </summary>
public class ConformanceTests
{
    private static JsonDocument Load(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;
        Assert.True(dir is not null, "conformance klasörü bulunamadı");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "conformance", name)));
    }

    [Fact]
    public void SigningCanonicalStringMatchesVectors()
    {
        using var doc = Load("signing.json");
        var n = 0;
        foreach (var v in doc.RootElement.GetProperty("vektorler").EnumerateArray())
        {
            var produced = SessionSigning.CanonicalString(
                v.GetProperty("connectorId").GetString()!,
                v.GetProperty("nonce").GetString()!,
                v.GetProperty("timestamp").GetRawText());
            Assert.Equal(v.GetProperty("expected").GetString(), produced);
            n++;
        }
        Assert.True(n >= 6, $"vektör dosyası boşalmış olmasın: {n}");
    }

    [Fact]
    public void DelimiterInFieldDoesNotCollide()
    {
        // Düz "a|b|c" birleştirmesinde bu ikisi AYNI olurdu; tek imza iki farklı isteği doğrulardı.
        Assert.NotEqual(
            SessionSigning.CanonicalString("conn|9", "abc", "1"),
            SessionSigning.CanonicalString("conn", "9|abc", "1"));
    }

    [Fact]
    public void StateTransitionsMatchVectors()
    {
        using var doc = Load("state-transitions.json");
        var n = 0;
        foreach (var v in doc.RootElement.GetProperty("vektorler").EnumerateArray())
        {
            var from = Enum.Parse<CommandState>(v.GetProperty("from").GetString()!);
            var to = Enum.Parse<CommandState>(v.GetProperty("to").GetString()!);
            Assert.Equal(v.GetProperty("expected").GetBoolean(), AgentStateRules.CanTransition(from, to));
            n++;
        }
        Assert.Equal(30, n);

        foreach (var e in doc.RootElement.GetProperty("final_durumlar").EnumerateArray())
            Assert.True(Enum.Parse<CommandState>(e.GetString()!).IsFinal(), e.GetString());

        foreach (var e in doc.RootElement.GetProperty("terminale_ulasmis_olabilir").EnumerateArray())
            Assert.True(Enum.Parse<CommandState>(e.GetString()!).MayHaveReachedTerminal(), e.GetString());
    }

    [Fact]
    public void BackoffMatchesVectors()
    {
        using var doc = Load("backoff.json");
        var steps = doc.RootElement.GetProperty("basamaklar_ms")
            .EnumerateArray().Select(e => e.GetInt64()).ToArray();
        var pct = doc.RootElement.GetProperty("jitter_yuzde").GetInt32();

        var b = new Backoff(random: x => x / 2);   // jitter'ı sıfırla → taban görünsün
        foreach (var expected in steps) Assert.Equal(expected, b.Next());
        Assert.Equal(steps[^1], b.Next());          // tavanda kalır

        var first = steps[0];
        var low = first - first * pct / 100;
        var high = first + first * pct / 100;
        for (var i = 0; i < 50; i++)
        {
            var g = new Backoff().Next();
            Assert.InRange(g, low, high);
        }
    }

    [Fact]
    public void ClockRefusesToGuessWhenUnsynced()
    {
        // §5.3 — TAHMİN YOK. Cihaz saatine güvenmek, geçerli tahsilatları reddetmeye veya süresi
        // geçmişleri çalıştırmaya yol açar.
        var clock = new ClockOffset();
        Assert.False(clock.IsSynced);
        Assert.Null(clock.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000));
        Assert.Throws<InvalidOperationException>(() => clock.ServerNow());
    }

    [Fact]
    public void ClockCorrectsWrongWallClock()
    {
        // Kasa saati 2 saat geride. Cihaz saatine güvenilseydi geçerli komutlar "süresi geçti"
        // diye reddedilirdi.
        var realServer = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var wrongWall = realServer - 2 * 3600_000;
        long mono = 1000;
        var clock = new ClockOffset(wallClock: () => wrongWall, monotonic: () => mono);
        clock.Sync(realServer, mono);

        Assert.True(clock.IsSynced);
        Assert.True(Math.Abs(clock.ServerNow() - realServer) < 1000);
        Assert.False(clock.IsExpired(realServer + 60_000));
        Assert.True(clock.IsExpired(realServer - 1));
    }

    [Fact]
    public void ClockRejectsStaleOffset()
    {
        long mono = 1000;
        var clock = new ClockOffset(
            wallClock: () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            monotonic: () => mono);
        clock.Sync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), mono);
        Assert.True(clock.IsSynced);

        mono += 11 * 60_000;   // 11 dk geçti
        Assert.False(clock.IsSynced);
        Assert.Null(clock.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000));
    }
}
