<#
.SYNOPSIS
    Publishes the daily scan exception report to SharePoint.

.DESCRIPTION
    1. Reads rejected scans for one plant business day from rpt.usp_ScanExceptionReport.
    2. Writes an HTML report and a CSV extract to -OutputFolder.
    3. Unless -LocalOnly: uploads both to the "Scan Exception Reports" library, adds each exception to the
       "Scan Exceptions" list (skipping ones already there), and marks them published in SQL Server.

    -LocalOnly runs in Windows PowerShell 5.1 without SharePoint. Publishing needs PowerShell 7.4+ and
    PnP.PowerShell (see Provision-SharePoint.ps1). Schedule it after the ETL, e.g. 06:00 daily.

.EXAMPLE
    .\sharepoint\Publish-ScanExceptionReport.ps1 -LocalOnly
    .\sharepoint\Publish-ScanExceptionReport.ps1 -BusinessDate 2026-09-11 -SiteUrl https://contoso.sharepoint.com/sites/yard -ClientId <app id>
#>
[CmdletBinding()]
param(
    [datetime] $BusinessDate = (Get-Date).Date.AddDays(-1),
    [string] $Server = '(localdb)\MSSQLLocalDB',
    [string] $Database = 'YardTracker',
    [string] $OutputFolder = (Join-Path $PSScriptRoot '..\out\sharepoint'),
    [string] $SiteUrl,
    [string] $ClientId,
    [switch] $LocalOnly
)

$ErrorActionPreference = 'Stop'
$connectionString = "Server=$Server;Database=$Database;Integrated Security=true"
$day = $BusinessDate.ToString('yyyy-MM-dd')

# ---------------------------------------------------------------- extract
$connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandText = 'rpt.usp_ScanExceptionReport'
    $command.CommandType = [System.Data.CommandType]::StoredProcedure
    [void] $command.Parameters.AddWithValue('@BusinessDate', $BusinessDate.Date)
    $rows = New-Object System.Data.DataTable
    $rows.Load($command.ExecuteReader())
}
finally { $connection.Dispose() }

$total = $rows.Rows.Count
$open = @($rows.Rows | Where-Object { $_.ResolutionStatus -eq 'Open' }).Count
Write-Host "$total scan exceptions on $day ($open open)" -ForegroundColor Cyan

# ---------------------------------------------------------------- render
New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null
$csvPath = Join-Path $OutputFolder "scan-exceptions-$day.csv"
$htmlPath = Join-Path $OutputFolder "scan-exceptions-$day.html"

$rows | Select-Object ScanExceptionId, OccurredLocal, ExceptionType, AttemptedAction, RawTag, StationCode, OperatorName, LocationCode, ResolutionStatus, Message |
    Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

$byType = $rows.Rows | Group-Object ExceptionType | Sort-Object Count -Descending
$encode = { param($v) [System.Net.WebUtility]::HtmlEncode([string] $v) }

$summaryRows = ($byType | ForEach-Object { "<tr><td>$(& $encode $_.Name)</td><td class='n'>$($_.Count)</td></tr>" }) -join "`n"
$detailRows = ($rows.Rows | ForEach-Object {
    "<tr><td>$(& $encode $_.OccurredLocal)</td><td>$(& $encode $_.ExceptionType)</td><td>$(& $encode $_.AttemptedAction)</td>" +
    "<td class='mono'>$(& $encode $_.RawTag)</td><td>$(& $encode $_.StationCode)</td><td>$(& $encode $_.OperatorName)</td>" +
    "<td>$(& $encode $_.Message)</td><td>$(& $encode $_.ResolutionStatus)</td></tr>"
}) -join "`n"

@"
<!DOCTYPE html>
<html lang="en"><head><meta charset="utf-8"><title>Scan exceptions $day</title>
<style>
 body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1f2933}
 h1{margin:0 0 4px} .muted{color:#616e7c}
 table{border-collapse:collapse;margin-top:16px;width:100%} th,td{border-bottom:1px solid #d9dee3;padding:6px 8px;text-align:left;font-size:13px}
 th{background:#1f2933;color:#fff} .n{text-align:right} .mono{font-family:Consolas,monospace}
 .kpi{display:inline-block;margin:12px 24px 0 0} .kpi b{display:block;font-size:28px}
</style></head><body>
<h1>Scan exception report</h1>
<div class="muted">Business day $day (plant time) &middot; generated $(Get-Date -Format 'yyyy-MM-dd HH:mm') from $Database</div>
<div class="kpi"><b>$total</b>exceptions</div><div class="kpi"><b>$open</b>open</div>
<h2>By type</h2><table><tr><th>Type</th><th class="n">Count</th></tr>$summaryRows</table>
<h2>Details</h2><table><tr><th>Occurred</th><th>Type</th><th>Action</th><th>Tag</th><th>Station</th><th>Operator</th><th>Message</th><th>Status</th></tr>
$detailRows
</table></body></html>
"@ | Set-Content -Path $htmlPath -Encoding UTF8

Write-Host "Wrote $htmlPath"
Write-Host "Wrote $csvPath"

if ($LocalOnly) { return }

# ---------------------------------------------------------------- publish
if (-not $SiteUrl -or -not $ClientId) { throw 'Pass -SiteUrl and -ClientId to publish, or use -LocalOnly.' }
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Publishing requires PowerShell 7.4+ (PnP.PowerShell).' }

Import-Module PnP.PowerShell
Connect-PnPOnline -Url $SiteUrl -ClientId $ClientId -Interactive

$values = @{ ReportDate = $BusinessDate.Date; TotalExceptions = $total; OpenExceptions = $open }
Add-PnPFile -Path $htmlPath -Folder 'ScanExceptionReports' -Values $values | Out-Null
Add-PnPFile -Path $csvPath -Folder 'ScanExceptionReports' -Values $values | Out-Null

$published = New-Object System.Data.DataTable
[void] $published.Columns.Add('Id', [long])

foreach ($row in $rows.Rows) {
    $id = [long] $row.ScanExceptionId
    $existing = Get-PnPListItem -List 'Scan Exceptions' -Query "<View><Query><Where><Eq><FieldRef Name='ExceptionId'/><Value Type='Number'>$id</Value></Eq></Where></Query></View>"
    if (-not $existing) {
        Add-PnPListItem -List 'Scan Exceptions' -Values @{
            Title           = "$($row.ExceptionType) $($row.RawTag)"
            ExceptionId     = $id
            OccurredLocal   = [datetime] $row.OccurredLocal
            ExceptionType   = [string] $row.ExceptionType
            AttemptedAction = [string] $row.AttemptedAction
            TagId           = [string] $row.RawTag
            StationCode     = [string] $row.StationCode
            OperatorName    = [string] $row.OperatorName
            Details         = [string] $row.Message
            Resolution      = 'Open'
        } | Out-Null
    }
    [void] $published.Rows.Add($id)
}

$connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandText = 'rpt.usp_MarkExceptionsPublished'
    $command.CommandType = [System.Data.CommandType]::StoredProcedure
    $parameter = $command.Parameters.Add('@ScanExceptionIds', [System.Data.SqlDbType]::Structured)
    $parameter.TypeName = 'dbo.IdList'
    $parameter.Value = $published
    [void] $command.ExecuteNonQuery()
}
finally { $connection.Dispose() }

Write-Host "Published $total exceptions to $SiteUrl" -ForegroundColor Green
