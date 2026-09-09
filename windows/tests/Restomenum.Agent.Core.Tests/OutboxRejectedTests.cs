using Microsoft.Data.Sqlite;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W68 — <b>kalıcı ret damgası</b>: platform gövdeyi <c>400</c> ile reddettiyse kayıt SİLİNMEZ.
///
/// <para><b>Neden (sahada ölçüldü, 2026-09-09 14:42):</b> başarılı bir fiş iptalinin bildirimi
/// <c>400 plugin.payment.invalidResult</c> aldı. Kod 400'ü "kesin sonuç" sayıp kaydı sildi;
/// <c>Depth()</c> <b>0</b> gösterdi. Yani <b>fiş cihazda iptal edilmişken platform bunu hiç
/// öğrenmedi</b> ve kuyruk "her şey yolunda" diyordu. Sayının kendisi yanıltıcıydı ve ıraksama
/// saatlerce fark edilmedi.</para>
///
/// <para><b>Kök kusur:</b> tek bir bayrağa (<c>IsFinal</c>) iki ayrı soru sorduruluyordu —
/// "tekrar denemek anlamlı mı" ve "kayıt silinsin mi". Aynı desen bu kod tabanında üçüncü kez
/// çıktı (W65'te <c>acikRet</c>, W59'da yaş sınırı sayımı).</para>
/// </summary>
public class OutboxRejectedTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"obxr_{Guid.NewGuid():N}.db");
    private readonly Outbox _outbox;
    private const long T0 = 1_700_000_000_000;

    public OutboxRejectedTests() => _outbox = Outbox.Open(_db);

    public void Dispose()
    {
        _outbox.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    private void Yaz(string eid) =>
        _outbox.Enqueue(eid, "pay_1", OutboxKinds.TicketCancel, """{"x":1}""", "");

    [Fact]
    public void Damgalanan_kayit_SILINMEZ_ama_kuyruktan_DUSER()
    {
        // ← ÇİVİ: ikisi birden olmalı. Silinirse defter boşluğunun izi kaybolur; kuyrukta kalırsa
        // her turda aynı 400'ü yiyen bir fırtına kurulur (409'da 252 turluk örneğini izledik).
        Yaz("e1");
        _outbox.MarkPermanentlyRejected("e1", T0);

        Assert.Empty(_outbox.Pending(ignoreBackoff: true));      // ← ÇİVİ: kuyrukta YOK
        var damgali = Assert.Single(_outbox.Rejected());          // ← ÇİVİ: diskte VAR
        Assert.Equal("e1", damgali.EventId);
        Assert.Equal("""{"x":1}""", damgali.PayloadJson);         // gövde de duruyor — yeniden gönderilebilir
    }

    [Fact]
    public void Depth_damgalilari_SAYMAZ_RejectedCount_sayar()
    {
        // ← ÇİVİ: iki ayrı olgu, iki ayrı sayı. Tek sayıda toplasaydık "3" hem "üç bildirim yolda"
        // hem "üç bildirim asla gitmeyecek" demek olurdu — 14:42'de tam olarak bu belirsizlik
        // yüzünden ıraksama görünmedi.
        Yaz("e1"); Yaz("e2"); Yaz("e3");
        _outbox.MarkPermanentlyRejected("e2", T0);

        Assert.Equal(2, _outbox.Depth());
        Assert.Equal(1, _outbox.RejectedCount());
    }

    [Fact]
    public void Damga_SESSIZCE_kalmaz_sayilabilir()
    {
        // Açılış uyarısının dayanağı bu sayı; sıfırsa uyarı da yok, gürültü yok.
        Assert.Equal(0, _outbox.RejectedCount());
        Yaz("e1");
        _outbox.MarkPermanentlyRejected("e1", T0);
        Assert.Equal(1, _outbox.RejectedCount());
    }

    [Fact]
    public void Ayni_kayit_IKI_KEZ_damgalanmaz()
    {
        Yaz("e1");
        Assert.True(_outbox.MarkPermanentlyRejected("e1", T0));
        Assert.False(_outbox.MarkPermanentlyRejected("e1", T0 + 1));   // zaten damgalı
        Assert.Equal(1, _outbox.RejectedCount());
    }

    [Fact]
    public void Normal_kayitlar_ETKILENMEZ()
    {
        // ← ÇİVİ: damga yalnız damgalanana. Bir süzgeç hatası bütün kuyruğu görünmez yapardı ve
        // hiçbir bildirim gitmezdi — sessiz ve tam kayıp.
        Yaz("e1"); Yaz("e2");
        _outbox.MarkPermanentlyRejected("e1", T0);

        var bekleyen = _outbox.Pending(ignoreBackoff: true);
        Assert.Single(bekleyen);
        Assert.Equal("e2", bekleyen[0].EventId);
    }

    [Fact]
    public void Confirm_hala_SILER()
    {
        // Gerçekten yazılan sonuç silinmeye devam etmeli; W68 yalnız 400 dalını değiştirdi.
        Yaz("e1");
        Assert.True(_outbox.Confirm("e1"));
        Assert.Equal(0, _outbox.Depth());
        Assert.Equal(0, _outbox.RejectedCount());
    }

    // ── Karar ayrımı: "tekrar anlamlı mı" ≠ "kayıt silinsin mi" ─────────────────

    [Theory]
    [InlineData(NotifyOutcome.Rejected, true)]
    [InlineData(NotifyOutcome.Conflict, false)]
    [InlineData(NotifyOutcome.NotFound, false)]
    [InlineData(NotifyOutcome.Recorded, false)]
    [InlineData(NotifyOutcome.Superseded, false)]
    [InlineData(NotifyOutcome.NetworkError, false)]
    [InlineData(NotifyOutcome.RateLimited, false)]
    public void IsPermanentGap_YALNIZ_400(NotifyOutcome sonuc, bool beklenen)
    {
        // ← ÇİVİ: kapsamı genişletmek çöp biriktirir, daraltmak boşluğu görünmez yapar.
        // 409 = platformun o denemeye ait KENDİ nihai sonucu var → boşluk yok.
        // 404 = ödeme bu cihazın değil ya da hiç yok → saklamak anlamsız.
        // 400 = platform HİÇBİR ŞEY yazmadı ve kendiliğinden yazmayacak → tek gerçek boşluk.
        var r = new NotifyResult(sonuc, null, null, 0, "");
        Assert.Equal(beklenen, r.IsPermanentGap);
    }

    [Fact]
    public void IsFinal_ile_IsPermanentGap_AYRI_sorular()
    {
        // ← ÇİVİ (yapısal): 400 için ikisi de "true" değil — `IsFinal` "tekrar deneme" der,
        // `IsPermanentGap` "ama silme" der. Tek bayrağa indirgenirse W68'in tamamı geri alınır.
        var red = new NotifyResult(NotifyOutcome.Rejected, null, null, 400, "");
        Assert.True(red.IsFinal);           // tekrar denemek anlamsız
        Assert.True(red.IsPermanentGap);    // ama kayıt DURMALI

        var yazildi = new NotifyResult(NotifyOutcome.Recorded, null, null, 200, "");
        Assert.True(yazildi.IsFinal);       // tekrar denemek anlamsız
        Assert.False(yazildi.IsPermanentGap);   // ve kayıt silinmeli
    }
}
