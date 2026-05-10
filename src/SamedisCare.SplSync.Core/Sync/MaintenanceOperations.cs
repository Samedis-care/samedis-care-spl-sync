using SamedisCare.SplSync.Core.Actimed;

namespace SamedisCare.SplSync.Core.Sync;

/// <summary>
/// Wartungs-Operationen, die der Tray-User direkt aus den Settings auslösen kann.
/// Vorteil: Operations muss kein DB Browser, kein Access und keine Shell anfassen — alles
/// laeuft über denselben OleDb-Pfad wie der normale Sync und respektiert daher auch das
/// gleiche Lock-Verhalten (ActimedLockedException → Tray-Dialog "Bitte Actimed schliessen").
///
/// Drei Operationen:
///   * <see cref="DeleteSyncCreatedDevices"/> — DELETE FROM A3_DEV WHERE DEV_Memo LIKE 'created_by=spl-sync%'
///   * <see cref="RepairAllSyncCreatedDevices"/> — laeuft RepairDeviceForeignKeys über alle vom Sync angelegten Devices
///   * <see cref="ResetDownloadCursor"/> — entfernt den Download-Cursor, damit der nächste Sync alles neu zieht
/// </summary>
public class MaintenanceOperations
{
    private readonly IActimedRepository _actimed;
    private readonly StateDb _stateDb;

    public MaintenanceOperations(IActimedRepository actimed, StateDb stateDb)
    {
        _actimed = actimed;
        _stateDb = stateDb;
    }

    /// <summary>
    /// Löscht alle vom Sync angelegten A3_DEV-Einträge für die angegebenen CUST_IDs.
    /// Erkennungsmerkmal: DEV_Memo beginnt mit "created_by=spl-sync".
    /// User-eigene Inventare werden nicht angefasst.
    /// </summary>
    public int DeleteSyncCreatedDevices(IEnumerable<int> custIds)
    {
        if (_actimed is not IMaintenanceCapable capable)
            throw new NotSupportedException(
                "Aktive Repository-Implementierung unterstuetzt keine Wartungs-Operationen. " +
                "Bitte gegen die echte Actimed-MDB über Microsoft.ACE.OLEDB.16.0 fahren.");

        return capable.DeleteSyncCreatedDevices(custIds.ToList());
    }

    /// <summary>
    /// Laeuft alle vom Sync angelegten Devices durch RepairDeviceForeignKeys.
    /// </summary>
    /// <returns>Tuple (Found, Repaired): wieviele Devices als Sync-Eintraege erkannt wurden,
    /// und davon wieviele tatsaechlich repariert (= mindestens ein FK war 0/NULL).</returns>
    public (int Found, int Repaired) RepairAllSyncCreatedDevices(IEnumerable<int> custIds)
    {
        if (_actimed is not IMaintenanceCapable capable)
            throw new NotSupportedException(
                "Aktive Repository-Implementierung unterstuetzt keine Wartungs-Operationen.");

        var ids = capable.GetSyncCreatedDeviceIds(custIds.ToList());
        var found = ids.Count;
        var repaired = 0;
        foreach (var devId in ids)
        {
            if (_actimed.RepairDeviceForeignKeys(devId)) repaired++;
        }
        return (found, repaired);
    }

    /// <summary>
    /// Entfernt den Download-Cursor für einen bestimmten Mandanten — der nächste Sync zieht
    /// dann ab dem Konfig-Default-Datum (2022-01-01) alles erneut.
    /// </summary>
    public void ResetDownloadCursor(string samedisTenantId)
    {
        _stateDb.ResetCursor(samedisTenantId, Cursor.KindDownload);
    }

    /// <summary>
    /// Entfernt zusaetzlich den Upload-Cursor — relevant, wenn der User auch Prüfungen
    /// nochmal hochladen lassen will.
    /// </summary>
    public void ResetUploadCursor(string samedisTenantId)
    {
        _stateDb.ResetCursor(samedisTenantId, Cursor.KindUpload);
    }
}

/// <summary>
/// Optionales Interface für Repository-Implementierungen, die Bulk-Wartungs-Operationen
/// direkt auf der DB ausführen koennen. Nur OleDbActimedRepository implementiert das —
/// die SQLite-Variante ist für Tests da und braucht das nicht.
/// </summary>
public interface IMaintenanceCapable
{
    int DeleteSyncCreatedDevices(IList<int> custIds);
    IReadOnlyList<int> GetSyncCreatedDeviceIds(IList<int> custIds);
}
