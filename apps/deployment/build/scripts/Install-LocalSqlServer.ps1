<#
.SYNOPSIS
    Prepares a local SQL Server instance on a developer Windows PC for Secure Plus - Modern Work.

.DESCRIPTION
    Idempotent. Run from an elevated PowerShell.

    1. Detects existing SQL Server instances (registry) and their versions. An instance of SQL Server 2022 or later
       (EaaS v3 targets SQL Server 2022 / Azure SQL) is suitable and is reused.
    2. Otherwise installs SQL Server Express (default) or Developer (-Edition Developer) with Windows authentication
       and the current user as sysadmin:
         a. resolves the winget package ID (verified with 'winget show' at run time; override with -WingetPackageId),
         b. downloads Microsoft's installer with 'winget download' (winget verifies the manifest hash),
         c. verifies the Microsoft Authenticode signature,
         d. downloads and extracts the installation media, verifies setup.exe's signature,
         e. runs setup.exe unattended.
    3. Enables TCP/IP for the instance if needed and restarts the service.
    4. Prints the server name to enter in Workspace > Settings > Database Configuration.

    The script never creates the application database (use Create Database and Initialize Schema in the
    application) and never writes passwords to the repository, disk or logs. With -SqlAuthentication it prompts
    for the 'sa' password at run time and passes it only to SQL Server setup.

.PARAMETER Edition
    Express (default) or Developer. Developer edition is licensed for development and test only.

.PARAMETER InstanceName
    Defaults to SQLEXPRESS for Express and MSSQLSERVER (default instance) for Developer.

.PARAMETER MinimumMajorVersion
    Minimum acceptable SQL Server major version of an existing instance. 16 = SQL Server 2022.

.PARAMETER WingetPackageId
    Use this winget package ID instead of resolving one (for example when Microsoft publishes a newer version).

.PARAMETER SqlAuthentication
    Also enable SQL Server authentication (mixed mode). Prompts for the sa password; it is not stored.

.PARAMETER SkipTcp
    Do not enable TCP/IP.

.EXAMPLE
    .\build\scripts\Install-LocalSqlServer.ps1

.EXAMPLE
    .\build\scripts\Install-LocalSqlServer.ps1 -Edition Developer
#>
[CmdletBinding()]
param(
    [ValidateSet('Express', 'Developer')]
    [string]$Edition = 'Express',
    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_$]{0,15}$')]
    [string]$InstanceName,
    [ValidateRange(13, 30)]
    [int]$MinimumMajorVersion = 16,
    [string]$WingetPackageId,
    [switch]$SqlAuthentication,
    [switch]$SkipTcp,
    [string]$WorkDirectory = (Join-Path $env:TEMP 'SecurePlus-SqlServerSetup')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $InstanceName) { $InstanceName = if ($Edition -eq 'Express') { 'SQLEXPRESS' } else { 'MSSQLSERVER' } }

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }

function Assert-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell (Run as administrator).'
    }
}

function Get-SqlInstances {
    $names = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
    if (-not (Test-Path -LiteralPath $names)) { return @() }
    $key = Get-Item -LiteralPath $names
    foreach ($name in $key.Property) {
        $id = $key.GetValue($name)
        $setup = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$id\Setup"
        $version = if (Test-Path -LiteralPath $setup) { (Get-ItemProperty -LiteralPath $setup).Version } else { $null }
        $editionName = if (Test-Path -LiteralPath $setup) { (Get-ItemProperty -LiteralPath $setup).Edition } else { $null }
        [pscustomobject]@{ Name = $name; Id = $id; Version = $version; Edition = $editionName }
    }
}

function Get-ServerName { param([string]$Name) if ($Name -eq 'MSSQLSERVER') { 'localhost' } else { "localhost\$Name" } }

function Get-ServiceName { param([string]$Name) if ($Name -eq 'MSSQLSERVER') { 'MSSQLSERVER' } else { "MSSQL`$$Name" } }

function Test-MicrosoftSignature {
    param([string]$Path)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "$Path is not validly signed by Microsoft Corporation ($($signature.Status)). It was not run."
    }
    Write-Host "    Signature valid: $($signature.SignerCertificate.Subject)"
}

function Resolve-WingetPackageId {
    if ($WingetPackageId) { $candidates = @($WingetPackageId) }
    else { $candidates = @("Microsoft.SQLServer.2025.$Edition", "Microsoft.SQLServer.2022.$Edition") }
    foreach ($candidate in $candidates) {
        & winget show --id $candidate --exact --accept-source-agreements --disable-interactivity *> $null
        if ($LASTEXITCODE -eq 0) {
            $details = & winget show --id $candidate --exact --accept-source-agreements --disable-interactivity
            Write-Host ($details | Select-String -Pattern '^(Found|Version|Publisher|Installer Url)' | ForEach-Object { "    $_" } | Out-String)
            return $candidate
        }
    }
    throw "No winget package was found for SQL Server $Edition (tried: $($candidates -join ', ')). Pass -WingetPackageId with the current ID from 'winget search Microsoft.SQLServer'."
}

function Enable-Tcp {
    param([string]$Id, [string]$Name)
    $tcp = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$Id\MSSQLServer\SuperSocketNetLib\Tcp"
    if (-not (Test-Path -LiteralPath $tcp)) { Write-Warning "TCP/IP settings were not found for $Name; skipped."; return }
    if ((Get-ItemProperty -LiteralPath $tcp).Enabled -eq 1) { Write-Host "    TCP/IP is already enabled for $Name."; return }
    Set-ItemProperty -LiteralPath $tcp -Name Enabled -Value 1
    $service = Get-ServiceName $Name
    Write-Host "    TCP/IP enabled; restarting $service."
    Restart-Service -Name $service -Force
    if ($Name -ne 'MSSQLSERVER') {
        Set-Service -Name SQLBrowser -StartupType Automatic
        Start-Service -Name SQLBrowser
        Write-Host '    SQL Server Browser started so the named instance resolves over TCP.'
    }
}

function Install-SqlServer {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget (App Installer) is required. Install it from the Microsoft Store, then run this script again.' }
    $packageId = Resolve-WingetPackageId
    $download = Join-Path $WorkDirectory 'bootstrapper'
    $media = Join-Path $WorkDirectory 'media'
    $extracted = Join-Path $WorkDirectory 'setup'
    foreach ($directory in @($download, $media, $extracted)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }

    Write-Step "Downloading $packageId with winget (winget verifies the published installer hash)"
    & winget download --id $packageId --exact --download-directory $download --accept-package-agreements --accept-source-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) { throw "winget download failed with exit code $LASTEXITCODE." }
    $bootstrapper = Get-ChildItem -LiteralPath $download -Filter '*.exe' -Recurse | Select-Object -First 1
    if (-not $bootstrapper) { throw 'winget did not download an installer executable.' }
    Test-MicrosoftSignature -Path $bootstrapper.FullName

    Write-Step 'Downloading installation media'
    $mediaType = if ($Edition -eq 'Express') { 'Core' } else { 'CAB' }
    $process = Start-Process -FilePath $bootstrapper.FullName -ArgumentList '/ACTION=Download', "/MEDIAPATH=`"$media`"", "/MEDIATYPE=$mediaType", '/QUIET' -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Media download failed with exit code $($process.ExitCode)." }
    $package = Get-ChildItem -LiteralPath $media -Filter '*.exe' -Recurse | Select-Object -First 1
    if (-not $package) { throw 'No installation media executable was downloaded.' }
    Test-MicrosoftSignature -Path $package.FullName

    Write-Step 'Extracting installation media'
    $process = Start-Process -FilePath $package.FullName -ArgumentList '/q', "/x:`"$extracted`"" -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Media extraction failed with exit code $($process.ExitCode)." }
    $setup = Join-Path $extracted 'setup.exe'
    if (-not (Test-Path -LiteralPath $setup)) { throw 'setup.exe was not found in the extracted media.' }
    Test-MicrosoftSignature -Path $setup

    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $arguments = @('/Q', '/ACTION=Install', '/FEATURES=SQLENGINE', "/INSTANCENAME=$InstanceName", "/SQLSYSADMINACCOUNTS=`"$user`"",
        '/TCPENABLED=1', '/UPDATEENABLED=False', '/IACCEPTSQLSERVERLICENSETERMS', '/SUPPRESSPRIVACYSTATEMENTNOTICE')
    $password = $null
    if ($SqlAuthentication) {
        $secure = Read-Host -AsSecureString -Prompt 'Enter a strong password for the sa login (not stored)'
        $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
        $arguments += '/SECURITYMODE=SQL'
        $arguments += "/SAPWD=`"$password`""
    }

    Write-Step "Installing SQL Server $Edition instance '$InstanceName' (Windows authentication; sysadmin: $user)"
    try {
        $process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru
    }
    finally {
        $password = $null
        $arguments = $null
    }

    if ($process.ExitCode -eq 3010) { Write-Warning 'SQL Server was installed; Windows must be restarted.' }
    elseif ($process.ExitCode -ne 0) { throw "SQL Server setup failed with exit code $($process.ExitCode). See the setup logs under %ProgramFiles%\Microsoft SQL Server\*\Setup Bootstrap\Log." }
}

Assert-Administrator
New-Item -ItemType Directory -Force -Path $WorkDirectory | Out-Null

Write-Step 'Detecting SQL Server instances'
$instances = @(Get-SqlInstances)
foreach ($instance in $instances) { Write-Host ("    {0} ({1}) version {2} {3}" -f $instance.Name, $instance.Id, $instance.Version, $instance.Edition) }
$suitable = $instances | Where-Object { $_.Version -and ([version]$_.Version).Major -ge $MinimumMajorVersion } |
    Sort-Object @{ Expression = { $_.Name -eq $InstanceName }; Descending = $true }, @{ Expression = { [version]$_.Version }; Descending = $true } | Select-Object -First 1

if ($suitable) {
    Write-Host "    Reusing $($suitable.Name) (SQL Server $($suitable.Version))."
    $target = $suitable
}
else {
    Write-Host "    No instance of SQL Server major version $MinimumMajorVersion or later was found."
    Install-SqlServer
    $target = @(Get-SqlInstances) | Where-Object { $_.Name -eq $InstanceName } | Select-Object -First 1
    if (-not $target) { throw "Instance $InstanceName was not found after installation." }
}

if (-not $SkipTcp) {
    Write-Step 'Checking TCP/IP'
    Enable-Tcp -Id $target.Id -Name $target.Name
}

$service = Get-Service -Name (Get-ServiceName $target.Name) -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Running') { Start-Service -Name $service.Name }

$serverName = Get-ServerName $target.Name
Write-Step 'Done'
Write-Host ''
Write-Host "  Server name for Database Configuration:  $serverName" -ForegroundColor Green
Write-Host '  Provider: SqlServer   Authentication: WindowsIntegrated'
Write-Host '  A new local instance uses a self-signed certificate: either install a trusted certificate or, for'
Write-Host '  development only, enable "Trust server certificate". Then use Test Connection, Create Database and'
Write-Host '  Initialize Schema in the application. This script did not create the application database.'
