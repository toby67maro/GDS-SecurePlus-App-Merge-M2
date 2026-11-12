<#
.SYNOPSIS
    Installs PowerShell 7 and the pinned modules the Secure Plus read-only PowerShell collector uses.

.DESCRIPTION
    In-depth scans read Exchange Online, Security & Compliance (Purview) and Teams settings with PowerShell 7 and these exact
    module versions (the app imports them with -RequiredVersion, so other installed versions are left alone):
      ExchangeOnlineManagement 3.10.1   (Exchange Online and Security & Compliance PowerShell)
      MicrosoftTeams 8.0.0              (Teams PowerShell)
    The installer runs this script when "Install PowerShell modules" is ticked; it can also be run by hand at any time.
    Without the modules a scan still runs: those settings are reported as "not collected" with the command to fix it.

.PARAMETER Scope
    AllUsers (needs an elevated prompt; used by the installer) or CurrentUser.

.PARAMETER SkipPowerShellInstall
    Do not install PowerShell 7 with winget when it is missing.
#>
[CmdletBinding()]
param(
    [ValidateSet('AllUsers', 'CurrentUser')]
    [string]$Scope = 'AllUsers',
    [switch]$SkipPowerShellInstall
)

$ErrorActionPreference = 'Stop'
$modules = [ordered]@{
    'ExchangeOnlineManagement' = '3.10.1'
    'MicrosoftTeams'           = '8.0.0'
}

function Find-Pwsh {
    $command = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $installed = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
    if (Test-Path $installed) { return $installed }
    return $null
}

$pwsh = Find-Pwsh
if (-not $pwsh -and -not $SkipPowerShellInstall) {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host 'Installing PowerShell 7 (winget, Microsoft.PowerShell)...'
        & $winget.Source install --id Microsoft.PowerShell --exact --silent --accept-package-agreements --accept-source-agreements | Out-Host
        $pwsh = Find-Pwsh
    }
}

if (-not $pwsh) {
    Write-Warning 'PowerShell 7 is not installed. Install it from https://aka.ms/powershell, then run this script again. Scans still run; Exchange, Purview and Teams settings are reported as not collected.'
    exit 0
}

if ($Scope -eq 'AllUsers') {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Warning 'Not elevated: installing for the current user instead of all users.'
        $Scope = 'CurrentUser'
    }
}

$failed = @()
foreach ($name in $modules.Keys) {
    $version = $modules[$name]
    Write-Host "Installing $name $version for $Scope..."
    $script = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
if (Get-Module -ListAvailable -Name '$name' | Where-Object { `$_.Version -eq [version]'$version' }) { 'already installed'; return }
if (-not (Get-PackageProvider -ListAvailable -Name NuGet -ErrorAction SilentlyContinue)) { Install-PackageProvider -Name NuGet -Force -Scope $Scope | Out-Null }
Install-Module -Name '$name' -RequiredVersion '$version' -Scope $Scope -Repository PSGallery -Force -AllowClobber -AcceptLicense
'installed'
"@
    & $pwsh -NoLogo -NoProfile -NonInteractive -Command $script
    if ($LASTEXITCODE -ne 0) { $failed += "$name $version" }
}

if ($failed.Count -gt 0) {
    Write-Warning ("Not installed: " + ($failed -join ', ') + '. Check the internet connection and access to www.powershellgallery.com, then run this script again.')
}
else {
    Write-Host 'PowerShell modules for Secure Plus are ready.'
}

exit 0
