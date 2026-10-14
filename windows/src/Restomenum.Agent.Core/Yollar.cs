namespace Restomenum.Agent.Core;

/// <summary>Komut satırı ve yapılandırmadaki göreli yollar, çalışma dizini değişmeden ÖNCE sabitlenir.</summary>
public static class Yollar
{
    public static string? Tam(string? yol, string taban) =>
        string.IsNullOrWhiteSpace(yol) ? null : Path.GetFullPath(yol, taban);
}
