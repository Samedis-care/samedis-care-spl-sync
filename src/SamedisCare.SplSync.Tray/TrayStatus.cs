namespace SamedisCare.SplSync.Tray;

public enum SyncState { Idle, Running, Ok, Warn, Error }

/// <summary>
/// Mutable status passed by reference from the SyncHost to the views.
/// Not thread-safe — callers should marshal to the UI thread via Dispatcher.
/// </summary>
public class TrayStatus
{
    public SyncState State { get; set; } = SyncState.Idle;
    public string Message { get; set; } = "Bereit.";
    public DateTimeOffset? LastRun { get; set; }
    public string LastTenant { get; set; } = "";
    public List<string> RecentErrors { get; } = new();
}
