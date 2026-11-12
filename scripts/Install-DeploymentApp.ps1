<#
.SYNOPSIS
  Publishes (or installs from a published folder) the Secure Plus Agent desktop app for the current user
  and points it at the shared SecurePlus database with Windows login.
#>
param(
    [string]$SqlServer = "GDS-CLSV-SQL.gds.pri",
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "GDS Secure Plus Agent\app"),
    [string]$PublishedFrom,
    [switch]$Launch
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if (-not $PublishedFrom) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet SDK not found. Use -PublishedFrom with a prebuilt folder." }
    $PublishedFrom = Join-Path $root "artifacts\deployment-app"
    dotnet publish (Join-Path $root "apps\deployment\src\EngineeringWorkspace.Desktop\EngineeringWorkspace.Desktop.csproj") -c Release -r win-x64 --self-contained false -o $PublishedFrom
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}

New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $PublishedFrom "*") $InstallDir -Recurse -Force

$cfg = Join-Path $env:LOCALAPPDATA "Global Data Systems\Secure Plus Agent\config"
New-Item -ItemType Directory -Force $cfg | Out-Null
@"
[Database]
Provider=SqlServer
ServerName=$SqlServer
Port=1433
DatabaseName=SecurePlus
Authentication=WindowsIntegrated
EncryptConnection=false
TrustServerCertificate=false
ConnectionTimeoutSeconds=30
CommandTimeoutSeconds=60
RetryCount=3
RetryDelaySeconds=10
AutomaticDatabaseInitialization=false
AutomaticSchemaUpgrade=false
"@ | Set-Content (Join-Path $cfg "Database.ini") -Encoding UTF8

$exe = Join-Path $InstallDir "EngineeringWorkspace.Desktop.exe"
$lnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Secure Plus Agent.lnk"
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$s.TargetPath = $exe; $s.WorkingDirectory = $InstallDir; $s.Save()
Write-Host "Installed to $InstallDir. Desktop shortcut created." -ForegroundColor Cyan
if ($Launch) { Start-Process $exe }
