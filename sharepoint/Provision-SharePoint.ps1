<#
.SYNOPSIS
    Creates the YardTracker lists and libraries on a SharePoint Online site.

.DESCRIPTION
    Creates:
      * "Scan Exception Reports"  document library for the daily exception report (HTML + CSV)
      * "Scan Exceptions"         list with one item per rejected scan, for supervisors to work through
      * "Yard SOPs"               document library for standard operating procedures (uploads .\sop\*)

    Requires PowerShell 7.4+ and PnP.PowerShell:  Install-Module PnP.PowerShell -Scope CurrentUser
    PnP.PowerShell needs an Entra ID app registration; pass its client id with -ClientId.

.EXAMPLE
    .\sharepoint\Provision-SharePoint.ps1 -SiteUrl https://contoso.sharepoint.com/sites/yard -ClientId 00000000-0000-0000-0000-000000000000
#>
#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SiteUrl,
    [Parameter(Mandatory)] [string] $ClientId
)

$ErrorActionPreference = 'Stop'
Import-Module PnP.PowerShell
Connect-PnPOnline -Url $SiteUrl -ClientId $ClientId -Interactive

function Ensure-List([string] $Title, [string] $Template, [string] $Url) {
    $list = Get-PnPList -Identity $Title -ErrorAction SilentlyContinue
    if ($list) { Write-Host "Exists:  $Title"; return $list }
    Write-Host "Creating $Title" -ForegroundColor Cyan
    return New-PnPList -Title $Title -Template $Template -Url $Url -OnQuickLaunch
}

function Ensure-Field([string] $List, [string] $InternalName, [string] $DisplayName, [string] $Type, [string[]] $Choices) {
    if (Get-PnPField -List $List -Identity $InternalName -ErrorAction SilentlyContinue) { return }
    $params = @{ List = $List; InternalName = $InternalName; DisplayName = $DisplayName; Type = $Type; AddToDefaultView = $true }
    if ($Choices) { $params.Choices = $Choices }
    Add-PnPField @params | Out-Null
}

$exceptionTypes = 'UnknownTag', 'UnknownLocation', 'UnknownOperator', 'UnknownStation', 'UnknownProduct', 'InvalidState', 'DuplicateTag', 'ClockSkew', 'WrongSite'

# Daily report library
Ensure-List -Title 'Scan Exception Reports' -Template DocumentLibrary -Url 'ScanExceptionReports' | Out-Null
Ensure-Field 'Scan Exception Reports' 'ReportDate' 'Report date' DateTime
Ensure-Field 'Scan Exception Reports' 'TotalExceptions' 'Total exceptions' Number
Ensure-Field 'Scan Exception Reports' 'OpenExceptions' 'Open exceptions' Number

# One item per exception
Ensure-List -Title 'Scan Exceptions' -Template GenericList -Url 'Lists/ScanExceptions' | Out-Null
Ensure-Field 'Scan Exceptions' 'ExceptionId' 'Exception id' Number
Ensure-Field 'Scan Exceptions' 'OccurredLocal' 'Occurred' DateTime
Ensure-Field 'Scan Exceptions' 'ExceptionType' 'Type' Choice $exceptionTypes
Ensure-Field 'Scan Exceptions' 'AttemptedAction' 'Attempted action' Text
Ensure-Field 'Scan Exceptions' 'TagId' 'Tag' Text
Ensure-Field 'Scan Exceptions' 'StationCode' 'Station' Text
Ensure-Field 'Scan Exceptions' 'OperatorName' 'Operator' Text
Ensure-Field 'Scan Exceptions' 'Details' 'Details' Note
Ensure-Field 'Scan Exceptions' 'Resolution' 'Resolution' Choice @('Open', 'Investigating', 'Resolved')

# SOP library
Ensure-List -Title 'Yard SOPs' -Template DocumentLibrary -Url 'YardSOPs' | Out-Null
Ensure-Field 'Yard SOPs' 'SopNumber' 'SOP number' Text
Ensure-Field 'Yard SOPs' 'NextReview' 'Next review' DateTime

foreach ($file in Get-ChildItem (Join-Path $PSScriptRoot 'sop') -File) {
    $number = ($file.BaseName -split '-')[0..1] -join '-'
    Write-Host "Uploading $($file.Name)"
    Add-PnPFile -Path $file.FullName -Folder 'YardSOPs' -Values @{ SopNumber = $number; NextReview = (Get-Date).AddYears(1) } | Out-Null
}

Write-Host 'SharePoint provisioning complete.' -ForegroundColor Green
