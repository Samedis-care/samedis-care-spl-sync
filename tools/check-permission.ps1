# Test whether actimed3db.mdb is opened shared or exclusive.
# Run this in PowerShell while Actimed is open.

$dbPath = "C:\Users\Public\SPL\Data\actimed3db.mdb"

Write-Host ""
Write-Host "=== ldb file BEFORE second connection attempt ==="
$ldb = $dbPath -replace "\.mdb$", ".ldb"
if (Test-Path $ldb) {
    Get-Item $ldb | Select-Object Length, LastWriteTime
} else {
    Write-Host "no .ldb found (Actimed not running?)"
}

Write-Host ""
Write-Host "=== Trying to open a second OleDb connection ==="

$connStr = "Provider=Microsoft.ACE.OLEDB.16.0;Data Source=$dbPath;"
$conn = New-Object System.Data.OleDb.OleDbConnection($connStr)

try {
    $conn.Open()
    Write-Host "OK - SHARED mode. Parallel connection allowed." -ForegroundColor Green

    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT TOP 5 CUST_ID, CUST_NAME1 FROM A3_CUST"
    $rdr = $cmd.ExecuteReader()
    while ($rdr.Read()) {
        "{0,4} | {1}" -f $rdr[0], $rdr[1]
    }
    $rdr.Close()
    $conn.Close()
}
catch {
    Write-Host "EXCLUSIVE mode or lock conflict:" -ForegroundColor Red
    $hex = "0x{0:X8}" -f $_.Exception.HResult
    Write-Host "  HRESULT: $hex"
    Write-Host "  Message: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "=== ldb file AFTER second connection attempt ==="
if (Test-Path $ldb) {
    Get-Item $ldb | Select-Object Length, LastWriteTime
}