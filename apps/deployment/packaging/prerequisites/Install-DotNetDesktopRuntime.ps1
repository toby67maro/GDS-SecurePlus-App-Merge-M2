<#
.SYNOPSIS
    Ensures the .NET 10 Windows Desktop Runtime (Microsoft.WindowsDesktop.App 10.x) is installed.

.DESCRIPTION
    Required only by the framework-dependent Secure Plus Agent package. The self-contained MSIX and
    portable ZIP already include the runtime and need no prerequisite.

    1. Detects Microsoft.WindowsDesktop.App 10.x for the requested architecture (WPF needs the Windows Desktop
       Runtime, not only the base .NET runtime).
    2. If it is missing, obtains the installer either from -OfflineInstallerPath or from Microsoft's official
       .NET release metadata (builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json). No installer
       URL is hard-coded: the URL and SHA-512 hash come from the metadata.
    3. Verifies the SHA-512 hash (online download) and the Microsoft Authenticode signature (always) before running it.
    4. Runs the installer silently with elevation (/install /quiet /norestart), handles reboot-required exit codes,
       then detects again. It never removes or downgrades other runtimes.

    Exit codes: 0 = installed or already present, 3010 = installed but a restart is required, 1 = failure.

.PARAMETER Architecture
    x64 (default), x86 or arm64 - must match the application build (the published package is win-x64).

.PARAMETER OfflineInstallerPath
    Path to a windowsdesktop-runtime-10.x.y-win-<arch>.exe obtained from Microsoft, for machines without internet access.

.PARAMETER LogPath
    Log file path. Defaults to %TEMP%\SecurePlus-DotNetDesktopRuntime-<timestamp>.log.

.EXAMPLE
    .\Install-DotNetDesktopRuntime.ps1

.EXAMPLE
    .\Install-DotNetDesktopRuntime.ps1 -OfflineInstallerPath .\windowsdesktop-runtime-10.0.0-win-x64.exe
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Architecture = 'x64',
    [string]$OfflineInstallerPath,
    [string]$LogPath = (Join-Path $env:TEMP ("SecurePlus-DotNetDesktopRuntime-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date)))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RequiredMajor = 10
$MetadataUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = '{0:u} [{1}] {2}' -f (Get-Date), $Level, $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    if ($Level -eq 'ERROR') { Write-Host $line -ForegroundColor Red } elseif ($Level -eq 'WARN') { Write-Host $line -ForegroundColor Yellow } else { Write-Host $line }
}

function Get-InstalledDesktopRuntimeVersions {
    # Primary source: the shared framework folders the installer creates. Registry is a secondary source.
    $roots = @()
    if ($Architecture -eq 'x86') { $roots += Join-Path ${env:ProgramFiles(x86)} 'dotnet\shared\Microsoft.WindowsDesktop.App' }
    else { $roots += Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App' }
    if ($Architecture -eq 'x64' -and (Test-Path (Join-Path $env:ProgramFiles 'dotnet\x64'))) { $roots += Join-Path $env:ProgramFiles 'dotnet\x64\shared\Microsoft.WindowsDesktop.App' }

    $versions = @()
    foreach ($root in $roots) {
        if (Test-Path -LiteralPath $root) {
            $versions += Get-ChildItem -LiteralPath $root -Directory | ForEach-Object { $_.Name }
        }
    }

    $registryKey = "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\$Architecture\sharedfx\Microsoft.WindowsDesktop.App"
    if (Test-Path -LiteralPath $registryKey) {
        $versions += (Get-Item -LiteralPath $registryKey).Property
    }

    $versions | Where-Object { $_ -match '^\d+\.\d+\.\d+' } | Sort-Object -Unique
}

function Test-DesktopRuntimePresent {
    $found = @(Get-InstalledDesktopRuntimeVersions | Where-Object { ([version]($_ -replace '-.*$', '')).Major -eq $RequiredMajor })
    if ($found.Count -gt 0) {
        Write-Log ("Microsoft.WindowsDesktop.App {0} found for {1}." -f ($found -join ', '), $Architecture)
        return $true
    }
    return $false
}

function Test-MicrosoftSignature {
    param([string]$Path)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') { throw "The installer signature is not valid ($($signature.Status)). It was not run." }
    if ($signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "The installer is not signed by Microsoft Corporation ($($signature.SignerCertificate.Subject)). It was not run." }
    Write-Log "Authenticode signature valid: $($signature.SignerCertificate.Subject)"
}

function Get-OnlineInstaller {
    Write-Log "Reading official release metadata: $MetadataUrl"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $metadata = Invoke-RestMethod -Uri $MetadataUrl -UseBasicParsing
    $latest = $metadata.'latest-runtime'
    $release = $metadata.releases | Where-Object { $_.windowsdesktop -and $_.windowsdesktop.version -eq $latest } | Select-Object -First 1
    if (-not $release) { $release = $metadata.releases | Where-Object { $_.windowsdesktop } | Select-Object -First 1 }
    if (-not $release) { throw 'The release metadata contains no Windows Desktop Runtime release.' }
    $rid = "win-$Architecture"
    $file = $release.windowsdesktop.files | Where-Object { $_.rid -eq $rid -and $_.name -like '*.exe' } | Select-Object -First 1
    if (-not $file) { throw "The release metadata has no Windows Desktop Runtime installer for $rid." }
    $uri = [Uri]$file.url
    if ($uri.Scheme -ne 'https' -or $uri.Host -notmatch '(^|\.)(microsoft\.com|dot\.net)$') { throw "Unexpected download host '$($uri.Host)'. Nothing was downloaded." }

    $target = Join-Path $env:TEMP ("windowsdesktop-runtime-{0}-{1}.exe" -f $release.windowsdesktop.version, $rid)
    Write-Log "Downloading Windows Desktop Runtime $($release.windowsdesktop.version) ($rid) from $uri"
    Invoke-WebRequest -Uri $uri -OutFile $target -UseBasicParsing
    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA512).Hash
    if ($actual -ne $file.hash.ToUpperInvariant()) {
        Remove-Item -LiteralPath $target -Force
        throw 'The downloaded installer does not match the SHA-512 hash published in the release metadata. It was deleted and not run.'
    }
    Write-Log 'SHA-512 hash matches the release metadata.'
    return $target
}

try {
    Write-Log "Secure Plus Agent prerequisite check: .NET $RequiredMajor Windows Desktop Runtime ($Architecture). Log: $LogPath"
    if (Test-DesktopRuntimePresent) { exit 0 }

    $installer = if ($OfflineInstallerPath) { (Resolve-Path -LiteralPath $OfflineInstallerPath).Path } else { Get-OnlineInstaller }
    if ($OfflineInstallerPath) { Write-Log "Using offline installer $installer" }
    Test-MicrosoftSignature -Path $installer

    Write-Log 'Running the installer silently with elevation (/install /quiet /norestart).'
    $process = Start-Process -FilePath $installer -ArgumentList '/install', '/quiet', '/norestart', '/log', ('"{0}.installer.log"' -f $LogPath) -Verb RunAs -Wait -PassThru
    $code = $process.ExitCode
    Write-Log "Installer exit code: $code"
    $restartRequired = $false
    switch ($code) {
        0 { }
        3010 { $restartRequired = $true; Write-Log 'Installation succeeded; Windows must be restarted.' 'WARN' }
        1641 { $restartRequired = $true; Write-Log 'Installation succeeded and a restart was initiated.' 'WARN' }
        1638 { Write-Log 'Another version of this product is already installed; it was not changed.' 'WARN' }
        1602 { throw 'The installation was cancelled.' }
        1618 { throw 'Another installation is in progress. Wait for it to finish and run this script again.' }
        default { throw "The installer failed with exit code $code. See $LogPath.installer.log." }
    }

    if (-not (Test-DesktopRuntimePresent)) {
        if ($restartRequired) { Write-Log 'The runtime is not visible yet; restart Windows and run this script again to confirm.' 'WARN'; exit 3010 }
        throw 'The installer finished but Microsoft.WindowsDesktop.App 10.x is still not detected.'
    }

    if ($restartRequired) { exit 3010 }
    exit 0
}
catch {
    Write-Log $_.Exception.Message 'ERROR'
    Write-Log 'Install the .NET 10 Desktop Runtime manually from https://dotnet.microsoft.com/download/dotnet/10.0 or use the self-contained package.' 'ERROR'
    exit 1
}
