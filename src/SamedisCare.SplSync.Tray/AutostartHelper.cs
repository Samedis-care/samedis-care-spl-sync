using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SamedisCare.SplSync.Tray;

/// <summary>
/// Manages a per-user autostart entry under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Per-user (not per-machine) avoids needing admin rights to enable/disable.
///
/// The entry simply launches our EXE; we use the current EXE path
/// (Environment.ProcessPath, .NET 6+). If the user moves the EXE later, the autostart
/// entry will break — same behavior as any "Run" entry.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutostartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SamedisCare.SplSync";

    /// <summary>True if a Run-entry pointing to *some* EXE is registered under our value name.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var existing = key?.GetValue(ValueName) as string;
        return !string.IsNullOrWhiteSpace(existing);
    }

    /// <summary>
    /// Returns the registered command (full quoted path), or null if not registered.
    /// Useful for diagnostics / showing the user what would be auto-started.
    /// </summary>
    public static string? GetEnabledCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>Add or update the autostart entry to point at the given absolute EXE path.</summary>
    public static void Enable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("exePath must not be empty", nameof(exePath));

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                       ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key == null)
            throw new InvalidOperationException($"Could not open or create HKCU\\{RunKeyPath}");

        // Always wrap the path in quotes — handles spaces, parentheses, etc.
        key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
    }

    /// <summary>Remove the autostart entry. No-op if not present.</summary>
    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key == null) return;
        try { key.DeleteValue(ValueName, throwOnMissingValue: false); }
        catch { /* swallow — disable is best-effort */ }
    }

    /// <summary>
    /// Best-effort EXE path discovery. Self-contained single-file builds expose this
    /// via Environment.ProcessPath. Returns null if it cannot be determined.
    /// </summary>
    public static string? CurrentExePath() => Environment.ProcessPath;
}
