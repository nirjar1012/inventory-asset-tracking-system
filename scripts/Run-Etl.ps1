<#
.SYNOPSIS
    Runs the DailyInventoryMovement ETL and prints the recent run log.

.DESCRIPTION
    Default: executes etl.usp_LoadDailyInventoryMovement (the same extract/merge logic the SSIS package calls).
    -UseSsis: runs etl\ssis\YardTracker.ETL\DailyInventoryMovement.dtsx with dtexec, if SSIS is installed.
    Schedule either one with SQL Agent or Windows Task Scheduler, e.g. every 15 minutes.

.EXAMPLE
    .\scripts\Run-Etl.ps1
    .\scripts\Run-Etl.ps1 -UseSsis
#>
[CmdletBinding()]
param(
    [string] $Server = '(localdb)\MSSQLLocalDB',
    [string] $Database = 'YardTracker',
    [switch] $UseSsis
)

$ErrorActionPreference = 'Stop'
$connectionString = "Server=$Server;Database=$Database;Integrated Security=true"

function Invoke-Query([string] $Sql, [int] $Timeout = 600) {
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $connection.Open()
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = $Sql
        $command.CommandTimeout = $Timeout
        $table = New-Object System.Data.DataTable
        $table.Load($command.ExecuteReader())
        return , $table
    }
    finally { $connection.Dispose() }
}

if ($UseSsis) {
    $dtexec = Get-Command dtexec -ErrorAction SilentlyContinue
    if (-not $dtexec) { throw 'dtexec was not found. Install SQL Server Integration Services, or run without -UseSsis.' }

    $package = Join-Path $PSScriptRoot '..\etl\ssis\YardTracker.ETL\DailyInventoryMovement.dtsx' | Resolve-Path
    Write-Host "Running SSIS package $package" -ForegroundColor Cyan
    & $dtexec.Source /File "$package" `
        /Set "\Package.Variables[User::ServerName].Properties[Value];$Server" `
        /Set "\Package.Variables[User::DatabaseName].Properties[Value];$Database" `
        /Reporting E
    if ($LASTEXITCODE -ne 0) { throw "dtexec exited with code $LASTEXITCODE" }
}
else {
    Write-Host 'Running etl.usp_LoadDailyInventoryMovement' -ForegroundColor Cyan
    Invoke-Query 'EXEC etl.usp_LoadDailyInventoryMovement' | Format-Table -AutoSize
}

Write-Host 'Recent runs:' -ForegroundColor Cyan
Invoke-Query @'
SELECT TOP (5) RunId, RunSource, Status, StartedAtUtc,
       DATEDIFF(MILLISECOND, StartedAtUtc, EndedAtUtc) AS DurationMs,
       RowsExtracted, RowsWritten, AffectedDays, ErrorMessage
FROM etl.RunLog
ORDER BY RunId DESC
'@ | Format-Table -AutoSize
