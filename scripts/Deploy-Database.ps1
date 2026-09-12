<#
.SYNOPSIS
    Creates the YardTracker database and runs every script in /database in order.

.DESCRIPTION
    Uses the SqlClient built into Windows PowerShell, so sqlcmd / SSMS are not required.
    Batches are split on lines containing only "GO", the same way sqlcmd does.

.EXAMPLE
    .\scripts\Deploy-Database.ps1
    .\scripts\Deploy-Database.ps1 -Server "(localdb)\MSSQLLocalDB" -Database YardTracker -Force
#>
[CmdletBinding()]
param(
    [string] $Server = '(localdb)\MSSQLLocalDB',
    [string] $Database = 'YardTracker',
    # Drop the database first if it already exists.
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$scriptDir = Join-Path $PSScriptRoot '..\database' | Resolve-Path

function Invoke-SqlBatches {
    param([string] $ConnectionString, [string] $Sql, [string] $Name)

    $connection = New-Object System.Data.SqlClient.SqlConnection $ConnectionString
    $connection.add_InfoMessage({ param($s, $e) Write-Host "    $($e.Message)" -ForegroundColor DarkGray })
    $connection.Open()
    try {
        $batches = [regex]::Split($Sql, '^\s*GO\s*$', [System.Text.RegularExpressions.RegexOptions]::Multiline -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        $index = 0
        foreach ($batch in $batches) {
            $index++
            if ([string]::IsNullOrWhiteSpace($batch)) { continue }
            $command = $connection.CreateCommand()
            $command.CommandText = $batch
            $command.CommandTimeout = 300
            try {
                [void] $command.ExecuteNonQuery()
            }
            catch {
                $firstLine = ($batch.Trim() -split "`n")[0]
                $inner = $_.Exception.InnerException
                $detail = if ($null -ne $inner) { $inner.Message } else { $_.Exception.Message }
                throw "$Name, batch $index ($firstLine): $detail"
            }
        }
    }
    finally {
        $connection.Dispose()
    }
}

$masterConnection = "Server=$Server;Database=master;Integrated Security=true;Connect Timeout=60"
$dbConnection = "Server=$Server;Database=$Database;Integrated Security=true;Connect Timeout=60"

Write-Host "Target: $Server / $Database" -ForegroundColor Cyan

$exists = $false
$probe = New-Object System.Data.SqlClient.SqlConnection $masterConnection
$probe.Open()
try {
    $cmd = $probe.CreateCommand()
    $cmd.CommandText = 'SELECT COUNT(*) FROM sys.databases WHERE name = @name'
    [void] $cmd.Parameters.AddWithValue('@name', $Database)
    $exists = [int] $cmd.ExecuteScalar() -gt 0
}
finally { $probe.Dispose() }

if ($exists -and -not $Force) {
    throw "Database '$Database' already exists. Re-run with -Force to drop and recreate it."
}

$quoted = '[' + $Database.Replace(']', ']]') + ']'
$create = @"
IF DB_ID(N'$($Database.Replace("'", "''"))') IS NOT NULL
BEGIN
    ALTER DATABASE $quoted SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE $quoted;
END
GO
CREATE DATABASE $quoted;
GO
ALTER DATABASE $quoted SET READ_COMMITTED_SNAPSHOT ON;
GO
"@
Write-Host "Creating database..." -ForegroundColor Cyan
Invoke-SqlBatches -ConnectionString $masterConnection -Sql $create -Name 'create database'

foreach ($file in Get-ChildItem $scriptDir -Filter '*.sql' | Sort-Object Name) {
    Write-Host "Running $($file.Name)..." -ForegroundColor Cyan
    Invoke-SqlBatches -ConnectionString $dbConnection -Sql (Get-Content $file.FullName -Raw) -Name $file.Name
}

Write-Host "Done. Connection string: $dbConnection" -ForegroundColor Green
