namespace Restomenum.Agent.Core;

/// <summary>Orkestratörün dış dünyaya verdiği karar.</summary>
public enum AgentDecision
{
    /// <summary>Ödeme alındı. `Result` doludur.</summary>
    Approved,
    /// <summary>Terminal reddetti. Para hareket etmedi, kesin.</summary>
    Declined,
    /// <summary>Süresi geçmiş; terminale HİÇ gönderilmedi.</summary>
    Expired,
    /// <summary>Saat senkronu yok — komut çalıştırılmadı (§5.3, TAHMİN YOK).</summary>
    ClockUnsynced,
    /// <summary>Şu an olmaz; **güvenle** tekrar denenebilir — terminale ulaşmadığı DOĞRULANDI.</summary>
    RetryLater,
    /// <summary>Belirsizlik çözülemedi. Para hareket etmiş olabilir; SALE tekrarı YASAK.</summary>
    Unresolved,
    /// <summary>Aynı komut daha önce işlendi; saklanan sonuç döndü. Terminal ÇAĞRILMADI.</summary>
    Replayed,
}

/// <summary>Orkestratör sonucu.</summary>
public sealed record AgentOutcome(
    AgentDecision Decision,
    CommandState State,
    TransportResult? Result = null,
    string? Note = null);

/// <summary>
/// Agent'ın kalbi: komutu alır, §12.2'nin dokuz değişmezini uygular, terminale gönderir.
///
/// ## Tasarımın merkezindeki ilke: VARSAYMA, SOR
///
/// Belirsiz her sonuç — `Busy`, `Unknown`, `TicketAlreadyOpen` — **tek bir yola** girer:
/// <see cref="ITerminalTransport.ReadTicketAsync"/>. Terminale sorulur.
///
/// Saha "`RECV_BUSY` komut ulaşmadan döner, güvenle tekrarlanabilir" diyor ve muhtemelen doğru —
/// ama bu **kanıtlanmadı**. Paranın hareket edip etmediğinin tek otoritesi terminaldir. Varsayımla
/// "güvenle tekrarla" demek, varsayım yanlışsa **çift tahsilattır**. Sormak bir tur maliyeti
/// getirir; yanlış varsaymak müşterinin parasına mal olur.
///
/// Aynı sebeple `Unknown` sonrası **asla SALE tekrarlanmaz** (§12.2/6) — yalnız sorgulanır.
///
/// ## Durum SIRASI kritik
///
/// `SENT_TO_TERMINAL`'a **çağrıdan ÖNCE** geçilir. Sonra geçilseydi, çağrı sırasındaki bir çökme
/// komutu `RECEIVED`'da bırakır; yeniden başlatmada "hiç gönderilmemiş" sanılır ve **ikinci kez
/// gönderilir**. §12.2/1'in ("yazılmadan onay yok") kardeş kuralı budur.
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly CommandStore _store;
    private readonly ITerminalTransport _transport;
    private readonly ClockOffset _clock;
    private readonly RecoveryPolicy _recovery;

    public AgentOrchestrator(
        CommandStore store, ITerminalTransport transport, ClockOffset clock, RecoveryPolicy? recovery = null)
    {
        _store = store;
        _transport = transport;
        _clock = clock;
        _recovery = recovery ?? new RecoveryPolicy();
    }

    /// <summary>Komutu işler. Aynı <c>CommandId</c> ile ikinci çağrı terminale GİTMEZ.</summary>
    public async Task<AgentOutcome> HandleAsync(SaleRequest req, long expiresAt, CancellationToken ct = default)
    {
        // ── 1. ATOMİK KAYIT (§12.2/1, /2) — yazılmadan hiçbir şey yapılmaz ─────────
        var saved = _store.Save(req.CommandId, req.PaymentId, req.TerminalId, expiresAt);
        if (saved is SaveResult.Duplicate dup)
            return await HandleDuplicateAsync(dup.Command, req, ct);

        // ── 2. SAAT (§5.3) — offset yoksa TAHMİN YOK ──────────────────────────────
        var expired = _clock.IsExpired(expiresAt);
        if (expired is null)
        {
            _store.Advance(req.CommandId, CommandState.RECEIVED, CommandState.REJECTED);
            return new AgentOutcome(AgentDecision.ClockUnsynced, CommandState.REJECTED,
                Note: "CLOCK_UNSYNCED — sunucu saat offseti bilinmiyor");
        }
        if (expired.Value)
        {
            _store.Advance(req.CommandId, CommandState.RECEIVED, CommandState.EXPIRED);
            return new AgentOutcome(AgentDecision.Expired, CommandState.EXPIRED);
        }

        // ── 3. GÖNDERİM — durum ÖNCE yazılır (çökme güvenliği) ────────────────────
        if (!_store.Advance(req.CommandId, CommandState.RECEIVED, CommandState.SENT_TO_TERMINAL))
        {
            // Yarış: başka bir iş parçacığı aynı komutu ilerletmiş. Kendi başımıza gönderemeyiz.
            var current = _store.Read(req.CommandId)!;
            return await ResolveAsync(current, "eşzamanlı ilerletme — durum başkası tarafından değişti", ct);
        }

        var result = await _transport.SaleAsync(req, ct);
        return await ApplyAsync(req.CommandId, result, ct);
    }

    /// <summary>Terminal sonucunu duruma çevirir.</summary>
    private async Task<AgentOutcome> ApplyAsync(string commandId, TransportResult result, CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case TransportOutcome.Approved:
            case TransportOutcome.Declined:
                // Kesin sonuç — terminal cevapladı.
                _store.Advance(commandId, CommandState.SENT_TO_TERMINAL, CommandState.COMPLETED,
                    terminalReference: result.Rrn, resultJson: Serialize(result));
                return new AgentOutcome(
                    result.Outcome == TransportOutcome.Approved ? AgentDecision.Approved : AgentDecision.Declined,
                    CommandState.COMPLETED, result);

            case TransportOutcome.Busy:
            case TransportOutcome.TicketAlreadyOpen:
            case TransportOutcome.Unknown:
            default:
                // BELİRSİZ — hepsi AYNI yola girer: terminale sor. Varsayım yok.
                _store.Advance(commandId, CommandState.SENT_TO_TERMINAL, CommandState.UNKNOWN);
                var current = _store.Read(commandId)!;
                return await ResolveAsync(current, $"transport={result.Outcome}", ct);
        }
    }

    /// <summary>
    /// Belirsizliği **terminale sorarak** çözer — yerel duruma güvenilmez (§8.3a/2).
    ///
    /// <para><b>Hemen sorulmaz.</b> Sahada ölçülen iki vakada da ilk sorgu `RECV_BUSY` aldı ve
    /// 6.5 saniyeyi boşa harcadı; terminal kart işlemini bitirmek için ~25–30 sn daha meşgul
    /// kalıyor. Gecikme <see cref="RecoveryPolicy"/>'dedir ve tahmin değil ölçümdür.</para>
    /// </summary>
    private async Task<AgentOutcome> ResolveAsync(StoredCommand cmd, string note, CancellationToken ct)
    {
        TicketState? ticket = null;
        string? sonHata = null;

        for (var deneme = 0; deneme < _recovery.MaxAttempts; deneme++)
        {
            await _recovery.Sleep(_recovery.DelayFor(deneme), ct);
            try
            {
                ticket = await _transport.ReadTicketAsync(ct);
                break;
            }
            catch (TerminalBusyException e)
            {
                // Terminal hâlâ meşgul — beklenen durum, tekrar sor. ASLA ödemeyi tekrar gönderme.
                sonHata = e.Message;
            }
            catch (Exception e)
            {
                // Terminale ulaşılamıyor → GERÇEK belirsizlik. Tahmin yok, SALE tekrarı yok.
                return new AgentOutcome(AgentDecision.Unresolved, cmd.State,
                    Note: $"{note}; terminale ulaşılamadı: {e.Message}");
            }
        }

        if (ticket is null)
        {
            // Sorgu bütçesi tükendi. **Tahmin edilmez** — insana gider.
            return new AgentOutcome(AgentDecision.Unresolved, cmd.State,
                Note: $"{note}; {_recovery.MaxAttempts} denemede terminal cevap vermedi ({sonHata})");
        }

        if (ticket.IsFullyPaid)
        {
            // Para HAREKET ETTİ. Belirsizlik kesin sonuca indi.
            var result = new TransportResult(TransportOutcome.Approved,
                ApprovedAmountMinor: ticket.PaidAmountMinor, Rrn: ticket.Rrn, CardLast4: ticket.CardLast4);
            _store.Advance(cmd.CommandId, cmd.State, CommandState.COMPLETED,
                terminalReference: ticket.Rrn, resultJson: Serialize(result));
            return new AgentOutcome(AgentDecision.Approved, CommandState.COMPLETED, result,
                Note: $"{note}; terminalden doğrulandı");
        }

        if (!ticket.HasOpenTicket)
        {
            // Açık fiş YOK → komut terminale ulaşmadı, para hareket etmedi. **Doğrulandı**, varsayılmadı.
            // Durum `UNKNOWN`'da bırakılır: aynı `commandId` ile tekrar gelirse burada tekrar çözülür.
            return new AgentOutcome(AgentDecision.RetryLater, cmd.State,
                Note: $"{note}; terminalde açık fiş yok — güvenle tekrar denenebilir");
        }

        // Açık fiş VAR ama tam ödenmemiş. Sahada bu KISMİ ödeme olarak gerçekleşti (3000 fişe 1000):
        // fiş açık bırakıldı, kalan ikinci bir ödemeyle kapandı ve çift tahsilat OLMADI. Bu yüzden
        // burada fişi kendi başımıza kapatmayız — tahsil edilmiş tutarı bildirip kararı yukarı bırakırız.
        return new AgentOutcome(AgentDecision.Unresolved, cmd.State,
            Note: $"{note}; fiş açık, {ticket.PaidAmountMinor}/{ticket.TotalAmountMinor} tahsil edildi — kalan ödeme gerekiyor");
    }

    /// <summary>
    /// Tekrar gelen komut. **Terminal ÇAĞRILMAZ** (§12.2/3) — kesin sonuç saklıysa replay edilir,
    /// değilse terminale sorulur. "Atla" demek güvenli değildir (§8.3a/2).
    /// </summary>
    private async Task<AgentOutcome> HandleDuplicateAsync(StoredCommand stored, SaleRequest req, CancellationToken ct)
    {
        if (stored.State.IsFinal())
        {
            var decision = stored.State == CommandState.COMPLETED ? AgentDecision.Replayed
                : stored.State == CommandState.EXPIRED ? AgentDecision.Expired
                : AgentDecision.Declined;
            return new AgentOutcome(decision, stored.State, Note: "saklanan sonuç replay edildi");
        }
        return await ResolveAsync(stored, "tekrar gelen komut, uçuşta", ct);
    }

    /// <summary>Sonucu saklanabilir hâle getirir. **Kart verisi TAŞIMAZ** (§12.3).</summary>
    private static string Serialize(TransportResult r) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            outcome = r.Outcome.ToString(),
            approvedAmountMinor = r.ApprovedAmountMinor,
            rrn = r.Rrn,
            approvalCode = r.ApprovalCode,
            cardLast4 = r.CardLast4,   // 4 hane — PAN DEĞİL
            scheme = r.Scheme,
            providerResultCode = r.ProviderResultCode,
        });
}
