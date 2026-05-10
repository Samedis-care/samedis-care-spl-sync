<#
.SYNOPSIS
    Disables Windows 11 bloat for a developer/test VM.

.DESCRIPTION
    Tunes a fresh Windows 11 Pro VM (e.g. UTM/Parallels/Fusion on Apple Silicon)
    for the SamedisCare.SplSync test scenario: only Actimed plus the Tray/Service
    EXE run inside the VM, everything else is on the host Mac.

    Idempotent: can be run multiple times.

    NOT for production end-customer notebooks. Many of the changes here
    (relaxed Defender, disabled Search) are explicitly unwanted in production.

.PARAMETER ExcludePaths
    Optional extra paths added to Defender exclusions. Defaults already cover
    the SplSync ProgramData folder and a couple of typical Actimed install paths.

.EXAMPLE
    # Open as Administrator:
    powershell -ExecutionPolicy Bypass -File .\tools\debloat-windev-vm.ps1

.EXAMPLE
    .\tools\debloat-windev-vm.ps1 -ExcludePaths "C:\Program Files (x86)\Actimed3","D:\Repos\samedis-care-spl-sync"

.NOTES
    Encoding: pure ASCII (no umlauts, no smart quotes) so the script loads
    correctly regardless of PowerShell's default code page interpretation.
#>

[CmdletBinding()]
param(
    [string[]] $ExcludePaths = @()
)

# -----------------------------------------------------------------------------
# Pre-checks
# -----------------------------------------------------------------------------

function Assert-Admin {
    $current = [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $current.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Error "Please run inside an elevated (Administrator) PowerShell."
        exit 1
    }
}
Assert-Admin

function Step([string]$message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Ok([string]$message)   { Write-Host "    OK   $message" -ForegroundColor Green }
function Warn([string]$message) { Write-Host "    WARN $message" -ForegroundColor Yellow }
function Skip([string]$message) { Write-Host "    SKIP $message" -ForegroundColor DarkGray }

# -----------------------------------------------------------------------------
# 1) Disable Windows services that have no place in a dev VM
# -----------------------------------------------------------------------------

Step "Disable services (Search, SysMain, DiagTrack, ...)"

$servicesToDisable = @(
    @{ Name = 'WSearch';             Reason = 'Full-text indexer (heavy disk IO in VM)' },
    @{ Name = 'SysMain';             Reason = 'Superfetch (counterproductive in VMs)' },
    @{ Name = 'DiagTrack';           Reason = 'Connected User Experiences and Telemetry' },
    @{ Name = 'dmwappushservice';    Reason = 'WAP push routing for telemetry' },
    @{ Name = 'MapsBroker';          Reason = 'Offline maps (irrelevant)' },
    @{ Name = 'Fax';                 Reason = 'Fax service' },
    @{ Name = 'WbioSrvc';            Reason = 'Biometrics (no webcam in VM)' },
    @{ Name = 'TabletInputService';  Reason = 'Pen/touch input' },
    @{ Name = 'WerSvc';              Reason = 'Windows Error Reporting' },
    @{ Name = 'PcaSvc';              Reason = 'Program Compatibility Assistant' },
    @{ Name = 'RetailDemo';          Reason = 'Demo mode for in-store devices' },
    @{ Name = 'XblAuthManager';      Reason = 'Xbox Live auth' },
    @{ Name = 'XblGameSave';         Reason = 'Xbox Live game save' },
    @{ Name = 'XboxGipSvc';          Reason = 'Xbox accessory management' },
    @{ Name = 'XboxNetApiSvc';       Reason = 'Xbox Live networking' }
)

foreach ($svc in $servicesToDisable) {
    $s = Get-Service -Name $svc.Name -ErrorAction SilentlyContinue
    if ($null -eq $s) { Skip "$($svc.Name) (not present)"; continue }
    try {
        if ($s.Status -ne 'Stopped') { Stop-Service -Name $svc.Name -Force -ErrorAction Stop }
        Set-Service -Name $svc.Name -StartupType Disabled -ErrorAction Stop
        Ok "$($svc.Name) - $($svc.Reason)"
    } catch {
        Warn "$($svc.Name): $($_.Exception.Message)"
    }
}

# -----------------------------------------------------------------------------
# 2) Visual effects: 'Adjust for best performance'
# -----------------------------------------------------------------------------

Step "Visual effects on performance mode"

# 2 = best looks, 3 = best performance, 0 = let user choose
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' `
    -Name VisualFXSetting -Value 3 -Type DWord -Force

# UserPreferencesMask bitfield (mostly off, except basic legibility flags).
$bytes = [byte[]](0x90,0x12,0x03,0x80,0x10,0x00,0x00,0x00)
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' `
    -Name UserPreferencesMask -Value $bytes -Force
Ok "Performance mode applied (effective after re-login)"

# Window minimize animation off
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop\WindowMetrics' `
    -Name MinAnimate -Value '0' -Type String -Force
Ok "Window-minimize animation off"

# Transparency off
Set-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize' `
    -Name EnableTransparency -Value 0 -Type DWord -Force
Ok "Transparency off"

# -----------------------------------------------------------------------------
# 3) Hibernate off
# -----------------------------------------------------------------------------

Step "Disable hibernate (saves disk + relieves IO stack)"
try {
    powercfg.exe -h off | Out-Null
    Ok "powercfg -h off"
} catch { Warn $_.Exception.Message }

# -----------------------------------------------------------------------------
# 4) Memory compression off
# -----------------------------------------------------------------------------

Step "Disable memory compression (often a net loss in emulated VMs)"
try {
    Disable-MMAgent -mc -ErrorAction Stop | Out-Null
    Ok "Memory compression disabled"
} catch {
    Warn "MMAgent: $($_.Exception.Message)"
}

# -----------------------------------------------------------------------------
# 5) Power plan: high performance
# -----------------------------------------------------------------------------

Step "Power plan: High Performance"
$schemes = powercfg.exe /list
$highPerf = ($schemes | Select-String -Pattern 'High performance|Hoechstleistung|Hochstleistung' | Select-Object -First 1)
if ($highPerf -and $highPerf -match '([0-9a-f\-]{36})') {
    powercfg.exe /setactive $matches[1] | Out-Null
    Ok "Active: High Performance ($($matches[1]))"
} else {
    # Duplicate the High Performance template if it does not exist
    powercfg.exe -duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c | Out-Null
    powercfg.exe /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c | Out-Null
    Ok "High Performance plan duplicated and activated"
}

# -----------------------------------------------------------------------------
# 6) Telemetry / data collection minimized
# -----------------------------------------------------------------------------

Step "Telemetry to minimum"

$dataCollection = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection'
New-Item -Path $dataCollection -Force | Out-Null
Set-ItemProperty -Path $dataCollection -Name AllowTelemetry -Value 0 -Type DWord -Force
Ok "Policy: AllowTelemetry=0 (Security)"

# Advertising ID off
Set-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo' `
    -Name Enabled -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
Ok "Advertising ID off"

# -----------------------------------------------------------------------------
# 7) Defender: relax cloud + sample submission, add exclusions
#    NOTE: only safe to do on a throwaway test VM, NOT on production machines.
# -----------------------------------------------------------------------------

Step "Relax Defender (TEST VM ONLY, never do this in production!)"

try {
    Set-MpPreference -MAPSReporting Disabled
    Set-MpPreference -SubmitSamplesConsent NeverSend
    Set-MpPreference -DisableArchiveScanning $true
    Set-MpPreference -DisableScriptScanning $true
    Ok "MAPS=Disabled, SubmitSamples=NeverSend, ArchiveScan/ScriptScan off"
} catch {
    Warn "Set-MpPreference: $($_.Exception.Message) (Tamper Protection on?)"
}

# Realtime monitoring: Microsoft blocks this when Tamper Protection is on.
# Try anyway; if it works great, otherwise log and move on.
try {
    Set-MpPreference -DisableRealtimeMonitoring $true -ErrorAction Stop
    Ok "Defender real-time protection off"
} catch {
    Warn "Real-time protection still on (Tamper Protection blocks this)."
    Warn "  Manually: Windows Security -> Device Security -> Tamper Protection: off"
}

# Default exclusions for our use-case
$defaultExcludes = @(
    "$env:ProgramData\SamedisCare\SplSync",
    "C:\Program Files (x86)\Actimed3",
    "C:\Program Files\Actimed3",
    "C:\Users\Public\Documents\Actimed",
    "$env:USERPROFILE\Documents\Actimed"
)
$allExcludes = $defaultExcludes + $ExcludePaths | Where-Object { $_ } | Select-Object -Unique
foreach ($p in $allExcludes) {
    try {
        Add-MpPreference -ExclusionPath $p -ErrorAction Stop
        Ok "Defender exclusion: $p"
    } catch {
        Warn "Exclusion '$p': $($_.Exception.Message)"
    }
}

# Process exclusions, so Defender does not scan our binaries on every load.
$defaultProcessExcludes = @(
    'SamedisCare.SplSync.Service.exe',
    'SamedisCare.SplSync.Tray.exe',
    'Actimed.exe',
    'msaccess.exe'
)
foreach ($exe in $defaultProcessExcludes) {
    try {
        Add-MpPreference -ExclusionProcess $exe -ErrorAction Stop
        Ok "Defender process exclusion: $exe"
    } catch { Warn "$exe : $($_.Exception.Message)" }
}

# -----------------------------------------------------------------------------
# 8) Taskbar cleanup: widgets, copilot, search, task view
# -----------------------------------------------------------------------------

Step "Clean up taskbar"

$tb = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
Set-ItemProperty -Path $tb -Name TaskbarDa            -Value 0 -Type DWord -Force # widgets
Set-ItemProperty -Path $tb -Name TaskbarMn            -Value 0 -Type DWord -Force # chat (Teams consumer)
Set-ItemProperty -Path $tb -Name ShowTaskViewButton   -Value 0 -Type DWord -Force
Set-ItemProperty -Path $tb -Name SearchboxTaskbarMode -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue

$search = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search'
New-Item -Path $search -Force | Out-Null
Set-ItemProperty -Path $search -Name SearchboxTaskbarMode -Value 0 -Type DWord -Force
Ok "Widgets / Chat / Task View / Search field off"

# Copilot off
$copilot = 'HKCU:\Software\Policies\Microsoft\Windows\WindowsCopilot'
New-Item -Path $copilot -Force | Out-Null
Set-ItemProperty -Path $copilot -Name TurnOffWindowsCopilot -Value 1 -Type DWord -Force
Ok "Copilot disabled"

# Suggested content / ads in Settings and Start
$cdm = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
$cdmKeys = @(
    'ContentDeliveryAllowed', 'OemPreInstalledAppsEnabled', 'PreInstalledAppsEnabled',
    'PreInstalledAppsEverEnabled', 'SilentInstalledAppsEnabled',
    'SubscribedContent-310093Enabled', 'SubscribedContent-338387Enabled',
    'SubscribedContent-338388Enabled', 'SubscribedContent-338389Enabled',
    'SubscribedContent-338393Enabled', 'SubscribedContent-353694Enabled',
    'SubscribedContent-353696Enabled', 'SystemPaneSuggestionsEnabled'
)
foreach ($k in $cdmKeys) {
    Set-ItemProperty -Path $cdm -Name $k -Value 0 -Type DWord -Force -ErrorAction SilentlyContinue
}
Ok "Suggestion / ad surfaces off in Start, Settings, lock screen"

# -----------------------------------------------------------------------------
# 9) Autostart cleanup: typical suspects
# -----------------------------------------------------------------------------

Step "Inspect Run keys"

$runKeys = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
)
foreach ($rk in $runKeys) {
    if (-not (Test-Path $rk)) { continue }
    Get-ItemProperty -Path $rk | ForEach-Object {
        $_.PSObject.Properties |
            Where-Object { $_.Name -notin @('PSPath','PSParentPath','PSChildName','PSDrive','PSProvider') } |
            ForEach-Object {
                if ($_.Value -match 'OneDrive|Teams|Edge|Spotify|Cortana|YourPhone|GameBar') {
                    try {
                        Remove-ItemProperty -Path $rk -Name $_.Name -ErrorAction Stop
                        Ok "Removed autostart: $($_.Name)"
                    } catch { Warn "$($_.Name): $($_.Exception.Message)" }
                } else {
                    Skip "Keep: $($_.Name)"
                }
            }
    }
}

# -----------------------------------------------------------------------------
# 10) Uninstall OneDrive
# -----------------------------------------------------------------------------

Step "Uninstall OneDrive"

$onedriveSetup = @(
    "$env:WINDIR\System32\OneDriveSetup.exe",
    "$env:WINDIR\SysWOW64\OneDriveSetup.exe"
)
$found = $onedriveSetup | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($found) {
    Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $found -ArgumentList '/uninstall' -Wait -NoNewWindow -ErrorAction SilentlyContinue
    Ok "OneDrive uninstaller invoked"
} else {
    Skip "OneDriveSetup.exe not found"
}

# -----------------------------------------------------------------------------
# 11) Remove UWP bloat that has no business in a dev VM
# -----------------------------------------------------------------------------

Step "Remove UWP bloat"

$uwpPackagesToRemove = @(
    'Microsoft.BingNews', 'Microsoft.BingWeather', 'Microsoft.GamingApp',
    'Microsoft.GetHelp', 'Microsoft.Getstarted', 'Microsoft.MicrosoftOfficeHub',
    'Microsoft.MicrosoftSolitaireCollection', 'Microsoft.PowerAutomateDesktop',
    'Microsoft.MixedReality.Portal', 'Microsoft.OneDriveSync',
    'Microsoft.People', 'Microsoft.SkypeApp', 'Microsoft.Wallet',
    'Microsoft.WindowsCommunicationsApps', 'Microsoft.WindowsFeedbackHub',
    'Microsoft.WindowsMaps', 'Microsoft.WindowsSoundRecorder',
    'Microsoft.Xbox.TCUI', 'Microsoft.XboxApp', 'Microsoft.XboxGameOverlay',
    'Microsoft.XboxGamingOverlay', 'Microsoft.XboxIdentityProvider',
    'Microsoft.XboxSpeechToTextOverlay', 'Microsoft.YourPhone',
    'Microsoft.ZuneMusic', 'Microsoft.ZuneVideo',
    'Clipchamp.Clipchamp', 'MicrosoftTeams', 'Microsoft.Todos'
)

foreach ($pkg in $uwpPackagesToRemove) {
    $installed = Get-AppxPackage -Name $pkg -AllUsers -ErrorAction SilentlyContinue
    if (-not $installed) { Skip "$pkg (not installed)"; continue }
    try {
        $installed | Remove-AppxPackage -AllUsers -ErrorAction Stop
        Ok "Removed: $pkg"
    } catch {
        Warn "$pkg : $($_.Exception.Message)"
    }
}

# -----------------------------------------------------------------------------
# 12) Restrict background apps system-wide
# -----------------------------------------------------------------------------

Step "Restrict background apps globally"

$bg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications'
New-Item -Path $bg -Force | Out-Null
Set-ItemProperty -Path $bg -Name GlobalUserDisabled -Value 1 -Type DWord -Force

$bgPolicy = 'HKLM:\Software\Policies\Microsoft\Windows\AppPrivacy'
New-Item -Path $bgPolicy -Force | Out-Null
Set-ItemProperty -Path $bgPolicy -Name LetAppsRunInBackground -Value 2 -Type DWord -Force # 2 = Force Deny
Ok "Background apps blocked (policy)"

# -----------------------------------------------------------------------------
# 13) Restart Explorer so taskbar changes take effect
# -----------------------------------------------------------------------------

Step "Restart Explorer"
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
    Start-Process explorer.exe
}
Ok "Explorer restarted"

# -----------------------------------------------------------------------------
Write-Host ""
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host " Done. Recommendation: reboot once,"             -ForegroundColor Cyan
Write-Host " so all settings take effect cleanly."           -ForegroundColor Cyan
Write-Host "===============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Not covered here (manual steps):"
Write-Host "  - Tamper Protection in Windows Security: turn off"
Write-Host "    if you really want Defender real-time disabled."
Write-Host "  - Pause Windows Update if you do not want the VM"
Write-Host "    pulling updates while you are testing."
