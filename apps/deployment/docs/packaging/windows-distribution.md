# Windows distribution

Packaging uses the existing PowerShell script `scripts/package-windows.ps1` (with `build/Version.props` as the single
version source) and the tag-driven workflow `.github/workflows/release-windows.yml`.

## Artifacts

| Artifact | Runtime | Prerequisite |
|---|---|---|
| `GDS-SecurePlusAgent-{Version}-win-x64-Setup.exe` | Self-contained | None. **Recommended.** Inno Setup installer (`packaging/installer/SecurePlusAgent.iss`): installs to `%ProgramFiles%\GDS Secure Plus agent`, adds Start menu (optional desktop) shortcuts, and registers **GDS Secure Plus agent** in Programs and Features (`appwiz.cpl`) with an uninstaller. Requires administrator rights. |
| `GDS-SecurePlusAgent-{Version}-win-x64-portable.zip` | Self-contained | None. Engineering and troubleshooting. |
| `GDS-SecurePlusAgent-{Version}-win-x64-framework-dependent.zip` | Framework-dependent (≈16 MB) | .NET 10 Windows Desktop Runtime: run `prerequisites\Install-DotNetDesktopRuntime.ps1` first. |
| `GDS-SecurePlusAgent-{Version}-win-x64.msix` | Self-contained | None. Built only when a signing certificate is available (`-SkipMsix` otherwise). MSIX apps appear in Settings > Apps, not in `appwiz.cpl`. |

The single-file executable of earlier releases is no longer produced: it extracted native libraries to `%TEMP%` on
every start. The installer installs a normal folder under Program Files instead.

MSIX cannot run an arbitrary runtime installer and the .NET Desktop Runtime is not available as an MSIX framework
package, which is why the MSIX and portable packages are self-contained and the prerequisite applies only to the
framework-dependent ZIP.

## .NET 10 Desktop Runtime prerequisite

`packaging/prerequisites/Install-DotNetDesktopRuntime.ps1`:

1. Detects `Microsoft.WindowsDesktop.App` 10.x for the architecture (shared-framework folders under
   `%ProgramFiles%\dotnet\shared` and `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\<arch>\sharedfx`). WPF needs the
   Desktop Runtime, not only the base runtime.
2. If missing, reads Microsoft's release metadata (`https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`)
   for the latest Windows Desktop Runtime installer for `win-<arch>`, downloads it from the Microsoft host named in
   the metadata and checks the SHA-512 hash published there. `-OfflineInstallerPath` uses a copy obtained earlier.
3. Verifies the Authenticode signature is valid and issued to Microsoft Corporation before running anything.
4. Installs silently with elevation (`/install /quiet /norestart`), handles 3010/1641 (restart), 1638, 1602 and 1618,
   detects again, logs to `%TEMP%\SecurePlus-DotNetDesktopRuntime-*.log` and exits 0, 3010 or 1. It never removes or
   downgrades other runtimes.

## Building on Windows

```powershell
./scripts/package-windows.ps1 -CertificatePath .\release-signing.pfx -CertificatePassword $env:MSIX_CERTIFICATE_PASSWORD
```

For engineering-only unsigned validation:

```powershell
./scripts/package-windows.ps1 -UnsignedMsix
```

The script needs the .NET 10 SDK, Inno Setup 6 for the installer (`choco install innosetup`; `-SkipInstaller` skips
it) and, for the MSIX, the Windows SDK (`makeappx.exe`, `signtool.exe`). `makeappx.exe`
and `signtool.exe` are found on PATH or in the newest `Windows Kits\10\bin\<version>\x64` folder. Production MSIX
packages must be signed with the approved Global Data Systems certificate.

The release workflow runs on `vMAJOR.MINOR.PATCH` tags (the tag must match `build/Version.props`). It runs the tests, installs Inno Setup when the runner lacks it, and with the
`MSIX_CERTIFICATE_BASE64` and `MSIX_CERTIFICATE_PASSWORD` secrets publishes a signed installer and MSIX plus the
ZIPs; without them it skips the MSIX and publishes an unsigned pre-release with the installer and ZIPs. Unsigned
executables can be blocked by SmartScreen or Defender attack surface reduction rules until they are signed.

## Identity and versioning

MSIX identity `GlobalDataSystems.SecurePlus.ModernWork`, publisher `CN=Global Data Systems`, display name
`GDS Secure Plus agent`, executable `EngineeringWorkspace.Desktop.exe` (ADR-0006). The version in
`build/Version.props` is used for assemblies, the status bar, artifact names and release tags.

## Runtime data and upgrades

The installed application never writes to its install folder (ADR-0011). On first launch it creates
`%LOCALAPPDATA%\Global Data Systems\Secure Plus Agent` with `config\` (INI and JSON templates copied only when
missing, from the installation folder, or from the copies embedded in the application) and `data\` (`Workspaces`, `Customers`, `GoldenTenants`, `Imports`, `Exports`, `Logs`, `Audit`, `Credentials`,
`State`). For MSIX installs Windows may redirect these writes to the package's private `LocalCache`; behaviour is the
same. User configuration, SQLite files, logs, audit records and DPAPI material survive upgrades. Packages contain
only non-secret templates and compiled defaults.

The installer copies only the application; on first start the application moves data of earlier versions from
`%LOCALAPPDATA%\Global Data Systems\Secure Plus - Modern Work` to the new folder and upgrades the old default names
in existing configuration files.

## Branding asset status

No approved production icon or logo was found. `packaging/msix/Assets/*` are temporary generated assets, and
`Branding.ini` points at `./assets/...` files that are not shipped yet, so the application logs a
`BrandingAssetMissing` warning and runs without them. Add approved Global Data Systems artwork before release.
