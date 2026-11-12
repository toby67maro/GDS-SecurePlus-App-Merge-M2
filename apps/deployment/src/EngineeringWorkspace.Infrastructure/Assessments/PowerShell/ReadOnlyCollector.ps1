<#
    GDS Secure Plus read-only tenant collector (4.0 beta.2).

    Reads Exchange Online, Security & Compliance (Purview) and Teams settings with app-only certificate authentication.
    Started by the Secure Plus app with PowerShell 7; everything it needs arrives as one JSON document on standard input:
      tenantId, appId, organization (the tenant's onmicrosoft.com domain), cloud, pkcs12 (base64), exchange[], compliance[], teams[],
      exchangeModuleVersion, teamsModuleVersion.
    The certificate exists only in memory. Only Get-* cmdlets from the lists are run, and Exchange and Security & Compliance sessions
    load only those cmdlets (-CommandName). The app registration holds the Global Reader role, so nothing can be changed.
    Output: one JSON line per dataset: {"dataset":"exo:Get-OrganizationConfig","status":"Collected","items":[...]}
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$WarningPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'

$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
$bytes = [Convert]::FromBase64String($request.pkcs12)
$request.pkcs12 = $null
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($bytes, [string]$null,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
[Array]::Clear($bytes, 0, $bytes.Length)

function Write-Dataset([string]$Name, [string]$Status, $Items, [string]$Message) {
    $line = [ordered]@{ dataset = $Name; status = $Status; items = @($Items); message = $Message }
    [Console]::Out.WriteLine(($line | ConvertTo-Json -Depth 6 -Compress -EnumsAsStrings -WarningAction SilentlyContinue))
}

function Write-Unavailable([string]$Prefix, [string[]]$Cmdlets, [string]$Status, [string]$Message) {
    foreach ($cmdlet in $Cmdlets) { Write-Dataset "${Prefix}:$cmdlet" $Status @() $Message }
}

# The most useful line of an error: the last AADSTS message if there is one, else the first line; at most 300 characters.
function Get-Reason([System.Management.Automation.ErrorRecord]$Record) {
    $text = "$($Record.Exception)"
    $aadsts = [regex]::Matches($text, 'AADSTS\d+:[^\r\n"]*?(?=\s+Trace ID|\.\s|"|$)')
    $reason = if ($aadsts.Count -gt 0) { $aadsts[$aadsts.Count - 1].Value } else { ("$($Record.Exception.Message)" -split "`n")[0] }
    if ($reason.Length -gt 300) { $reason = $reason.Substring(0, 300) }
    return $reason.Trim().TrimEnd('.') + '.'
}

function Get-Status([System.Management.Automation.ErrorRecord]$Record) {
    $text = "$($Record.Exception)"
    if ($text -match 'not recognized|is not present|CommandNotFound|not found in the current session') { return 'NotAvailable' }
    if ($text -match 'Access is denied|Unauthorized|403|insufficient|not have permission|AADSTS') { return 'Forbidden' }
    return 'Failed'
}

function Invoke-Cmdlets([string]$Prefix, [string[]]$Cmdlets) {
    foreach ($cmdlet in $Cmdlets) {
        if ($cmdlet -notmatch '^Get-[A-Za-z0-9]+$') { Write-Dataset "${Prefix}:$cmdlet" 'Failed' @() 'Only Get- cmdlets are allowed.'; continue }
        try {
            $items = @(& $cmdlet -ErrorAction Stop | Select-Object -First 500 | ForEach-Object {
                $_ | Select-Object -Property * -ExcludeProperty RunspaceId, PSComputerName, PSShowComputerName, PSSourceJobInstanceId
            })
            Write-Dataset "${Prefix}:$cmdlet" 'Collected' $items $null
        }
        catch {
            Write-Dataset "${Prefix}:$cmdlet" (Get-Status $_) @() ("$cmdlet failed: " + (Get-Reason $_))
        }
    }
}

function Import-Pinned([string]$Name, [string]$Version) {
    try {
        Import-Module -Name $Name -RequiredVersion $Version -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null
        return $null
    }
    catch {
        return "PowerShell module $Name $Version is not installed. Run: Install-Module $Name -RequiredVersion $Version -Scope CurrentUser"
    }
}

$exchangeEnvironment = switch ($request.cloud) { 'GccHigh' { 'O365USGovGCCHigh' } 'DoD' { 'O365USGovDoD' } default { 'O365Default' } }

# Exchange Online and Security & Compliance (ExchangeOnlineManagement).
# Only Get- cmdlets are ever run, whatever the request says.
$exchange = @($request.exchange | Where-Object { $_ -match '^Get-[A-Za-z0-9]+$' })
$compliance = @($request.compliance | Where-Object { $_ -match '^Get-[A-Za-z0-9]+$' })
$missing = Import-Pinned 'ExchangeOnlineManagement' $request.exchangeModuleVersion
if ($missing) {
    Write-Unavailable 'exo' $exchange 'NotAvailable' $missing
    Write-Unavailable 'scc' $compliance 'NotAvailable' $missing
}
else {
    if ($exchange.Count -gt 0) {
        try {
            Connect-ExchangeOnline -AppId $request.appId -Certificate $certificate -Organization $request.organization -ExchangeEnvironmentName $exchangeEnvironment `
                -CommandName $exchange -ShowBanner:$false -SkipLoadingCmdletHelp -ErrorAction Stop | Out-Null
            Invoke-Cmdlets 'exo' $exchange
        }
        catch {
            Write-Unavailable 'exo' $exchange 'Forbidden' ("Exchange Online sign-in failed: " + (Get-Reason $_) +
                " Check that the Read registration has Exchange.ManageAsApp and the Global Reader role (Update permissions).")
        }
        finally {
            Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
        }
    }

    if ($compliance.Count -gt 0) {
        try {
            $ipps = @{ AppId = $request.appId; Certificate = $certificate; Organization = $request.organization; CommandName = $compliance; ShowBanner = $false }
            if ($request.cloud -eq 'GccHigh') {
                $ipps.ConnectionUri = 'https://ps.compliance.protection.office365.us/powershell-liveid/'
                $ipps.AzureADAuthorizationEndpointUri = 'https://login.microsoftonline.us/organizations'
            }
            elseif ($request.cloud -eq 'DoD') {
                $ipps.ConnectionUri = 'https://l5.ps.compliance.protection.office365.us/powershell-liveid/'
                $ipps.AzureADAuthorizationEndpointUri = 'https://login.microsoftonline.us/organizations'
            }
            Connect-IPPSSession @ipps -ErrorAction Stop | Out-Null
            Invoke-Cmdlets 'scc' $compliance
        }
        catch {
            Write-Unavailable 'scc' $compliance 'Forbidden' ("Security & Compliance sign-in failed: " + (Get-Reason $_) +
                " Check that the Read registration has Exchange.ManageAsApp (Microsoft Exchange Online Protection) and the Global Reader role.")
        }
        finally {
            Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
        }
    }
}

# Teams (MicrosoftTeams).
$teams = @($request.teams | Where-Object { $_ -match '^Get-[A-Za-z0-9]+$' })
if ($teams.Count -gt 0) {
    $missing = Import-Pinned 'MicrosoftTeams' $request.teamsModuleVersion
    if ($missing) {
        Write-Unavailable 'teams' $teams 'NotAvailable' $missing
    }
    else {
        try {
            $connect = @{ TenantId = $request.tenantId; ApplicationId = $request.appId; Certificate = $certificate }
            if ($request.cloud -eq 'GccHigh') { $connect.TeamsEnvironmentName = 'TeamsGCCH' }
            elseif ($request.cloud -eq 'DoD') { $connect.TeamsEnvironmentName = 'TeamsDOD' }
            Connect-MicrosoftTeams @connect -ErrorAction Stop | Out-Null
            Invoke-Cmdlets 'teams' $teams
        }
        catch {
            Write-Unavailable 'teams' $teams 'Forbidden' ("Teams sign-in failed: " + (Get-Reason $_) +
                " Check that the Read registration has the Global Reader role.")
        }
        finally {
            Disconnect-MicrosoftTeams -ErrorAction SilentlyContinue | Out-Null
        }
    }
}

$certificate.Dispose()
