<#
.SYNOPSIS
  Builds and runs the sales intake form locally against the shared SQL Server.
.DESCRIPTION
  Requires .NET 10 SDK and Node 20.19+/22.12+, plus Windows-login access to the SQL Server.
  Settings are passed as process environment variables only; nothing is written to disk or git.
  The optional Planner flow URL is read as a secret prompt and is never stored.
#>
param(
    [string]$SqlServer = "GDS-CLSV-SQL.gds.pri",
    [int]$Port = 5080,
    [switch]$SkipBuild,
    [switch]$NoBridge,
    [switch]$NoPrompt
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$web = Join-Path $root "apps\sales\web"
$api = Join-Path $root "apps\sales\server\ArchiTech.Api"

$required = if ($SkipBuild) { @("dotnet") } else { @("dotnet", "node", "npm") }
foreach ($tool in $required) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool was not found on PATH. See docs\TESTER-SETUP.md." }
}

if (-not $SkipBuild) {
    Push-Location $web
    try {
        if (-not (Test-Path node_modules)) { npm ci }
        npm run build
    } finally { Pop-Location }
}

$conn = "Server=tcp:$SqlServer,1433;Database={0};Integrated Security=true;Encrypt=false;Connect Timeout=10;Application Name={1}"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://0.0.0.0:$Port"
$env:Database__Provider = "SqlServer"
$env:ConnectionStrings__Default = $conn -f "ArchiTech3", "GDS-SalesForm"
if (-not $NoBridge) {
    $env:Bridge__Enabled = "true"
    $env:Bridge__ApplySchema = "false"
    $env:Bridge__ConnectionString = $conn -f "SecurePlus", "GDS-SalesBridge"
}

$secure = if ($NoPrompt) { $null } else { Read-Host "Planner flow URL (press Enter to skip; held in memory only)" -AsSecureString }
$plain = if ($secure) { [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)) } else { "" }
if ($plain) {
    $env:PlannerFlow__Endpoint = $plain.Trim()
    $env:PlannerFlow__AuthenticationMode = "SignedUrlTest"
}

Write-Host "Sales form: http://localhost:$Port" -ForegroundColor Cyan
Set-Location $api
dotnet run --no-launch-profile
