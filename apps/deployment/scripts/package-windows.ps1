[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts",
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$UnsignedMsix,
    # Skip the MSIX (for example when no signing certificate is available); the installer and ZIPs are still produced.
    [switch]$SkipMsix,
    # Skip the Setup.exe installer (for example when Inno Setup is not installed on an engineering machine).
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

# Windows SDK tools are often installed but not on PATH (for example on GitHub-hosted runners).
function Find-SdkTool {
    param([string]$Name)
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $kits)) { return $null }
    $versions = Get-ChildItem $kits -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending
    foreach ($directory in $versions) {
        $candidate = Join-Path $directory.FullName "x64\$Name"
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

# The Inno Setup compiler: on PATH, or in its default installation folders.
function Find-InnoSetupCompiler {
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA "Programs"))) {
        if (-not $base) { continue }
        $candidate = Join-Path $base "Inno Setup 6\ISCC.exe"
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "src/EngineeringWorkspace.Desktop/EngineeringWorkspace.Desktop.csproj"
$versionOutput = & dotnet msbuild $project -getProperty:VersionPrefix -nologo
if ($LASTEXITCODE -ne 0) { throw "Unable to resolve VersionPrefix from MSBuild." }
$version = ($versionOutput | Select-Object -Last 1).ToString().Trim()
if ($version -match '[:=]') { $version = $version.Split(':', 2)[-1].Split('=', 2)[-1].Trim() }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VersionPrefix must be MAJOR.MINOR.PATCH; found '$version'." }
# A pre-release (VersionSuffix such as beta.1) is part of file names and the product version; Windows file and package
# versions stay numeric (MAJOR.MINOR.PATCH.0).
$suffixOutput = & dotnet msbuild $project -getProperty:VersionSuffix -nologo
$suffix = (($suffixOutput | Select-Object -Last 1) -as [string])
if ($suffix) { $suffix = $suffix.Trim(); if ($suffix -match '[:=]') { $suffix = $suffix.Split(':', 2)[-1].Split('=', 2)[-1].Trim() } }
$fullVersion = if ($suffix) { "$version-$suffix" } else { $version }
$packageVersion = "$version.0"
$artifactPrefix = "GDS-SecurePlusAgent-$fullVersion-$Runtime"
$artifacts = Join-Path $root $OutputRoot
$publish = Join-Path $artifacts "publish/$Runtime"
$portable = Join-Path $artifacts "$artifactPrefix-portable"
$msixStage = Join-Path $artifacts "msix-stage"
$portableZip = Join-Path $artifacts "$artifactPrefix-portable.zip"
$frameworkPublish = Join-Path $artifacts "publish/$Runtime-framework-dependent"
$frameworkZip = Join-Path $artifacts "$artifactPrefix-framework-dependent.zip"
$installerScript = Join-Path $root "packaging/installer/SecurePlusAgent.iss"
$installer = Join-Path $artifacts "$artifactPrefix-Setup.exe"
$msix = Join-Path $artifacts "$artifactPrefix.msix"

New-Item -ItemType Directory -Force $artifacts | Out-Null
Remove-Item $publish, $portable, $msixStage, $portableZip, $msix, $frameworkPublish, $frameworkZip, $installer -Recurse -Force -ErrorAction SilentlyContinue

# Self-contained folder publish: the installer, the portable ZIP and the MSIX are built from it. A folder (not a
# single-file executable) is used so nothing is extracted to %TEMP% at run time.
& dotnet publish $project -c $Configuration -r $Runtime --self-contained true -p:Version=$fullVersion -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
# PowerShell 7 and the pinned modules for the read-only collector (the installer runs it when its task is ticked).
New-Item -ItemType Directory -Force (Join-Path $publish "prerequisites") | Out-Null
Copy-Item (Join-Path $root "packaging/prerequisites/Install-SecurePlusPowerShellModules.ps1") (Join-Path $publish "prerequisites") -Force

# Framework-dependent ZIP: smaller, needs the .NET 10 Windows Desktop Runtime. It carries the prerequisite installer
# (detects Microsoft.WindowsDesktop.App 10.x, downloads from Microsoft's release metadata, verifies SHA-512 and the
# Microsoft Authenticode signature, installs silently). The self-contained packages need no prerequisite.
& dotnet publish $project -c $Configuration -r $Runtime --self-contained false -p:Version=$fullVersion -o $frameworkPublish --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (framework-dependent) failed." }
New-Item -ItemType Directory -Force (Join-Path $frameworkPublish "prerequisites") | Out-Null
Copy-Item (Join-Path $root "packaging/prerequisites/Install-DotNetDesktopRuntime.ps1") (Join-Path $frameworkPublish "prerequisites") -Force
Copy-Item (Join-Path $root "packaging/prerequisites/Install-SecurePlusPowerShellModules.ps1") (Join-Path $frameworkPublish "prerequisites") -Force
Compress-Archive -Path (Join-Path $frameworkPublish "*") -DestinationPath $frameworkZip -CompressionLevel Optimal

New-Item -ItemType Directory -Force $portable | Out-Null
Copy-Item (Join-Path $publish "*") $portable -Recurse -Force
Compress-Archive -Path (Join-Path $portable "*") -DestinationPath $portableZip -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $iscc = Find-InnoSetupCompiler
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) is required to build the installer. Install it (for example 'choco install innosetup'), or use -SkipInstaller." }
    & $iscc "/DAppVersion=$fullVersion" "/DNumericVersion=$version" "/DSourceDir=$([IO.Path]::GetFullPath($publish))" "/DOutputDir=$([IO.Path]::GetFullPath($artifacts))" $installerScript
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed." }
    if (-not (Test-Path $installer)) { throw "The installer was not produced at '$installer'." }
    if ($CertificatePath) {
        $signTool = Find-SdkTool "signtool.exe"
        if (-not $signTool) { throw "signtool.exe is required to sign the installer. Install the Windows SDK." }
        & $signTool sign /fd SHA256 /f $CertificatePath /p $CertificatePassword /tr http://timestamp.digicert.com /td SHA256 $installer
        if ($LASTEXITCODE -ne 0) { throw "signtool.exe failed to sign the installer." }
    }
}

if (-not $SkipMsix) {
    New-Item -ItemType Directory -Force $msixStage | Out-Null
    Copy-Item (Join-Path $publish "*") $msixStage -Recurse -Force
    New-Item -ItemType Directory -Force (Join-Path $msixStage "Assets") | Out-Null
    Copy-Item (Join-Path $root "packaging/msix/Assets/*") (Join-Path $msixStage "Assets") -Force
    Copy-Item (Join-Path $root "config/ApplicationProperties.json") (Join-Path $msixStage "config/ApplicationProperties.template.json") -Force
    $manifest = (Get-Content (Join-Path $root "packaging/msix/AppxManifest.xml") -Raw).Replace("__VERSION__", $packageVersion)
    Set-Content (Join-Path $msixStage "AppxManifest.xml") $manifest -Encoding utf8

    $makeAppx = Find-SdkTool "makeappx.exe"
    if (-not $makeAppx) { throw "makeappx.exe is required for MSIX packaging. Install the Windows SDK, or use -SkipMsix." }
    & $makeAppx pack /d $msixStage /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx.exe failed." }

    if ($CertificatePath) {
        $signTool = Find-SdkTool "signtool.exe"
        if (-not $signTool) { throw "signtool.exe is required to sign the MSIX. Install the Windows SDK." }
        & $signTool sign /fd SHA256 /f $CertificatePath /p $CertificatePassword $msix
        if ($LASTEXITCODE -ne 0) { throw "signtool.exe failed." }
    } elseif (-not $UnsignedMsix) {
        throw "A signing certificate is required for a distributable MSIX. Use -UnsignedMsix only for engineering validation, or -SkipMsix."
    }
}

if (-not $SkipInstaller) { Write-Host "Installer (installs to Program Files, registers in Programs and Features): $installer" }
Write-Host "Portable ZIP (self-contained): $portableZip"
Write-Host "Framework-dependent ZIP (run prerequisites\Install-DotNetDesktopRuntime.ps1 first): $frameworkZip"
if (-not $SkipMsix) { Write-Host "MSIX: $msix" }

# SHA-256 checksums of every downloadable artifact (attached to the GitHub release).
$checksums = Join-Path $artifacts "$artifactPrefix-SHA256SUMS.txt"
Get-ChildItem $artifacts -File | Where-Object { $_.Extension -in ".exe", ".zip", ".msix" } | Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content -Path $checksums -Encoding ascii
Write-Host "Checksums: $checksums"
