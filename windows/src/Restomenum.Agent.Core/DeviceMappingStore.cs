namespace Restomenum.Agent.Core;

/// <summary>Ajanın o an geçerli cihaz eşlemesini tutan depo. Pull istemcisi tazeleyince canlı güncellenir
/// (çözümleyiciler bir sonraki satışta yeni eşlemeyi görür — satış anında ÇEKME yok, sadece OKUMA).</summary>
public interface IDeviceMappingStore
{
    DeviceMapping? Current { get; }
    /// <summary>En son yüklenen sürüm; <c>null</c> = hiç yüklenmedi (ne dosya ne fetch). If-None-Match için.</summary>
    int? CurrentVersion { get; }
    /// <summary>Kullanılabilir eşleme var mı: yüklenmiş VE <c>version&gt;0</c>. <c>false</c> → SETUP_INCOMPLETE
    /// (kurulum tamamlanmamış), eşlenmemiş ürün/yöntemden AYRI teşhis.</summary>
    bool IsConfigured { get; }
    /// <summary>Yeni eşlemeyi yerleştir + ham gövdeyi diske yaz (yeniden başlatmada geri yüklenir).</summary>
    void Update(DeviceMapping mapping, string rawJson);
}

/// <summary>
/// <see cref="IDeviceMappingStore"/> + <b>çözümleyici</b>: aynı depo hem eşlemeyi tutar hem
/// <see cref="ILineDepartmentResolver"/>/<see cref="IPaymentMethodResolver"/> olarak çözer. Böylece pull
/// güncellemesi tek yerden bütün satışlara yansır. Çözüm için ön-hesaplı sözlükler (ürün/kategori→dept,
/// dept→oran) atomik değiştirilir. Kalıcılık: ham gövde dosyaya yazılır; açılışta okunup <see
/// cref="DeviceMappingParser"/> ile yeniden çözülür (aynı şekil, tek doğruluk kaynağı parser).
/// </summary>
public sealed class DeviceMappingStore : IDeviceMappingStore, ILineDepartmentResolver, IPaymentMethodResolver
{
    private readonly string? _persistPath;
    private readonly Action<string, object?> _log;
    private volatile Resolved? _resolved;

    private sealed record Resolved(
        DeviceMapping Mapping,
        IReadOnlyDictionary<string, int> ProductToDept,
        IReadOnlyDictionary<string, int> CategoryToDept,
        IReadOnlyDictionary<int, int> DeptRate);

    public DeviceMappingStore(string? persistPath = null, Action<string, object?>? log = null)
    {
        _persistPath = persistPath;
        _log = log ?? ((_, _) => { });
        LoadFromDisk();
    }

    public DeviceMapping? Current => _resolved?.Mapping;
    public int? CurrentVersion => _resolved?.Mapping.Version;
    public bool IsConfigured => _resolved is { Mapping.Version: > 0 };

    public void Update(DeviceMapping mapping, string rawJson)
    {
        _resolved = Build(mapping);
        if (!string.IsNullOrEmpty(_persistPath))
        {
            try { File.WriteAllText(_persistPath, rawJson); }
            catch (Exception ex) { _log("[config] eşleme diske yazılamadı (bellekte geçerli)", new { error = ex.Message }); }
        }
        _log("[config] eşleme güncellendi", new { mapping.Version, dept = mapping.Departments.Count, entry = mapping.Entries.Count, pm = mapping.PaymentMethods.Count });
    }

    // ── ILineDepartmentResolver: ürün ÖNCE, sonra kategori; oran departman tablosundan ──
    public DepartmentMatch? Resolve(string? productCode, string? categoryId)
    {
        var r = _resolved;
        if (r is null) return null;
        int? dept = null;
        if (productCode is not null && r.ProductToDept.TryGetValue(productCode, out var byP)) dept = byP;
        else if (categoryId is not null && r.CategoryToDept.TryGetValue(categoryId, out var byC)) dept = byC;
        if (dept is null) return null;
        return new DepartmentMatch(dept.Value, r.DeptRate.TryGetValue(dept.Value, out var rate) ? rate : null);
    }

    // ── IPaymentMethodResolver: yöntem kimliği → cihaz tipi ──
    public int? Resolve(string paymentMethodId)
    {
        var r = _resolved;
        if (r is null || string.IsNullOrEmpty(paymentMethodId)) return null;
        return r.Mapping.PaymentMethods.TryGetValue(paymentMethodId, out var t) ? t : null;
    }

    private static Resolved Build(DeviceMapping m)
    {
        var product = new Dictionary<string, int>(StringComparer.Ordinal);
        var category = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in m.Entries)
        {
            if (e.Kind == "product" && e.ProductCode is not null) product[e.ProductCode] = e.DepartmentIndex;
            else if (e.Kind == "category" && e.CategoryId is not null) category[e.CategoryId] = e.DepartmentIndex;
        }
        var rate = new Dictionary<int, int>();
        foreach (var d in m.Departments) if (d.TaxRateBasisPoints is int tr) rate[d.Index] = tr;   // null-oran = "bilinmiyor", indekslenmez
        return new Resolved(m, product, category, rate);
    }

    private void LoadFromDisk()
    {
        if (string.IsNullOrEmpty(_persistPath) || !File.Exists(_persistPath)) return;
        try
        {
            var raw = File.ReadAllText(_persistPath);
            if (DeviceMappingParser.Parse(raw) is DeviceMappingParseResult.Ok ok)
            {
                _resolved = Build(ok.Mapping);
                _log("[config] eşleme diskten yüklendi", new { ok.Mapping.Version });
            }
            else _log("[config] kalıcı eşleme dosyası bozuk — boş başlanıyor", null);
        }
        catch (Exception ex) { _log("[config] kalıcı eşleme okunamadı", new { error = ex.Message }); }
    }
}
