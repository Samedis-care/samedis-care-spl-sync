namespace SamedisCare.SplSync.Core.Actimed;

/// <summary>One A3_CUST row (= a customer / "Mandant" of the testing service provider).</summary>
public record ActimedCustomer(
    int CustId,
    string Short,
    string CustNo,
    string Name1,
    string? Name2)
{
    /// <summary>Friendly label for UI dropdowns: "ID — Name1 (CUST_NO)".</summary>
    public string Label =>
        string.IsNullOrWhiteSpace(CustNo)
            ? $"{CustId} — {Name1}"
            : $"{CustId} — {Name1}  ({CustNo})";
}

/// <summary>One A3_DEV row, the relevant subset.</summary>
public record ActimedDevice(
    int DevId,
    int CustId,
    string InventoryNo,
    string SerialNo,
    int DevTypeId,
    int? LocationId,
    int? StatusId,
    int? NextActivityId,
    string? Memo);

/// <summary>One A3_DEV_TYPE row.</summary>
public record ActimedDeviceType(
    int DevTypeId,
    int ManuId,
    int DevKindId,
    string Name,
    string Modell);

/// <summary>One A3_DEV_KIND row (= "Geräteart" / DIMDI category).</summary>
public record ActimedDeviceKind(
    int DevKindId,
    string Name,
    string? DimdiNr);

/// <summary>One A3_MANUF row.</summary>
public record ActimedManufacturer(
    int ManuId,
    string Name1,
    string? Name2);

/// <summary>One A3_LOCATION row.</summary>
public record ActimedLocation(
    int LocationId,
    string Name);

/// <summary>One A3_ACTIVITY_KIND row (= maintenance category like "MPBe_§11_STK/DGUV V3").</summary>
public record ActimedActivityKind(
    int KindId,
    string Name,
    string? Description);

/// <summary>One A3_ACTIVITY row (= concrete planned check linked to a TEST_SPEC).</summary>
public record ActimedActivity(
    int ActivityId,
    int TestSpecId,
    int KindId,
    int IntervalMonths,
    string Name);

/// <summary>
/// One TEST_SPEC row (= Prüfvorschrift / "recipe"). TEST_SPEC_ID=1 is the built-in
/// "Unbekannt" placeholder — an activity pointing at it has no test steps and opens an
/// empty check dialog in Actimed, so the sync never creates activities with that id.
/// </summary>
public record ActimedTestSpec(
    int TestSpecId,
    string Name)
{
    public const int UnknownId = 1;
    public bool IsUnknown => TestSpecId == UnknownId;
}

/// <summary>One A3_IS_ACT_DEV row (= scheduled check assignment for one device).</summary>
public record ActimedIsActDev(
    int DevId,
    int ActivityId,
    DateTime? Next,
    DateTime? Last,
    int TesterId);

/// <summary>One A3_FINISHED_TEST row, header.</summary>
public record ActimedFinishedTest(
    string TestId,
    int DevId,
    DateTime TestDate,
    string TesterName,
    string PvsName,
    string Pruefberichtsnummer,
    string Pruefergebnis,
    DateTime? LastTestDate,
    DateTime? NextTestDate,
    string? Memo,
    DateTime ModifyTime);

/// <summary>One A3_FINISHED_TEST_ITEM row (= test step).</summary>
public record ActimedFinishedTestItem(
    string TestId,
    string TestItemId,
    int ItemId,
    string WsDscr,
    string FuncName,
    bool Success);

/// <summary>One A3_FINISHED_TEST_ITEM_RESULT row (= measurement value).</summary>
public record ActimedFinishedTestResult(
    string TestItemId,
    int ResultNo,
    string ItemDscr,
    string? Unit,
    string? Value,
    string? Limit1,
    string? Limit2,
    bool Success);
