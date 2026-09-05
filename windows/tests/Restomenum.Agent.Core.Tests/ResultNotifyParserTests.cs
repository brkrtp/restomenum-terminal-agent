using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// <c>POST /result</c> yanıtı — peer'de gerçek HTTP ile ölçülen gövdeler (e2e 51/51, 760aa8f09).
/// Outbox kararı: kesin (IsFinal) mi tekrar mı, alarm (IsProblem) mı.
/// </summary>
public class ResultNotifyParserTests
{
    [Fact]
    public void Onay_kaydedildi_kesin()
    {
        var r = ResultNotifyParser.Parse(200, """{"success":true,"data":{"recorded":true,"state":"APPROVED"}}""");
        Assert.Equal(NotifyOutcome.Recorded, r.Outcome);
        Assert.Equal("APPROVED", r.State);
        Assert.True(r.IsFinal);
        Assert.False(r.IsProblem);
    }

    [Fact]
    public void Ilerleme_kaydedildi()
    {
        var r = ResultNotifyParser.Parse(200, """{"success":true,"data":{"recorded":true,"state":"WAITING_CUSTOMER"}}""");
        Assert.Equal(NotifyOutcome.Recorded, r.Outcome);
        Assert.Equal("WAITING_CUSTOMER", r.State);
    }

    [Fact]
    public void recorded_false_bayat_HATA_DEGIL()
    {
        var r = ResultNotifyParser.Parse(200, """{"success":true,"data":{"recorded":false,"reason":"stale","state":"APPROVED"}}""");
        Assert.Equal(NotifyOutcome.Superseded, r.Outcome);
        Assert.Equal("stale", r.Reason);
        Assert.True(r.IsFinal);      // tekrar gönderme
        Assert.False(r.IsProblem);   // alarm değil
    }

    [Theory]
    [InlineData("plugin.payment.paymentIdMismatch")]
    [InlineData("plugin.payment.amountExceedsRequested")]
    [InlineData("plugin.payment.conflictingResult")]
    [InlineData("plugin.payment.currencyMismatch")]
    public void Cakisma_409_kesin_ve_alarm(string message)
    {
        var r = ResultNotifyParser.Parse(409, $$"""{"success":false,"message":"{{message}}"}""");
        Assert.Equal(NotifyOutcome.Conflict, r.Outcome);
        Assert.True(r.IsFinal);      // aynı gövde tekrar aynı hatayı verir
        Assert.True(r.IsProblem);    // sorun sinyali
    }

    [Fact]
    public void Ham_PAN_400_reddi()
    {
        var r = ResultNotifyParser.Parse(400, """{"success":false,"message":"plugin.payment.invalidCardLast4"}""");
        Assert.Equal(NotifyOutcome.Rejected, r.Outcome);
        Assert.True(r.IsProblem);
    }

    [Fact]
    public void NotFound_404()
    {
        var r = ResultNotifyParser.Parse(404, """{"success":false,"message":"plugin.payment.notFound"}""");
        Assert.Equal(NotifyOutcome.NotFound, r.Outcome);
        Assert.True(r.IsFinal);
        Assert.True(r.IsProblem);
    }

    [Fact]
    public void RateLimited_429_tekrar_denenir()
    {
        var r = ResultNotifyParser.Parse(429, """{"success":false,"message":"plugin.rateLimited"}""");
        Assert.Equal(NotifyOutcome.RateLimited, r.Outcome);
        Assert.False(r.IsFinal);     // outbox'ta kalır, geri çekilip tekrar
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    [InlineData(0)]
    public void Sunucu_ag_hatasi_tekrar_denenir(int status)
    {
        var r = ResultNotifyParser.Parse(status, "");
        Assert.Equal(NotifyOutcome.NetworkError, r.Outcome);
        Assert.False(r.IsFinal);
    }
}
