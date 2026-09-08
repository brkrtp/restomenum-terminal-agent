using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W48 — bildirim süresi <b>iki ayağa</b> ayrılıyor: oturum alımı ve POST.
///
/// <para><b>Neden (ölçüm 2026-09-08 19:59:34):</b> sahada `sureMs = 3004` göründü ve bunu "POST üç
/// saniye sürdü" diye okumak çok kolaydı. Oysa hedef LOOPBACK ölü porttu; bağlantı ANINDA
/// reddediliyordu. Üç saniyenin tamamı oturum JWT'sini almaktı. Tek sayı, yavaşlığın hangi ayakta
/// olduğunu söyleyemiyor ve teşhisi yanlış yere gönderiyor.</para>
/// </summary>
public class NotifyTimingTests
{
    private sealed class PatlayanOturum : ISessionProvider
    {
        public Task<SessionToken> AcquireAsync(CancellationToken ct = default)
            => throw new HttpRequestException("oturum ucuna ulaşılamadı");
    }

    [Fact]
    public async Task Oturum_alinamazsa_postMs_NULL_kalir_sifir_DEGIL()
    {
        // ← ÇİVİ: POST'a HİÇ gelinmedi. `postMs = 0` yazmak "denendi, anında bitti" derdi —
        // "hiç denenmedi" ile "0 ms sürdü" ayrı olgular ve teşhiste tam bu ayrım aranıyor.
        var n = new HttpResultNotifier(new HttpClient(), new PatlayanOturum(),
            new Uri("http://127.0.0.1:1/"));

        var r = await n.NotifyTicketCancelAsync("{}");

        Assert.Equal(NotifyOutcome.NetworkError, r.Outcome);
        Assert.NotNull(r.SessionMs);      // oturum ayağı DENENDİ ve ölçüldü
        Assert.Null(r.PostMs);            // ← ÇİVİ: POST ayağına gelinmedi
    }

    [Fact]
    public void Olculmemis_sonucta_iki_alan_da_NULL()
    {
        // Sahte istemciler (testler, simülatör) süre ölçmez; alanlar 0 değil NULL gelmeli ki
        // "ölçülmedi" ile "0 ms" karışmasın.
        var r = new NotifyResult(NotifyOutcome.Recorded, "OK", null, 200, "");

        Assert.Null(r.SessionMs);
        Assert.Null(r.PostMs);
    }
}
