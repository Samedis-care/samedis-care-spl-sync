$conn = New-Object System.Data.OleDb.OleDbConnection("Provider=Microsoft.ACE.OLEDB.16.0;Data Source=C:\Users\Public\SPL\Data\actimed3db.mdb;")
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT CUST_ID, CUST_SHORT, CUST_NAME1, CUST_NAME2 FROM A3_CUST"
$rdr = $cmd.ExecuteReader()
while ($rdr.Read()) { "{0,4} | {1,-12} | {2}" -f $rdr[0], $rdr[1], $rdr[2] }
$conn.Close()