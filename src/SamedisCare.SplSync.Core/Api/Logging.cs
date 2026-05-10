namespace SamedisCare.SplSync.Core.Api;

/// <summary>
/// Lightweight logging facade used by the API layer. Replaces Helper.Message in the reference repo.
/// Wired up to Serilog or Microsoft.Extensions.Logging by the host.
/// </summary>
public interface ISyncLog
{
    /// <summary>Log level: 0=off, 1=info, 2=debug.</summary>
    int Level { get; }

    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
    void Debug(string message);
}

public sealed class ConsoleSyncLog : ISyncLog
{
    public int Level { get; }

    public ConsoleSyncLog(int level = 1) => Level = level;

    public void Info(string message)
    {
        if (Level >= 1) Console.WriteLine($"[INFO ] {DateTime.Now:HH:mm:ss} {message}");
    }

    public void Warn(string message)
    {
        if (Level >= 1) Console.WriteLine($"[WARN ] {DateTime.Now:HH:mm:ss} {message}");
    }

    public void Error(string message, Exception? ex = null)
    {
        Console.Error.WriteLine($"[ERROR] {DateTime.Now:HH:mm:ss} {message}");
        if (ex != null) Console.Error.WriteLine(ex);
    }

    public void Debug(string message)
    {
        if (Level >= 2) Console.WriteLine($"[DEBUG] {DateTime.Now:HH:mm:ss} {message}");
    }
}

/// <summary>No-op logger for tests.</summary>
public sealed class NullSyncLog : ISyncLog
{
    public int Level => 0;
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
    public void Debug(string message) { }
}

/// <summary>
/// Logger that records every message in-memory. Used by the tenant mapping dialog
/// to expose API/DB call diagnostics in the UI without depending on file logs.
/// </summary>
public sealed class RecordingSyncLog : ISyncLog
{
    public int Level { get; }
    public List<(string Severity, string Message, Exception? Ex)> Entries { get; } = new();

    public RecordingSyncLog(int level = 2) => Level = level;

    public void Info(string message)  => Entries.Add(("INFO",  message, null));
    public void Warn(string message)  => Entries.Add(("WARN",  message, null));
    public void Error(string message, Exception? ex = null) => Entries.Add(("ERROR", message, ex));
    public void Debug(string message) { if (Level >= 2) Entries.Add(("DEBUG", message, null)); }

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (sev, msg, ex) in Entries)
        {
            sb.Append('[').Append(sev).Append("] ").AppendLine(msg);
            if (ex != null) sb.Append("    ").AppendLine(ex.Message);
        }
        return sb.ToString();
    }
}

/// <summary>Helpers for redacting secrets in logs.</summary>
public static class Redact
{
    public static string Token(string? value, int keepStart = 3, int keepEnd = 2)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= keepStart + keepEnd) return new string('*', value.Length);
        return string.Concat(value.AsSpan(0, keepStart), new string('*', value.Length - keepStart - keepEnd), value.AsSpan(value.Length - keepEnd));
    }
}
