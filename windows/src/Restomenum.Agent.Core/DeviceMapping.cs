using System.Text.Json;

namespace Restomenum.Agent.Core;

/// <summary>
/// Cihaz yapılandırma eşlemesi — Ingenico eklentisinin <c>GET /api/device/mapping</c> yanıtının
/// çözülmüş hâli (üretim config kanalı, §20.2 / §20-I). Departman tablosu + ürün/kategori→departman
/// girdileri + ödeme yöntemi→cihaz tipi eşlemesi. <see cref="Version"/> ORTAK sayaç: departman ya da
/// ödeme eşlemesi değişince artar, tek yoklama ikisini de tazeler. <c>version==0</c> = platformda hiç
/// eşleme yazılmamış (kurulum eksik) — ürün-yok'tan ayrı teşhis.
/// </summary>
public sealed record DeviceMapping(
    int Version,
    IReadOnlyList<DeviceDepartment> Departments,
    IReadOnlyList<MappingEntry> Entries,
    IReadOnlyDictionary<string, int> PaymentMethods);

/// <summary>Cihaz departmanı: indeks + ad + KDV oranı (baz puan). Oran §30.12 doğrulamasının kaynağı —
/// artık elle dosya değil, cihazın <c>GetDepartments</c> çıktısından platforma bildirilip geri gelir.</summary>
public sealed record DeviceDepartment(int Index, string Name, int TaxRateBasisPoints);

/// <summary>Eşleme girdisi: ürün ya da kategori kimliği → departman indeksi. <see cref="Kind"/> "product"
/// ise <see cref="ProductCode"/>, "category" ise <see cref="CategoryId"/> dolu. Ürün girdisi kategoriyi ezer.</summary>
public sealed record MappingEntry(string Kind, string? ProductCode, string? CategoryId, int DepartmentIndex);

/// <summary>Eşleme çözümleme sonucu — ayrıştırma HTTP'den ayrı, gerçek gövdelerle test edilebilsin.</summary>
public abstract record DeviceMappingParseResult
{
    public sealed record Ok(DeviceMapping Mapping) : DeviceMappingParseResult;
    public sealed record Invalid(string Reason) : DeviceMappingParseResult;
}

/// <summary>
/// <c>GET /api/device/mapping</c> (200) gövdesini <see cref="DeviceMapping"/>'e çözer. 304 (değişmedi)
/// HTTP istemcisinde ele alınır — bu ayrıştırıcı yalnız 200 gövdesini görür.
///
/// <para><b>Savunmacı:</b> üst-şekil bozuksa <see cref="DeviceMappingParseResult.Invalid"/> (istemci
/// eski eşlemeyi korur + alarm). Tek tek bozuk girdiler ATLANIR — biri hatalı diye tüm eşleme düşmesin;
/// atlanan girdi o ürün/yöntem için fail-closed'a düşer, sessiz yanlış değil. Bilinmeyen cihaz ödeme
/// tipi (1/4/16 dışı) değeri de elenir (uydurma tip terminale gitmesin).</para>
/// </summary>
public static class DeviceMappingParser
{
    public static DeviceMappingParseResult Parse(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return new DeviceMappingParseResult.Invalid("JSON değil"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new DeviceMappingParseResult.Invalid("kök nesne değil");

            // Platform zarfı: {success, data:{...}}. Uç hem sarmalı (canlı) hem düz (test) olabilir —
            // data nesnesi varsa onu kök al, yoksa root'u kullan.
            var m = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object ? dataEl : root;

            // version: yoksa 0 (kurulum eksik sinyali). Sayı değilse Invalid (şekil bozuk).
            int version = 0;
            if (m.TryGetProperty("version", out var vEl))
            {
                if (vEl.ValueKind != JsonValueKind.Number || !vEl.TryGetInt32(out version))
                    return new DeviceMappingParseResult.Invalid("version sayı değil");
            }

            var departments = new List<DeviceDepartment>();
            if (m.TryGetProperty("departments", out var deptArr) && deptArr.ValueKind == JsonValueKind.Array)
                foreach (var d in deptArr.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    if (!TryInt(d, "index", out var idx) || !TryInt(d, "taxRateBasisPoints", out var rate)) continue;
                    departments.Add(new DeviceDepartment(idx, StrOr(d, "name", ""), rate));
                }

            var entries = new List<MappingEntry>();
            if (m.TryGetProperty("entries", out var entArr) && entArr.ValueKind == JsonValueKind.Array)
                foreach (var e in entArr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!TryInt(e, "departmentIndex", out var deptIndex)) continue;
                    var kind = StrOr(e, "kind", "");
                    var productCode = StrOrNull(e, "productCode");
                    var categoryId = StrOrNull(e, "categoryId");
                    // Kimlik yoksa girdi işe yaramaz — atla.
                    if (kind == "product" && string.IsNullOrEmpty(productCode)) continue;
                    if (kind == "category" && string.IsNullOrEmpty(categoryId)) continue;
                    if (kind != "product" && kind != "category") continue;
                    entries.Add(new MappingEntry(kind, productCode, categoryId, deptIndex));
                }

            var paymentMethods = new Dictionary<string, int>(StringComparer.Ordinal);
            if (m.TryGetProperty("paymentMethods", out var pm) && pm.ValueKind == JsonValueKind.Object)
                foreach (var kv in pm.EnumerateObject())
                {
                    if (kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetInt32(out var t)
                        && GmpPaymentTypes.IsKnown(t))
                        paymentMethods[kv.Name] = t;
                }

            return new DeviceMappingParseResult.Ok(
                new DeviceMapping(version, departments, entries, paymentMethods));
        }
    }

    private static bool TryInt(JsonElement obj, string prop, out int value)
    {
        value = 0;
        return obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }

    private static string StrOr(JsonElement e, string prop, string def) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;

    private static string? StrOrNull(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
