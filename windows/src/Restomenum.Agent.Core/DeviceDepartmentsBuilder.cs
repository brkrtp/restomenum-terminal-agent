using System.Text.Json;

namespace Restomenum.Agent.Core;

/// <summary>
/// Cihazın iki GMP tablosunu birleştirip platforma bildirilecek <see cref="DeviceDepartment"/> listesini
/// üretir: <c>GetDepartments</c> (ad + <c>u8TaxIndex</c>, indeks = dizi konumu) + <c>GetTaxRates</c>
/// (<c>taxRate</c> baz puan, indeks = dizi konumu). Departmanın <c>u8TaxIndex</c>'i vergi tablosunda
/// karşılık BULAMAZSA oran <c>null</c> — <b>ASLA uydurma 0</b>. Doğrulanamayan oran doğrulanmış sayılmamalı;
/// plugin null'da eşlemeyi engeller, §30.12 de null-oranı atlar. Vergi tablosu hiç okunamazsa TÜM oranlar
/// null olur (güvenli fail: hepsi engellenir), sessizce yanlış oran değil.
/// </summary>
public static class DeviceDepartmentsBuilder
{
    public static IReadOnlyList<DeviceDepartment> FromGmp(string departmentsJson, string taxRatesJson)
    {
        // taxRates: dizi konumu → baz puan. %0 geçerli bir orandır (0 ≠ null): tabloda VARSA 0 döner.
        var rates = new List<int>();
        try
        {
            using var td = JsonDocument.Parse(taxRatesJson);
            if (td.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var t in td.RootElement.EnumerateArray())
                    rates.Add(t.TryGetProperty("taxRate", out var tr) && tr.ValueKind == JsonValueKind.Number && tr.TryGetInt32(out var v) ? v : 0);
        }
        catch (JsonException) { /* boş → tüm oranlar null (güvenli) */ }

        var result = new List<DeviceDepartment>();
        try
        {
            using var dd = JsonDocument.Parse(departmentsJson);
            if (dd.RootElement.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var d in dd.RootElement.EnumerateArray())
                {
                    var name = d.TryGetProperty("szDeptName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    int? rate = null;
                    if (d.TryGetProperty("u8TaxIndex", out var ti) && ti.ValueKind == JsonValueKind.Number
                        && ti.TryGetInt32(out var taxIdx) && taxIdx >= 0 && taxIdx < rates.Count)
                        rate = rates[taxIdx];   // tabloda karşılığı VAR (0 dâhil); yoksa null
                    result.Add(new DeviceDepartment(index, name, rate));
                    index++;
                }
            }
        }
        catch (JsonException) { /* bozuk → boş liste */ }

        return result;
    }
}
