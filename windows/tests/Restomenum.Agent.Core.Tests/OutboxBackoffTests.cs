using Microsoft.Data.Sqlite;
using Restomenum.Agent.Core;
using Xunit;

namespace Restomenum.Agent.Core.Tests;

/// <summary>
/// W20 — kayıt başına ÜSTEL geri çekilme.
///
/// <para><b>Neden gerekti (ölçüm 2026-09-08):</b> yeniden deneme sabit 30 sn ve sınırsızdı; süzgeç
/// yoktu. Kalıcı bir arızada dakikada 2 istek, sonsuza kadar. K-33 ile bu kayıtlar PARA satırı geri
/// alacağı için kuyrukta bekleme ihtimali arttı: gürültü, uçta yük ve alarmı değersizleştiren log
/// seli.</para>
///
/// <para><b>Ertelemek başka, unutmak başka:</b> vazgeçme yok, silme yok, retention yok. Üst sınır
/// 5 dakika — kayıt sonsuza kadar kuyrukta kalır ve uç düzelince gider.</para>
/// </summary>
public class OutboxBackoffTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"obx_{Guid.NewGuid():N}.db");
    private readonly Outbox _outbox;
    private const long T0 = 1_700_000_000_000;

    public OutboxBackoffTests() => _outbox = Outbox.Open(_db);

    public void Dispose()
    {
        _outbox.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { /* geçici */ }
    }

    private void Yaz(string eid = "e1") => _outbox.Enqueue(eid, "pay_1", OutboxKinds.Result, "{}", "");

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 300)]
    [InlineData(6, 300)]
    [InlineData(50, 300)]     // taşma yok, sınırda kalıyor
    public void Aralik_tablosu(int deneme, int beklenenSaniye)
        => Assert.Equal(beklenenSaniye, (int)Outbox.Bekleme(deneme).TotalSeconds);

    [Fact]
    public void Gelecek_tarihli_kayit_Pendingde_DONMEZ()
    {
        // ← ÇİVİ: süzgeç olmadan geri çekilme yazılmış ama uygulanmamış olurdu.
        Yaz();
        _outbox.MarkAttempt("e1", now: T0);                       // → +30 sn

        Assert.Empty(_outbox.Pending(now: T0 + 29_000));           // vakti GELMEDİ
        Assert.Single(_outbox.Pending(now: T0 + 30_000));          // vakti GELDİ
    }

    [Fact]
    public void Sira_created_at_olarak_KALIYOR()
    {
        // Süzgeç eklendi ama kuyruk sırası değişmedi: en eski önce.
        _outbox.Enqueue("eski", "pay_1", OutboxKinds.Result, "{}", "", now: T0);
        _outbox.Enqueue("yeni", "pay_2", OutboxKinds.Result, "{}", "", now: T0 + 5_000);

        var sirali = _outbox.Pending(now: T0 + 10_000);
        Assert.Equal(new[] { "eski", "yeni" }, sirali.Select(x => x.EventId));
    }

    [Fact]
    public void Baglanti_yeniden_kurulunca_geri_cekilme_ATLANIR()
    {
        // ← ÇİVİ: beklemenin sebebi ağın gitmiş olmasıysa, ağ geri geldiğinde beklemek anlamsız.
        Yaz();
        _outbox.MarkAttempt("e1", now: T0);                        // → +30 sn

        Assert.Empty(_outbox.Pending(now: T0 + 1_000));                          // normal tur
        Assert.Single(_outbox.Pending(now: T0 + 1_000, ignoreBackoff: true));    // yeniden bağlanma
    }

    [Fact]
    public void Yeniden_baslayinca_next_attempt_at_KALICI()
    {
        // ← ÇİVİ: geri çekilme bellekte tutulsaydı her yeniden başlatma onu sıfırlar ve
        // kalıcı arızada istek seli geri gelirdi.
        Yaz();
        _outbox.MarkAttempt("e1", now: T0);
        _outbox.Dispose();
        SqliteConnection.ClearAllPools();

        using var yeniden = Outbox.Open(_db);
        Assert.Empty(yeniden.Pending(now: T0 + 29_000));
        Assert.Single(yeniden.Pending(now: T0 + 30_000));
    }

    [Fact]
    public void MarkAttempt_YENI_sayaci_dondurur()
    {
        // Alarm eşiği (10. denemede ve her 10'da bir) buna dayanıyor.
        Yaz();
        Assert.Equal(1, _outbox.MarkAttempt("e1", now: T0));
        Assert.Equal(2, _outbox.MarkAttempt("e1", now: T0));
        Assert.Equal(3, _outbox.MarkAttempt("e1", now: T0));
    }

    [Fact]
    public void Confirm_edilmis_kayitta_MarkAttempt_patlamaz()
    {
        Yaz();
        _outbox.Confirm("e1");
        Assert.Equal(0, _outbox.MarkAttempt("e1", now: T0));   // kayıt yok → 0, istisna YOK
    }

    [Fact]
    public void ESKI_SEMA_dosyasi_acilista_sutunu_KAZANIR()
    {
        // ← ÇİVİ: sahadaki dosyalarda sütun yok. Açılış kırılırsa ajan hiç başlamaz ve
        // kuyruktaki para kayıtları gönderilemez.
        var eski = Path.Combine(Path.GetTempPath(), $"obx_old_{Guid.NewGuid():N}.db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={eski}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE outbox (
                        event_id TEXT PRIMARY KEY, payment_id TEXT NOT NULL, status TEXT NOT NULL,
                        payload_json TEXT NOT NULL, provider_plugin_id TEXT NOT NULL,
                        created_at INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0)
                    """;
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO outbox VALUES ('eski','pay_1','result','{}','',1,0)";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            using var acilan = Outbox.Open(eski);
            // Varsayılan 0 → eski kayıt HEMEN uygun, geçiş bir bildirimi bile geciktirmiyor.
            Assert.Single(acilan.Pending(now: T0));
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(eski); } catch { } }
    }
}
