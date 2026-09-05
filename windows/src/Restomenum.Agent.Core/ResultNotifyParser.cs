using System.Text.Json;

namespace Restomenum.Agent.Core;

/// <summary>
/// <c>POST /plugin-api/payments/{id}/result</c> yanıtını ayrıştırır. HTTP'den AYRI (test edilebilir).
/// Şekiller peer'de gerçek HTTP ile ölçüldü (e2e 51/51, 760aa8f09).
/// </summary>
public static class ResultNotifyParser
{
    public static NotifyResult Parse(int statusCode, string body)
    {
        string? state = null, reason = null, message = null;
        bool? recorded = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                message = m.GetString();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("recorded", out var rec) &&
                    (rec.ValueKind == JsonValueKind.True || rec.ValueKind == JsonValueKind.False))
                    recorded = rec.GetBoolean();
                if (data.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String)
                    state = st.GetString();
                if (data.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String)
                    reason = rs.GetString();
            }
        }
        catch (JsonException) { /* gövde JSON değil — durum koduna göre karar verilir */ }

        var outcome = statusCode switch
        {
            200 => recorded == false ? NotifyOutcome.Superseded : NotifyOutcome.Recorded,
            400 => NotifyOutcome.Rejected,
            404 => NotifyOutcome.NotFound,
            409 => NotifyOutcome.Conflict,
            429 => NotifyOutcome.RateLimited,
            // 5xx / 401 / beklenmedik → ağ/geçici: outbox'ta kalsın, tekrar denensin.
            _ => NotifyOutcome.NetworkError,
        };

        return new NotifyResult(outcome, state, reason, statusCode, message ?? "");
    }
}
