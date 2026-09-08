namespace Restomenum.Agent.Core;

/// <summary>
/// Cihazı <b>işsizken</b> tek başına kullanma hakkı. Meşgulse <c>null</c> döner ve <b>BEKLEMEZ</b>:
/// bakım işi satışı asla geciktirmez (değişmez #4 — terminal başına tek işlem).
/// </summary>
public interface IDeviceIdleGate
{
    /// <summary>Cihaz boştaysa kilidi alır; meşgulse <c>null</c> (bekleme YOK).</summary>
    IDisposable? TryAcquire();
}

/// <summary>
/// Cihazdan okunan son kimlik + kurulu ödeme uygulamaları. Eşleşmede doldurulur, bakım turunda okunur.
///
/// <para><b>Neden var:</b> <c>POST /api/device/departments</c> gövdesinde <c>deviceInfo</c> ve
/// <c>paymentApplications</c> alanları da gidiyor. Bunları <c>null</c> göndermek "okuyamadım" değil
/// "yok" anlamına gelebilir (gövde <c>null</c>'ları AYNEN serialize ediyor) — panelde çalışan
/// bankaları sildirme riski. Bu yüzden son OKUNAN değer saklanır ve tazeleme turunda aynen
/// yeniden gönderilir: "değişmedi" demek, "bilmiyorum" demekten güvenli.</para>
/// </summary>
public sealed class DeviceIdentityHolder
{
    private readonly object _kilit = new();
    private GmpDeviceInfo? _bilgi;
    private string? _uygulamalar;

    public void Kaydet(GmpDeviceInfo? bilgi, string? uygulamalarJson)
    {
        lock (_kilit)
        {
            if (bilgi is not null) _bilgi = bilgi;
            if (uygulamalarJson is not null) _uygulamalar = uygulamalarJson;
        }
    }

    public (GmpDeviceInfo? Bilgi, string? Uygulamalar) Oku()
    {
        lock (_kilit) return (_bilgi, _uygulamalar);
    }
}

/// <summary>Bir tazeleme turunun sonucu — günlük ve test için tek kelimeyle ne olduğu.</summary>
public enum DepartmentRefreshOutcome
{
    /// <summary>Cihaz meşguldü; hiç okunmadı. Satış önceliklidir.</summary>
    DeviceBusy,
    /// <summary>Okuma başarısız (rc≠0). Eski tablo korundu, bildirim YOK.</summary>
    ReadFailed,
    /// <summary>Okuma başarılı ama tablo BOŞ. Bildirim YOK — boş liste platformdaki tabloyu silerdi.</summary>
    ReadEmpty,
    /// <summary>Cihaz tablosu platformdakiyle aynı. Bildirim YOK (gereksiz sürüm artışı olmaz).</summary>
    Unchanged,
    /// <summary>Fark bulundu ve C2 ile bildirildi.</summary>
    Reported,
    /// <summary>Fark bulundu ama bildirim reddedildi/hata verdi. Bir sonraki turda yeniden denenir.</summary>
    ReportFailed,
}

/// <summary>
/// <b>W56 — cihaz bölüm tablosunun periyodik tazelenmesi.</b>
///
/// <para><b>Neden var (sahada ölçüldü, 2026-09-09 00:51):</b> tabloyu cihazdan okuyup platforma
/// bildiren tek yol eşleşmeydi (<c>--pair</c> / <c>--payment-apps</c> / <c>--void</c> /
/// <c>--cancel-ticket</c>). Kullanıcı terminalde "DIGER" bölümünün KDV'sini %0'dan %10'a çekti ve
/// ajan, biri yeniden eşleştirene kadar eski oranı DOĞRU sandı. Mali fiş yine doğru basılır (oranı
/// cihaz uygular, <c>taxRate</c> telde zaten 0) ama <b>K-30 KDV çelişki raporu yanlış olur</b>:
/// eski orana göre ya hayalet çelişki bildirilir ya da gerçek çelişki kaçırılır.</para>
///
/// <para><b>Ne yapar:</b> yoklama turunda cihaz <b>boştaysa</b> tabloyu okur, platformdaki tabloyla
/// karşılaştırır ve <b>yalnız farklıysa</b> bildirir. Aynıysa bildirmez — her turda bildirmek
/// eşleme sürümünü boşuna artırır ve "sürüm değişti" sinyalini anlamsızlaştırırdı.</para>
///
/// <para><b>Neyi ASLA yapmaz:</b> okuyamadığı ya da boş okuduğu bir tabloyu bildirmez. Üçü ayrı
/// durumdur — okunamadı ≠ boş ≠ dolu — ve ikisi de eski tabloyu olduğu yerde bırakır. Yanlış
/// bildirmektense bildirmemek: boş bir liste platformdaki tabloyu KIRPAR ve eşleme ekranı çalışmaz
/// hâle gelir.</para>
/// </summary>
public sealed class DeviceDepartmentRefresher
{
    private readonly IGmpWrapper _gmp;
    private readonly DeviceConfigClient _client;
    private readonly IDeviceMappingStore _store;
    private readonly IDeviceIdleGate _kapi;
    private readonly DeviceIdentityHolder _kimlik;
    private readonly Action<string, object?> _log;

    public DeviceDepartmentRefresher(
        IGmpWrapper gmp, DeviceConfigClient client, IDeviceMappingStore store,
        IDeviceIdleGate kapi, DeviceIdentityHolder kimlik, Action<string, object?>? log = null)
    {
        _gmp = gmp;
        _client = client;
        _store = store;
        _kapi = kapi;
        _kimlik = kimlik;
        _log = log ?? ((_, _) => { });
    }

    public async Task<DepartmentRefreshOutcome> RefreshAsync(CancellationToken ct = default)
    {
        var kilit = _kapi.TryAcquire();
        if (kilit is null)
        {
            _log("[config] bölüm tablosu tazelemesi ATLANDI — cihaz meşgul", null);
            return DepartmentRefreshOutcome.DeviceBusy;
        }

        IReadOnlyList<DeviceDepartment> cihaz;
        try
        {
            var drc = _gmp.GetDepartments(out var deptJson);
            if (!drc.Ok)
            {
                // "Okunamadı" ≠ "boş". Eski tablo GEÇERLİ kalır; bildirim yok.
                _log("[config] bölüm tablosu OKUNAMADI — eski tablo korunuyor", new { rc = drc.Code });
                return DepartmentRefreshOutcome.ReadFailed;
            }

            // Vergi tablosu okunamazsa TÜM oranlar null olur (builder'ın güvenli davranışı). Böyle bir
            // liste platformdaki gerçek oranların üzerine yazılırsa her ürün eşlemesi engellenir —
            // bu yüzden oran tablosu okunamadığında da bildirmiyoruz.
            var trc = _gmp.GetTaxRates(out var taxJson);
            if (!trc.Ok)
            {
                _log("[config] oran tablosu OKUNAMADI — bölüm bildirimi yapılmıyor, eski tablo korunuyor",
                    new { rc = trc.Code });
                return DepartmentRefreshOutcome.ReadFailed;
            }

            cihaz = DeviceDepartmentsBuilder.FromGmp(deptJson, taxJson);
        }
        finally { kilit.Dispose(); }

        if (cihaz.Count == 0)
        {
            _log("[config] bölüm tablosu BOŞ okundu — bildirim YOK (boş liste platformdakini silerdi)", null);
            return DepartmentRefreshOutcome.ReadEmpty;
        }

        var platform = _store.Current?.Departments;
        if (Ayni(cihaz, platform))
            return DepartmentRefreshOutcome.Unchanged;

        var (bilgi, uygulamalar) = _kimlik.Oku();
        _log("[config] cihaz bölüm tablosu DEĞİŞMİŞ — bildiriliyor", new
        {
            cihazAdet = cihaz.Count,
            platformAdet = platform?.Count ?? 0,
            farklar = Farklar(cihaz, platform),
        });

        bool ok;
        try { ok = await _client.ReportDepartmentsAsync(cihaz, bilgi, uygulamalar, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log("[config] bölüm bildirimi hata verdi — sonraki turda yeniden denenir", new { error = e.Message });
            return DepartmentRefreshOutcome.ReportFailed;
        }

        return ok ? DepartmentRefreshOutcome.Reported : DepartmentRefreshOutcome.ReportFailed;
    }

    /// <summary>İndeks, ad ve oran — üçü de aynıysa aynı. Oranda <c>null</c> ile <c>0</c> AYRI şeydir
    /// (<c>null</c> = bilinmiyor, <c>0</c> = %0) ve bu karşılaştırma ikisini karıştırmaz.</summary>
    private static bool Ayni(IReadOnlyList<DeviceDepartment> a, IReadOnlyList<DeviceDepartment>? b)
    {
        if (b is null || a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Index != b[i].Index
                || !string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)
                || a[i].TaxRateBasisPoints != b[i].TaxRateBasisPoints)
                return false;
        return true;
    }

    /// <summary>Günlüğe yazılacak insan-okur fark özeti. Kişisel veri yok; yalnız indeks/ad/oran.</summary>
    private static string Farklar(IReadOnlyList<DeviceDepartment> cihaz, IReadOnlyList<DeviceDepartment>? platform)
    {
        var eski = platform?.ToDictionary(d => d.Index) ?? new Dictionary<int, DeviceDepartment>();
        var satirlar = new List<string>();
        foreach (var d in cihaz)
        {
            if (!eski.TryGetValue(d.Index, out var o)) { satirlar.Add($"{d.Index}:YENİ({d.Name}={Oran(d.TaxRateBasisPoints)})"); continue; }
            if (o.TaxRateBasisPoints != d.TaxRateBasisPoints)
                satirlar.Add($"{d.Index}:{Oran(o.TaxRateBasisPoints)}→{Oran(d.TaxRateBasisPoints)}");
            else if (!string.Equals(o.Name, d.Name, StringComparison.Ordinal))
                satirlar.Add($"{d.Index}:ad '{o.Name}'→'{d.Name}'");
        }
        foreach (var o in eski.Values)
            if (cihaz.All(d => d.Index != o.Index)) satirlar.Add($"{o.Index}:SİLİNDİ");
        return satirlar.Count == 0 ? "(sıra/adet farkı)" : string.Join(", ", satirlar);
    }

    private static string Oran(int? bp) => bp is null ? "null" : bp.Value.ToString();
}
