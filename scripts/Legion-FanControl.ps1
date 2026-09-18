<#
.SYNOPSIS
    Lenovo Legion S7-15ACH6 fan / thermal control, probing & monitoring tool.

.DESCRIPTION
    A single combined tool for inspecting and managing thermals on the
    Legion S7-15ACH6 (AMD Ryzen 7 5800H + NVIDIA RTX, Windows 10).
    Confirmed platform: Model 82K8, BIOS HACN46WW (2024-11-14).

    HOW TO ACTUALLY SPIN THE FANS (confirmed 2026-08-24, HACN46WW):

        .\Legion-FanControl.ps1 custom max     # 2800 -> 4300 rpm, both fans
        .\Legion-FanControl.ps1 custom off     # release, back to the previous mode

      Modes 1/2/3 are EC-owned curves. Performance raises the curve but
      still idles ~2800 rpm and DROPS Fan_Set_FullSpeed — which is why
      every earlier fan write looked like a firmware refusal. Fan forcing
      is gated behind CUSTOM MODE (SetSmartFanMode(255)).

      Measured on this machine:
        GetSupportThermalMode = 65543 (0x10007) -> bits 0,1,2 + bit 16 =
          Quiet, Balanced, Performance AND Custom. Custom was always there.
        GetCustomModeAbility  = 63 (0x3F)
        Set_Custom_Mode_Status(1) does NOT engage Custom Mode; only
          SetSmartFanMode(255) does. ThermalMode does NOT follow to 255,
          so SmartFanMode is the oracle for Custom, not ThermalMode.
        Fan_Set_Table is NOT usable: FanTable_Len stays 0 on every
          FanID x SensorID pair even inside Custom Mode. Full-speed
          toggle only — no custom curve on this BIOS.
        Lenovo Vantage was running throughout and did NOT interfere.
          -SuspendVantage remains available but was not needed.

    NOTE: unelevated CIM calls into these classes return 'Access denied'
    for READS as well as writes. Everything except 'help' wants an
    elevated terminal.

    Subcommands:
      monitor   Read-only live dashboard: CPU/GPU temp, fan 0/1 RPM, mode.
      probe     Read-only full capability dump of every Lenovo WMI class.
      cap       [percent]  Cap CPU max processor state (powercfg). ADMIN.
      uncap     Restore CPU max processor state to 100%. ADMIN.
      mode      <q|b|p>  Set thermal mode, verified via ThermalMode. ADMIN.
      custom    max | off | status | on | fullspeed <on|off>  Custom Mode. ADMIN.
      force     Attempt Fan_Set_FullSpeed, then VERIFY by RPM readback. ADMIN.
      help      Show usage (default when no subcommand is given).

.EXAMPLE
    .\Legion-FanControl.ps1 monitor
    .\Legion-FanControl.ps1 monitor -IntervalSeconds 1

.EXAMPLE
    # Run from an ELEVATED terminal, on AC power:
    .\Legion-FanControl.ps1 custom max               # fans to 4300 rpm
    .\Legion-FanControl.ps1 custom off               # restore the previous mode

.EXAMPLE
    # Prove whether Lenovo Vantage is reverting the writes:
    .\Legion-FanControl.ps1 force on -SuspendVantage
    .\Legion-FanControl.ps1 custom on -SuspendVantage

.EXAMPLE
    # Run from an ELEVATED terminal:
    .\Legion-FanControl.ps1 cap 99       # cap at ~base clock, no Turbo
    .\Legion-FanControl.ps1 uncap        # restore 100%

.NOTES
    Every write path here reads back and reports an honest OK / PARTIAL /
    FAIL verdict. A bare ReturnValue = 0 is never treated as success.
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('monitor', 'probe', 'cap', 'uncap', 'force', 'mode', 'custom', 'test', 'help')]
    [string]$Command = 'help',

    # Positional arg for `cap <percent>`, `force <on|off>`,
    # `mode <quiet|balanced|performance>`, `custom <on|off|status|fullspeed|curve>`.
    [Parameter(Position = 1)]
    [string]$Value,

    # Second positional arg, used by `custom fullspeed <on|off>` and `custom curve <arg>`.
    [Parameter(Position = 2)]
    [string]$Value2,

    # Refresh interval for `monitor`.
    [int]$IntervalSeconds = 2,

    # `test` timings: seconds per phase, total pulse duration, pulse on/off,
    # and the CPU temperature that aborts any write test.
    [int]$HoldSeconds = 30,
    [int]$DurationSeconds = 60,
    [int]$OnSeconds = 3,
    [int]$OffSeconds = 5,
    [int]$MaxTempC = 90,

    # `test table`: flat fan level (0-10 scale, clamped to 3-10) and byte 0 of
    # the Fan_Set_Table buffer (1 as LenovoLegionToolkit writes, or 255).
    [int]$Level = 5,
    [int]$TableMode = 1,

    # `test powerlimit`: sustained (long-term) CPU limit to try, optional
    # short-term limit (defaults to the same), and -NoLoad to supply your own.
    [int]$Watts = 0,
    [int]$ShortWatts = 0,
    [switch]$NoLoad,

    # Temporarily stop Lenovo Vantage / ImController around the write, then
    # restore them. Used to prove whether Vantage is reverting host writes.
    [switch]$SuspendVantage
)

$ErrorActionPreference = 'Stop'

# --- WMI namespace / class constants -----------------------------------------
$WmiNamespace    = 'root\WMI'
$GamezoneClass   = 'LENOVO_GAMEZONE_DATA'
$FanMethodClass  = 'LENOVO_FAN_METHOD'
$OtherMethodClass = 'LENOVO_OTHER_METHOD'

# Custom Mode sentinel used by SetSmartFanMode on 2021 Legion firmware.
$CustomModeValue = 255

# Stock mode active before 'custom on/max', so 'custom off' can return to it.
# A low-wattage charger locks the EC to Quiet; asking for Balanced is refused.
$ModeBeforeFile = Join-Path $env:LOCALAPPDATA 'LegionFanTray\script-mode-before.txt'

$ModeNames = @{
    1   = 'Quiet'
    2   = 'Balanced'
    3   = 'Performance'
    255 = 'Custom'
}

# Services / processes that hold Lenovo's thermal control ownership.
$VantageServices = @(
    'LenovoVantageService',
    'ImControllerService',
    'LenovoFnAndFunctionKeys'
)
$VantageProcesses = @(
    'LenovoVantage-(LenovoGamingSystemAddin)',
    'LenovoUtilityService',
    'Lenovo.Modern.ImController'
)

# =============================================================================
# Helpers
# =============================================================================

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Admin {
    param([string]$Action)
    if (-not (Test-IsAdmin)) {
        Write-Host ""
        Write-Host "  [!] '$Action' requires an ELEVATED (Run as administrator) terminal." -ForegroundColor Yellow
        Write-Host "      On this machine the Lenovo WMI provider denies even READS to" -ForegroundColor Yellow
        Write-Host "      non-admin processes. Right-click PowerShell -> 'Run as administrator'." -ForegroundColor Yellow
        Write-Host ""
        return $false
    }
    return $true
}

# --- Confirmed-working READ methods (see findings appendix) ------------------

function Get-GamezoneInstance {
    Get-CimInstance -Namespace $WmiNamespace -ClassName $GamezoneClass -ErrorAction Stop
}

function Get-FanMethodInstance {
    Get-CimInstance -Namespace $WmiNamespace -ClassName $FanMethodClass -ErrorAction Stop
}

function Get-OtherMethodInstance {
    Get-CimInstance -Namespace $WmiNamespace -ClassName $OtherMethodClass -ErrorAction Stop
}

# Read a thermal sensor by ID via LENOVO_FAN_METHOD.
# NOTE: value is returned in .CurrentSensorTemperature (degrees C), NOT .Data.
# On the S7-15ACH6 (HACN), SensorID 3 = CPU and SensorID 4 = GPU.
function Get-SensorTemp {
    param([byte]$SensorId)
    try {
        $v = (Get-FanMethodInstance |
            Invoke-CimMethod -MethodName Fan_GetCurrentSensorTemperature -Arguments @{ SensorID = $SensorId }).CurrentSensorTemperature
        if ($null -ne $v -and $v -gt 0) { return [int]$v }
        return $null
    } catch { $null }
}

# CPU temp: GetCPUTemp is broken on this BIOS (returns 0), so fall back to sensor 3.
function Get-CpuTemp {
    try {
        $d = (Get-GamezoneInstance | Invoke-CimMethod -MethodName GetCPUTemp).Data
        if ($null -ne $d -and $d -gt 0) { return [int]$d }
    } catch { }
    return (Get-SensorTemp -SensorId 3)
}

# GPU temp: GetGPUTemp is broken on this BIOS (returns 0), so fall back to sensor 4.
function Get-GpuTemp {
    try {
        $d = (Get-GamezoneInstance | Invoke-CimMethod -MethodName GetGPUTemp).Data
        if ($null -ne $d -and $d -gt 0) { return [int]$d }
    } catch { }
    return (Get-SensorTemp -SensorId 4)
}

function Get-FanRpm {
    param([byte]$FanId)
    try {
        # NOTE: this method returns the value in .CurrentFanSpeed, NOT .Data.
        (Get-FanMethodInstance |
            Invoke-CimMethod -MethodName Fan_GetCurrentFanSpeed -Arguments @{ FanID = $FanId }).CurrentFanSpeed
    } catch { $null }
}

function Get-SmartFanMode {
    try {
        (Get-GamezoneInstance | Invoke-CimMethod -MethodName GetSmartFanMode).Data
    } catch { $null }
}

# ThermalMode is the EFFECTIVE profile the EC actually applies. SmartFanMode can
# be set independently, but if ThermalMode doesn't follow, the mode isn't really
# engaged (e.g. Performance is gated to AC power).
function Get-ThermalMode {
    try {
        (Get-GamezoneInstance | Invoke-CimMethod -MethodName GetThermalMode).Data
    } catch { $null }
}

function Get-FullSpeedStatus {
    try {
        (Get-FanMethodInstance | Invoke-CimMethod -MethodName Fan_Get_FullSpeed).Status
    } catch { $null }
}

# Read one fan curve table. Outside Custom Mode this typically returns
# FanTableSize = 0 with an empty array — that is EXPECTED, not "unsupported".
function Get-FanTable {
    param([byte]$FanId, [byte]$SensorId)
    try {
        $r = Get-FanMethodInstance |
            Invoke-CimMethod -MethodName Fan_Get_Table -Arguments @{ FanID = $FanId; SensorID = $SensorId } -ErrorAction Stop
        return [pscustomobject]@{
            FanId    = $FanId
            SensorId = $SensorId
            Table    = $r.FanTable
            Size     = $r.FanTableSize
            Error    = $null
        }
    }
    catch {
        return [pscustomobject]@{
            FanId    = $FanId
            SensorId = $SensorId
            Table    = $null
            Size     = $null
            Error    = $_.Exception.Message.Trim()
        }
    }
}

# Returns $true if a charger is connected, $false on battery, $null if unknown.
# Asks Windows for the AC line status first: a weak charger that cannot keep up
# leaves Win32_Battery at 1 (discharging) while still plugged in.
# Fallback BatteryStatus: 2 = AC, 3 = fully charged, 6-9 = charging.
function Get-OnAcPower {
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        switch ([string][System.Windows.Forms.SystemInformation]::PowerStatus.PowerLineStatus) {
            'Online'  { return $true }
            'Offline' { return $false }
        }
    } catch { }
    try {
        $b = Get-CimInstance Win32_Battery -ErrorAction Stop | Select-Object -First 1
        if ($null -eq $b) { return $true }  # desktop / no battery => effectively AC
        return ([int]$b.BatteryStatus -in 2, 3, 6, 7, 8, 9)
    } catch { $null }
}

function Get-BatteryCharge {
    try { (Get-CimInstance Win32_Battery -ErrorAction Stop | Select-Object -First 1).EstimatedChargeRemaining }
    catch { $null }
}

function Format-Mode {
    param($ModeValue)
    if ($null -eq $ModeValue) { return 'n/a' }
    $name = $ModeNames[[int]$ModeValue]
    if ($name) { "$ModeValue ($name)" } else { "$ModeValue (unknown)" }
}

function Format-Reading {
    param($Value, [string]$Unit)
    if ($null -eq $Value) { 'n/a' } else { "$Value$Unit" }
}

# Render any CIM out-param for the probe dump. Byte arrays become hex so a
# fan table is diffable between runs.
function Format-ProbeValue {
    param($Value)
    if ($null -eq $Value) { return '<null>' }
    if ($Value -is [System.Array]) {
        if ($Value.Count -eq 0) { return '<empty array>' }
        $hex = ($Value | ForEach-Object { '{0:X2}' -f [int]$_ }) -join ' '
        $dec = ($Value | ForEach-Object { [int]$_ }) -join ','
        return "len=$($Value.Count) hex=[$hex] dec=[$dec]"
    }
    return "$Value"
}

# =============================================================================
# Lenovo Vantage suppression
#   Vantage continuously re-asserts thermal state. Suspending it around a write
#   is the only way to tell "firmware refused" apart from "Vantage reverted it".
#   Nothing is permanently disabled — Resume-LenovoVantage always runs in a
#   finally block so an exception or Ctrl+C can't leave the machine crippled.
# =============================================================================

function Suspend-LenovoVantage {
    $stopped = New-Object System.Collections.Generic.List[string]

    Write-Host "   [VANTAGE] Suspending Lenovo thermal-owner services..." -ForegroundColor DarkYellow
    foreach ($name in $VantageServices) {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            Write-Host "             - $name : not installed" -ForegroundColor DarkGray
            continue
        }
        if ($svc.Status -ne 'Running') {
            Write-Host "             - $name : already stopped" -ForegroundColor DarkGray
            continue
        }
        try {
            Stop-Service -Name $name -Force -ErrorAction Stop
            $stopped.Add($name)
            Write-Host "             - $name : stopped" -ForegroundColor DarkYellow
        }
        catch {
            Write-Host "             - $name : COULD NOT STOP ($($_.Exception.Message.Trim()))" -ForegroundColor Red
        }
    }

    foreach ($proc in $VantageProcesses) {
        $running = @(Get-Process -Name $proc -ErrorAction SilentlyContinue)
        if ($running.Count -gt 0) {
            try {
                $running | Stop-Process -Force -ErrorAction Stop
                Write-Host "             - $proc : killed ($($running.Count) proc)" -ForegroundColor DarkYellow
            }
            catch {
                Write-Host "             - $proc : could not kill" -ForegroundColor Red
            }
        }
    }

    Start-Sleep -Seconds 2
    return , $stopped.ToArray()
}

function Resume-LenovoVantage {
    param([string[]]$Stopped)

    if ($null -eq $Stopped -or $Stopped.Count -eq 0) {
        Write-Host "   [VANTAGE] Nothing to restore." -ForegroundColor DarkGray
        return
    }

    Write-Host "   [VANTAGE] Restoring services..." -ForegroundColor DarkYellow
    # Restore in reverse stop order.
    for ($i = $Stopped.Count - 1; $i -ge 0; $i--) {
        $name = $Stopped[$i]
        try {
            Start-Service -Name $name -ErrorAction Stop
            Write-Host "             - $name : started" -ForegroundColor Green
        }
        catch {
            Write-Host "             - $name : FAILED TO RESTART ($($_.Exception.Message.Trim()))" -ForegroundColor Red
            Write-Host "               Start it manually:  Start-Service $name" -ForegroundColor Red
        }
    }
}

# Run a scriptblock with Vantage optionally suspended, always restoring after.
function Invoke-WithVantageSuspended {
    param(
        [Parameter(Mandatory)][scriptblock]$Body,
        [bool]$Suspend
    )

    if (-not $Suspend) { return & $Body }

    $stopped = $null
    try {
        $stopped = Suspend-LenovoVantage
        Write-Host ""
        return & $Body
    }
    finally {
        Write-Host ""
        Resume-LenovoVantage -Stopped $stopped
        Write-Host ""
    }
}

# =============================================================================
# Subcommand: monitor  (READ-ONLY — no system changes)
# =============================================================================

function Invoke-Monitor {
    Write-Host ""
    Write-Host "  Legion S7 Thermal Monitor (read-only)" -ForegroundColor Cyan
    Write-Host "  Refresh: every ${IntervalSeconds}s   |   Press Ctrl+C to stop" -ForegroundColor DarkGray
    if (-not (Test-IsAdmin)) {
        Write-Host "  [!] Not elevated — the Lenovo WMI methods return 'Access denied' to" -ForegroundColor Yellow
        Write-Host "      non-admin processes on this machine. Run from an elevated terminal." -ForegroundColor Yellow
    }
    Write-Host ""

    # Fail fast if the Lenovo WMI provider isn't reachable / readable.
    try { $null = (Get-GamezoneInstance | Invoke-CimMethod -MethodName GetCPUTemp) }
    catch {
        Write-Host "  [x] Cannot read $GamezoneClass in $WmiNamespace." -ForegroundColor Red
        if (-not (Test-IsAdmin)) {
            Write-Host "      Cause: not elevated. Re-run this script as administrator." -ForegroundColor Red
        } else {
            Write-Host "      Detail: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "      The Lenovo WMI provider may be unavailable on this machine." -ForegroundColor Red
        }
        return
    }

    try {
        while ($true) {
            $cpu  = Get-CpuTemp
            $gpu  = Get-GpuTemp
            $fan0 = Get-FanRpm -FanId 0
            $fan1 = Get-FanRpm -FanId 1
            $mode = Get-SmartFanMode
            $full = Get-FullSpeedStatus
            $onAc = Get-OnAcPower
            $batt = Get-BatteryCharge
            $powerStr = if ($onAc -eq $true) { "AC (plugged in)" } elseif ($onAc -eq $false) { "Battery ($batt%)" } else { "unknown" }

            Clear-Host
            Write-Host ""
            Write-Host "  ============================================" -ForegroundColor DarkGray
            Write-Host "   Legion S7-15ACH6  -  Thermal Monitor" -ForegroundColor Cyan
            Write-Host "   $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor DarkGray
            Write-Host "  ============================================" -ForegroundColor DarkGray
            Write-Host ""
            Write-Host ("   CPU temp    : {0}" -f (Format-Reading $cpu ' C'))
            Write-Host ("   GPU temp    : {0}" -f (Format-Reading $gpu ' C'))
            Write-Host ("   Fan 0 RPM   : {0}" -f (Format-Reading $fan0 ' rpm'))
            Write-Host ("   Fan 1 RPM   : {0}" -f (Format-Reading $fan1 ' rpm'))
            Write-Host ("   Fan mode    : {0}" -f (Format-Mode $mode))
            Write-Host ("   Full speed  : {0}" -f (Format-Reading $full ''))
            Write-Host ("   Power       : {0}" -f $powerStr)
            if ($onAc -eq $false) {
                Write-Host "   [!] On battery — Performance mode is locked (AC-only)." -ForegroundColor Yellow
            }
            if ($null -ne $mode -and [int]$mode -eq $CustomModeValue) {
                Write-Host "   [!] CUSTOM MODE is active. Exit with: .\Legion-FanControl.ps1 custom off" -ForegroundColor Magenta
            }
            Write-Host ""
            Write-Host "   (Ctrl+C to stop)" -ForegroundColor DarkGray

            Start-Sleep -Seconds $IntervalSeconds
        }
    }
    finally {
        Write-Host ""
        Write-Host "  Monitor stopped." -ForegroundColor DarkGray
    }
}

# =============================================================================
# Subcommand: probe  (READ-ONLY — full capability dump)
#   Establishes ground truth about what this firmware actually exposes, so
#   the Custom Mode / fan table work is driven by real data, not guesses.
# =============================================================================

$script:ProbeLog = $null

function Write-Probe {
    param([string]$Text = '', [string]$Color = 'Gray')
    Write-Host $Text -ForegroundColor $Color
    if ($null -ne $script:ProbeLog) { [void]$script:ProbeLog.AppendLine($Text) }
}

# Invoke one method and render the result. Never throws — a failing call must
# not abort the sweep.
function Invoke-ProbeMethod {
    param(
        [string]$Class,
        [string]$Method,
        [hashtable]$Arguments,
        [string]$Label
    )

    $display = if ($Label) { $Label } else { $Method }

    try {
        $inst = Get-CimInstance -Namespace $WmiNamespace -ClassName $Class -ErrorAction Stop | Select-Object -First 1
        if ($null -eq $inst) {
            Write-Probe ("    {0,-42} <no instance of {1}>" -f $display, $Class) 'DarkGray'
            return
        }

        $r = if ($Arguments) {
            Invoke-CimMethod -InputObject $inst -MethodName $Method -Arguments $Arguments -ErrorAction Stop
        } else {
            Invoke-CimMethod -InputObject $inst -MethodName $Method -ErrorAction Stop
        }

        $skip = @('PSComputerName', 'CimClass', 'CimInstanceProperties', 'CimSystemProperties')
        $parts = $r.PSObject.Properties |
            Where-Object { $skip -notcontains $_.Name } |
            ForEach-Object { "$($_.Name)=$(Format-ProbeValue $_.Value)" }

        Write-Probe ("    {0,-42} {1}" -f $display, ($parts -join '  ')) 'Gray'
    }
    catch {
        Write-Probe ("    {0,-42} ERROR: {1}" -f $display, $_.Exception.Message.Trim()) 'DarkRed'
    }
}

function Invoke-Probe {
    if (-not (Assert-Admin -Action 'probe')) { return }

    $script:ProbeLog = New-Object System.Text.StringBuilder

    $stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outFile  = Join-Path $PSScriptRoot "probe-$stamp.txt"

    Write-Probe ""
    Write-Probe "  ============================================================" 'DarkGray'
    Write-Probe "   Legion S7-15ACH6 — Lenovo WMI capability probe (read-only)" 'Cyan'
    Write-Probe "   $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" 'DarkGray'
    Write-Probe "  ============================================================" 'DarkGray'
    Write-Probe ""

    # --- Platform identity ---
    Write-Probe "  [PLATFORM]" 'White'
    try {
        $cs   = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        $bios = Get-CimInstance Win32_BIOS -ErrorAction Stop
        Write-Probe ("    {0,-42} {1}" -f 'Model',       $cs.Model)
        Write-Probe ("    {0,-42} {1}" -f 'SKU',         $cs.SystemSKUNumber)
        Write-Probe ("    {0,-42} {1}" -f 'BIOS',        $bios.SMBIOSBIOSVersion)
        Write-Probe ("    {0,-42} {1}" -f 'BIOS date',   $bios.ReleaseDate)
    } catch {
        Write-Probe "    ERROR reading platform info: $($_.Exception.Message.Trim())" 'DarkRed'
    }
    $onAc = Get-OnAcPower
    Write-Probe ("    {0,-42} {1}" -f 'On AC power', $onAc)
    Write-Probe ""

    # --- Lenovo control-owner processes ---
    Write-Probe "  [LENOVO CONTROL-OWNER SERVICES / PROCESSES]" 'White'
    foreach ($name in $VantageServices) {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        $state = if ($svc) { $svc.Status } else { 'not installed' }
        Write-Probe ("    {0,-42} {1}" -f $name, $state)
    }
    $lenovoProcs = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -match 'Lenovo|Legion|Vantage|ImController' })
    if ($lenovoProcs.Count -eq 0) {
        Write-Probe ("    {0,-42} {1}" -f 'running processes', 'none')
    } else {
        foreach ($p in $lenovoProcs) {
            Write-Probe ("    {0,-42} pid {1}" -f $p.ProcessName, $p.Id)
        }
    }
    Write-Probe ""

    # --- Class inventory ---
    Write-Probe "  [LENOVO WMI CLASS INVENTORY: $WmiNamespace]" 'White'
    try {
        $classes = @(Get-CimClass -Namespace $WmiNamespace -ErrorAction Stop |
            Where-Object { $_.CimClassName -like 'LENOVO_*' } |
            Select-Object -ExpandProperty CimClassName |
            Sort-Object)
        foreach ($c in $classes) {
            $mCount = @((Get-CimClass -Namespace $WmiNamespace -ClassName $c -ErrorAction SilentlyContinue).CimClassMethods).Count
            Write-Probe ("    {0,-42} {1} method(s)" -f $c, $mCount)
        }
    }
    catch {
        Write-Probe "    ERROR enumerating classes: $($_.Exception.Message.Trim())" 'DarkRed'
    }
    Write-Probe ""

    # --- LENOVO_OTHER_METHOD: the Custom Mode gate ---
    Write-Probe "  [LENOVO_OTHER_METHOD]  <- Custom Mode capability lives here" 'White'
    Invoke-ProbeMethod -Class $OtherMethodClass -Method 'GetDeviceType'
    Invoke-ProbeMethod -Class $OtherMethodClass -Method 'GetSupportThermalMode'
    Invoke-ProbeMethod -Class $OtherMethodClass -Method 'GetCustomModeAbility'
    Invoke-ProbeMethod -Class $OtherMethodClass -Method 'Get_Legion_Device_Support_Feature'
    Invoke-ProbeMethod -Class $OtherMethodClass -Method 'Get_Device_Current_Support_Feature'
    Invoke-ProbeMethod -Class $OtherMethodClass -Method 'Get_Support_LegionZone_Version'
    Write-Probe ""

    # --- LENOVO_GAMEZONE_DATA ---
    Write-Probe "  [LENOVO_GAMEZONE_DATA]" 'White'
    foreach ($m in @(
        'GetVersion', 'GetProductInfo', 'IsSupportSmartFan', 'GetSmartFanMode',
        'GetSmartFanSetting', 'GetThermalMode', 'GetThermalTableID',
        'IsSupportFanCooling', 'GetFanCoolingStatus', 'GetFanCount',
        'GetFanMaxSpeed', 'GetFan1Speed', 'GetFan2Speed',
        'GetIntelligentSubMode', 'GetTriggerTemperatureValue',
        'GetPowerChargeMode', 'GetHardwareInfoSupportVersion'
    )) {
        Invoke-ProbeMethod -Class $GamezoneClass -Method $m
    }
    Write-Probe ""

    # --- LENOVO_FAN_METHOD ---
    Write-Probe "  [LENOVO_FAN_METHOD]" 'White'
    Invoke-ProbeMethod -Class $FanMethodClass -Method 'Fan_Get_FullSpeed'
    foreach ($fid in 0..2) {
        Invoke-ProbeMethod -Class $FanMethodClass -Method 'Fan_Get_MaxSpeed' `
            -Arguments @{ Fan_ID = [byte]$fid } -Label "Fan_Get_MaxSpeed(Fan_ID=$fid)"
    }
    foreach ($fid in 0..2) {
        Invoke-ProbeMethod -Class $FanMethodClass -Method 'Fan_GetCurrentFanSpeed' `
            -Arguments @{ FanID = [byte]$fid } -Label "Fan_GetCurrentFanSpeed(FanID=$fid)"
    }
    foreach ($sid in 0..5) {
        Invoke-ProbeMethod -Class $FanMethodClass -Method 'Fan_GetCurrentSensorTemperature' `
            -Arguments @{ SensorID = [byte]$sid } -Label "Fan_GetCurrentSensorTemperature(Sensor=$sid)"
    }
    Write-Probe ""

    # --- Fan curve table matrix ---
    Write-Probe "  [FAN CURVE TABLE MATRIX: Fan_Get_Table(FanID x SensorID)]" 'White'
    Write-Probe "    A non-zero FanTableSize here means the curve is readable and" 'DarkGray'
    Write-Probe "    Fan_Set_Table has a real layout to modify. Zero outside Custom" 'DarkGray'
    Write-Probe "    Mode is EXPECTED — re-run this probe after 'custom on'." 'DarkGray'
    $anyTable = $false
    foreach ($fid in 0..2) {
        foreach ($sid in 0..5) {
            $t = Get-FanTable -FanId ([byte]$fid) -SensorId ([byte]$sid)
            if ($t.Error) {
                Write-Probe ("    Fan={0} Sensor={1}   ERROR: {2}" -f $fid, $sid, $t.Error) 'DarkRed'
            }
            else {
                if ($t.Size -gt 0) { $anyTable = $true }
                $color = if ($t.Size -gt 0) { 'Green' } else { 'DarkGray' }
                Write-Probe ("    Fan={0} Sensor={1}   Size={2}  Table={3}" -f `
                    $fid, $sid, (Format-Reading $t.Size ''), (Format-ProbeValue $t.Table)) $color
            }
        }
    }
    Write-Probe ""
    if ($anyTable) {
        Write-Probe "    => At least one fan table IS readable. Fan_Set_Table is viable." 'Green'
    } else {
        Write-Probe "    => No readable fan table in the current mode. Try: custom on" 'Yellow'
    }
    Write-Probe ""

    # --- Raw data classes ---
    Write-Probe "  [RAW DATA CLASSES]" 'White'
    foreach ($cls in @('LENOVO_FAN_TABLE_DATA', 'LENOVO_FAN_MAX_SPEED_DATA', 'LENOVO_INTELLIGENT_OP_LIST')) {
        Write-Probe "    --- $cls ---" 'DarkCyan'
        try {
            $instances = @(Get-CimInstance -Namespace $WmiNamespace -ClassName $cls -ErrorAction Stop)
            if ($instances.Count -eq 0) {
                Write-Probe "      <no instances>" 'DarkGray'
            }
            foreach ($inst in $instances) {
                foreach ($p in $inst.CimInstanceProperties) {
                    Write-Probe ("      {0,-38} {1}" -f $p.Name, (Format-ProbeValue $p.Value))
                }
                Write-Probe "      -" 'DarkGray'
            }
        }
        catch {
            Write-Probe "      ERROR: $($_.Exception.Message.Trim())" 'DarkRed'
        }
    }
    Write-Probe ""

    # --- Save ---
    try {
        $script:ProbeLog.ToString() | Set-Content -Path $outFile -Encoding UTF8
        Write-Host "  [SAVED] $outFile" -ForegroundColor Green
        Write-Host "          Re-run after 'custom on' and diff the two files." -ForegroundColor DarkGray
    }
    catch {
        Write-Host "  [!] Could not write $outFile : $($_.Exception.Message.Trim())" -ForegroundColor Yellow
    }
    Write-Host ""

    $script:ProbeLog = $null
}

# =============================================================================
# Subcommand: cap / uncap  (WRITE — powercfg, requires ADMIN)
#   Reduce heat at the source so the fans don't need to ramp.
# =============================================================================

function Set-CpuCap {
    param([int]$Percent)

    if (-not (Assert-Admin -Action 'cap')) { return }

    if ($Percent -lt 1 -or $Percent -gt 100) {
        Write-Host "  [x] Percent must be 1-100 (got $Percent)." -ForegroundColor Red
        return
    }

    Write-Host ""
    Write-Host "  [WRITE] Capping CPU max processor state to ${Percent}% (powercfg)..." -ForegroundColor Yellow

    # PROCTHROTTLEMAX under SUB_PROCESSOR, applied to AC and DC on the active scheme.
    & powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX $Percent | Out-Null
    & powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX $Percent | Out-Null
    & powercfg /setactive SCHEME_CURRENT | Out-Null

    Write-Host "      Done. Verifying..." -ForegroundColor DarkGray
    Write-Host ""

    $query = (& powercfg /query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX) -join "`n"
    $acHex = ([regex]::Match($query, 'Current AC Power Setting Index:\s*(0x[0-9a-fA-F]+)')).Groups[1].Value
    $dcHex = ([regex]::Match($query, 'Current DC Power Setting Index:\s*(0x[0-9a-fA-F]+)')).Groups[1].Value
    $acVal = if ($acHex) { [Convert]::ToInt32($acHex, 16) } else { $null }
    $dcVal = if ($dcHex) { [Convert]::ToInt32($dcHex, 16) } else { $null }

    Write-Host ("   AC max processor state : {0}%" -f (Format-Reading $acVal '')) -ForegroundColor Green
    Write-Host ("   DC max processor state : {0}%" -f (Format-Reading $dcVal '')) -ForegroundColor Green
    Write-Host ""
    if ($Percent -le 99) {
        Write-Host "   99% disables Turbo boost (the main thermal driver on the 5800H)." -ForegroundColor DarkGray
        Write-Host "   ~80% is the floor of diminishing returns." -ForegroundColor DarkGray
    }
    Write-Host "   NOTE: the cap is stored PER POWER SCHEME (the currently active one)." -ForegroundColor Yellow
    Write-Host "         Switching power plans drops the cap; re-run 'cap' after switching." -ForegroundColor Yellow
    Write-Host "   Revert with: .\Legion-FanControl.ps1 uncap" -ForegroundColor DarkGray
    Write-Host ""
}

function Reset-CpuCap {
    if (-not (Assert-Admin -Action 'uncap')) { return }

    Write-Host ""
    Write-Host "  [WRITE] Restoring CPU max processor state to 100% (powercfg)..." -ForegroundColor Yellow

    & powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100 | Out-Null
    & powercfg /setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100 | Out-Null
    & powercfg /setactive SCHEME_CURRENT | Out-Null

    Write-Host "      Done. CPU cap removed (Turbo re-enabled) on the active power scheme." -ForegroundColor Green
    Write-Host "      NOTE: only the currently active scheme was changed." -ForegroundColor DarkGray
    Write-Host ""
}

# =============================================================================
# Subcommand: force  (WRITE — attempt Fan_Set_FullSpeed, then VERIFY honestly)
#   In modes 1/2/3 the EC owns the curve and drops this write. It is expected
#   to work only inside Custom Mode — see 'custom fullspeed'.
# =============================================================================

function Set-FanFullSpeed {
    <#
        Core full-speed write + RPM verification. Returns $true if the EC
        demonstrably reacted (RPM moved more than the threshold on either fan).
        Shared by 'force' and 'custom fullspeed' so there is exactly one
        verification implementation.
    #>
    param(
        [bool]$On,
        [int]$SettleSeconds = 8,
        [int]$Threshold = 150
    )

    # --- Baseline readback (before) ---
    $rpm0Before = Get-FanRpm -FanId 0
    $rpm1Before = Get-FanRpm -FanId 1
    $modeBefore = Get-SmartFanMode

    Write-Host ("   BEFORE  ->  Fan0: {0}   Fan1: {1}   Mode: {2}" -f `
        (Format-Reading $rpm0Before ' rpm'), (Format-Reading $rpm1Before ' rpm'), (Format-Mode $modeBefore)) `
        -ForegroundColor DarkGray

    # --- Attempt the write ---
    try {
        $null = Get-FanMethodInstance |
            Invoke-CimMethod -MethodName Fan_Set_FullSpeed -Arguments @{ Status = $On }
        Write-Host "   WRITE   ->  Fan_Set_FullSpeed($On) returned without error." -ForegroundColor DarkGray
    }
    catch {
        Write-Host "   WRITE   ->  Fan_Set_FullSpeed threw: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }

    Write-Host "   Waiting ${SettleSeconds}s for the EC to react..." -ForegroundColor DarkGray
    Start-Sleep -Seconds $SettleSeconds

    # --- Verification readback (after) ---
    $rpm0After  = Get-FanRpm -FanId 0
    $rpm1After  = Get-FanRpm -FanId 1
    $modeAfter  = Get-SmartFanMode
    $fullAfter  = Get-FullSpeedStatus

    Write-Host ("   AFTER   ->  Fan0: {0}   Fan1: {1}   Mode: {2}   FullSpeedFlag: {3}" -f `
        (Format-Reading $rpm0After ' rpm'), (Format-Reading $rpm1After ' rpm'), `
        (Format-Mode $modeAfter), (Format-Reading $fullAfter '')) `
        -ForegroundColor DarkGray
    Write-Host ""

    # --- Honest verdict based on RPM delta ---
    $delta0 = if ($null -ne $rpm0Before -and $null -ne $rpm0After) { [math]::Abs($rpm0After - $rpm0Before) } else { $null }
    $delta1 = if ($null -ne $rpm1Before -and $null -ne $rpm1After) { [math]::Abs($rpm1After - $rpm1Before) } else { $null }

    $tookEffect = (($null -ne $delta0) -and ($delta0 -gt $Threshold)) -or `
                  (($null -ne $delta1) -and ($delta1 -gt $Threshold))

    if ($tookEffect) {
        Write-Host "  [OK] RPM changed after the write (Fan0 delta: $delta0, Fan1 delta: $delta1)." -ForegroundColor Green
        Write-Host "       The EC ACCEPTED Fan_Set_FullSpeed." -ForegroundColor Green
    }
    else {
        Write-Host "  [NO-OP] No meaningful RPM change (Fan0 delta: $delta0, Fan1 delta: $delta1)." -ForegroundColor Yellow
        Write-Host "          The write returned 'success' but the EC ignored it." -ForegroundColor Yellow
    }

    return $tookEffect
}

function Invoke-ForceFan {
    param([bool]$On)

    if (-not (Assert-Admin -Action 'force')) { return }

    $stateLabel = if ($On) { 'ON (full speed)' } else { 'OFF (release)' }
    $mode = Get-SmartFanMode

    Write-Host ""
    Write-Host "  [WRITE] Attempting Fan_Set_FullSpeed = $On  [$stateLabel]" -ForegroundColor Yellow
    if ($null -ne $mode -and [int]$mode -ne $CustomModeValue) {
        Write-Host "  [!] You are in mode $(Format-Mode $mode), NOT Custom Mode. On 2021 Legion" -ForegroundColor Yellow
        Write-Host "      firmware the EC owns the curve in modes 1/2/3 and drops this write." -ForegroundColor Yellow
        Write-Host "      For the real path, use:  .\Legion-FanControl.ps1 custom fullspeed on" -ForegroundColor Cyan
    }
    Write-Host ""

    $ok = Invoke-WithVantageSuspended -Suspend $SuspendVantage.IsPresent -Body {
        Set-FanFullSpeed -On $On
    }

    if (-not $ok) {
        Write-Host ""
        if (-not $SuspendVantage.IsPresent) {
            Write-Host "          Next: rule out Lenovo Vantage reverting the write —" -ForegroundColor Cyan
            Write-Host "                .\Legion-FanControl.ps1 force on -SuspendVantage" -ForegroundColor Cyan
        }
        Write-Host "          Then: try the Custom Mode path —" -ForegroundColor Cyan
        Write-Host "                .\Legion-FanControl.ps1 custom on" -ForegroundColor Cyan
        Write-Host "                .\Legion-FanControl.ps1 custom fullspeed on" -ForegroundColor Cyan
    }
    Write-Host ""
}

# =============================================================================
# Subcommand: mode  (WRITE — set power/thermal mode, requires ADMIN)
#   SetSmartFanMode works on this BIOS, BUT Performance (3) is gated to AC power:
#   the firmware accepts the SmartFanMode register change while refusing to move
#   the effective ThermalMode unless plugged in. This command detects that.
# =============================================================================

function Invoke-SetMode {
    param([int]$TargetMode)   # 1=Quiet, 2=Balanced, 3=Performance

    if (-not (Assert-Admin -Action 'mode')) { return }

    $name = $ModeNames[$TargetMode]
    Write-Host ""
    Write-Host "  [WRITE] Setting fan/thermal mode -> $TargetMode ($name)" -ForegroundColor Yellow

    $onAc = Get-OnAcPower
    if ($TargetMode -eq 3 -and $onAc -eq $false) {
        Write-Host "  [!] You are on BATTERY. Performance mode is AC-only on this firmware —" -ForegroundColor Yellow
        Write-Host "      it will likely refuse to engage. Plug in the charger first." -ForegroundColor Yellow
    }

    Invoke-WithVantageSuspended -Suspend $SuspendVantage.IsPresent -Body {
        try {
            $null = Get-GamezoneInstance | Invoke-CimMethod -MethodName SetSmartFanMode -Arguments @{ Data = [uint32]$TargetMode }
        } catch {
            Write-Host "  [x] SetSmartFanMode threw: $($_.Exception.Message)" -ForegroundColor Red
            return
        }

        Start-Sleep -Seconds 2
        $smart   = Get-SmartFanMode
        $thermal = Get-ThermalMode

        Write-Host ""
        Write-Host ("   SmartFanMode : {0}" -f (Format-Mode $smart))
        Write-Host ("   ThermalMode  : {0}" -f (Format-Mode $thermal))
        Write-Host ""

        # Verdict: the mode is only REALLY engaged if ThermalMode followed.
        if ([int]$thermal -eq $TargetMode) {
            Write-Host "  [OK] Mode '$name' is engaged (ThermalMode followed)." -ForegroundColor Green
            if ($TargetMode -eq 3) {
                Write-Host "       NOTE: Performance is still a CURVE, not full speed. The EC will" -ForegroundColor DarkGray
                Write-Host "       hold ~3800 rpm until temps demand more. To actually pin the fans" -ForegroundColor DarkGray
                Write-Host "       at max, use:  .\Legion-FanControl.ps1 custom fullspeed on" -ForegroundColor Cyan
            }
        }
        elseif ([int]$smart -eq $TargetMode) {
            Write-Host "  [PARTIAL] SmartFanMode changed, but ThermalMode stayed at $thermal." -ForegroundColor Yellow
            Write-Host "            The firmware accepted the flag but did NOT apply the profile." -ForegroundColor Yellow
            if ($TargetMode -eq 3) {
                Write-Host "            Cause: Performance is gated to AC power. Plug in the charger." -ForegroundColor Cyan
            }
        }
        else {
            Write-Host "  [FAIL] Mode did not change (SmartFanMode still $smart)." -ForegroundColor Red
        }
    }
    Write-Host ""
}

# =============================================================================
# Subcommand: custom  (WRITE — Custom Mode unlock + fan curve, requires ADMIN)
#
#   On 2021 Legion firmware, Fan_Set_FullSpeed and Fan_Set_Table are only
#   honoured inside Custom Mode. Outside it the EC owns the curve, which is
#   why every earlier fan write silently no-op'd.
#
#   SAFETY: Custom Mode hands the fan curve to the host. Leaving the machine
#   in Custom Mode with a bad table is a real thermal risk, so:
#     - 'custom on' saves the previous mode and warns you to run 'custom off'
#     - a failed unlock always restores the previous mode in a finally block
#     - 'custom curve' refuses to write a table it cannot parse, and refuses
#       any curve that is slower than stock at the same temperature
# =============================================================================

function Test-CustomModeEngaged {
    $smart = Get-SmartFanMode
    if ($null -ne $smart -and [int]$smart -eq $CustomModeValue) { return $true }
    $thermal = Get-ThermalMode
    if ($null -ne $thermal -and [int]$thermal -eq $CustomModeValue) { return $true }
    return $false
}

# Report whether any fan table became readable — secondary evidence that
# Custom Mode really engaged, independent of the mode register.
function Get-ReadableFanTables {
    $found = @()
    foreach ($fid in 0..2) {
        foreach ($sid in 0..5) {
            $t = Get-FanTable -FanId ([byte]$fid) -SensorId ([byte]$sid)
            if (-not $t.Error -and $t.Size -gt 0) { $found += $t }
        }
    }
    return , $found
}

function Enter-CustomMode {
    $modeBefore = Get-SmartFanMode

    Write-Host ("   Mode before: {0}" -f (Format-Mode $modeBefore)) -ForegroundColor DarkGray

    if ($null -ne $modeBefore -and [int]$modeBefore -in 1, 2, 3) {
        try {
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $ModeBeforeFile)
            Set-Content -Path $ModeBeforeFile -Value ([int]$modeBefore)
        } catch { }
    }

    if (Test-CustomModeEngaged) {
        Write-Host "  [OK] Already in Custom Mode." -ForegroundColor Green
        return $true
    }

    # Three strategies, in escalating order. Stop at the first that takes.
    $strategies = @(
        @{
            Name   = 'LENOVO_OTHER_METHOD.Set_Custom_Mode_Status(1)'
            Action = { $null = Get-OtherMethodInstance | Invoke-CimMethod -MethodName Set_Custom_Mode_Status -Arguments @{ Status = [byte]1 } }
        },
        @{
            Name   = "LENOVO_GAMEZONE_DATA.SetSmartFanMode($CustomModeValue)"
            Action = { $null = Get-GamezoneInstance | Invoke-CimMethod -MethodName SetSmartFanMode -Arguments @{ Data = [uint32]$CustomModeValue } }
        },
        @{
            Name   = 'Set_Custom_Mode_Status(1) + SetSmartFanMode(255) back-to-back'
            Action = {
                $null = Get-OtherMethodInstance | Invoke-CimMethod -MethodName Set_Custom_Mode_Status -Arguments @{ Status = [byte]1 }
                Start-Sleep -Milliseconds 500
                $null = Get-GamezoneInstance | Invoke-CimMethod -MethodName SetSmartFanMode -Arguments @{ Data = [uint32]$CustomModeValue }
            }
        }
    )

    foreach ($s in $strategies) {
        Write-Host ""
        Write-Host ("   TRY  ->  {0}" -f $s.Name) -ForegroundColor Yellow
        try {
            & $s.Action
            Write-Host "            call returned without error." -ForegroundColor DarkGray
        }
        catch {
            Write-Host "            threw: $($_.Exception.Message.Trim())" -ForegroundColor DarkRed
            continue
        }

        Start-Sleep -Seconds 2
        $smart   = Get-SmartFanMode
        $thermal = Get-ThermalMode
        Write-Host ("            SmartFanMode: {0}   ThermalMode: {1}" -f (Format-Mode $smart), (Format-Mode $thermal)) -ForegroundColor DarkGray

        if (Test-CustomModeEngaged) {
            Write-Host "  [OK] Custom Mode ENGAGED via: $($s.Name)" -ForegroundColor Green
            return $true
        }

        # Secondary evidence: a fan table that was empty is now readable.
        $tables = Get-ReadableFanTables
        if ($tables.Count -gt 0) {
            Write-Host ("  [OK] Mode register unchanged, but {0} fan table(s) became READABLE." -f $tables.Count) -ForegroundColor Green
            Write-Host "       That is the functional signal — Fan_Set_Table now has a layout." -ForegroundColor Green
            return $true
        }
    }

    Write-Host ""
    Write-Host "  [FAIL] Custom Mode did not engage via any strategy." -ForegroundColor Red

    # Restore whatever mode we started in — do not leave a half-applied state.
    if ($null -ne $modeBefore -and [int]$modeBefore -ne $CustomModeValue) {
        try {
            $null = Get-GamezoneInstance | Invoke-CimMethod -MethodName SetSmartFanMode -Arguments @{ Data = [uint32]$modeBefore }
            Write-Host ("         Restored previous mode: {0}" -f (Format-Mode $modeBefore)) -ForegroundColor DarkGray
        } catch { }
    }
    return $false
}

function Exit-CustomMode {
    $target = 2
    try {
        if (Test-Path $ModeBeforeFile) {
            $saved = [int](Get-Content $ModeBeforeFile -TotalCount 1)
            if ($saved -in 1, 2, 3) { $target = $saved }
        }
    } catch { }

    Write-Host ""
    Write-Host ("  [WRITE] Leaving Custom Mode -> {0}" -f (Format-Mode $target)) -ForegroundColor Yellow

    # Release full speed first, so the EC isn't handed a stale override.
    try {
        $null = Get-FanMethodInstance | Invoke-CimMethod -MethodName Fan_Set_FullSpeed -Arguments @{ Status = $false }
        Write-Host "   Fan_Set_FullSpeed(false) sent." -ForegroundColor DarkGray
    } catch {
        Write-Host "   Fan_Set_FullSpeed(false) threw: $($_.Exception.Message.Trim())" -ForegroundColor DarkRed
    }

    try {
        $null = Get-OtherMethodInstance | Invoke-CimMethod -MethodName Set_Custom_Mode_Status -Arguments @{ Status = [byte]0 }
        Write-Host "   Set_Custom_Mode_Status(0) sent." -ForegroundColor DarkGray
    } catch {
        Write-Host "   Set_Custom_Mode_Status(0) threw: $($_.Exception.Message.Trim())" -ForegroundColor DarkRed
    }

    try {
        $null = Get-GamezoneInstance | Invoke-CimMethod -MethodName SetSmartFanMode -Arguments @{ Data = [uint32]$target }
    } catch {
        Write-Host "   SetSmartFanMode($target) threw: $($_.Exception.Message.Trim())" -ForegroundColor Red
    }

    Start-Sleep -Seconds 2
    $smart   = Get-SmartFanMode
    $thermal = Get-ThermalMode
    Write-Host ""
    Write-Host ("   SmartFanMode : {0}" -f (Format-Mode $smart))
    Write-Host ("   ThermalMode  : {0}" -f (Format-Mode $thermal))
    Write-Host ""

    # Out of Custom is what matters. The EC may pick a different stock mode
    # than asked for: a low-wattage charger locks Quiet.
    if ($null -ne $smart -and [int]$smart -eq $target) {
        Write-Host ("  [OK] Back on the firmware's own curve ({0})." -f $ModeNames[$target]) -ForegroundColor Green
        Remove-Item $ModeBeforeFile -ErrorAction SilentlyContinue
    } elseif ($null -ne $smart -and [int]$smart -in 1, 2, 3) {
        Write-Host ("  [OK] Out of Custom Mode. Asked for {0}, the firmware kept {1}" -f $ModeNames[$target], $ModeNames[[int]$smart]) -ForegroundColor Yellow
        Write-Host "       (a low-wattage charger locks Quiet)." -ForegroundColor Yellow
        Remove-Item $ModeBeforeFile -ErrorAction SilentlyContinue
    } else {
        Write-Host ("  [!] Still in Custom Mode (SmartFanMode {0}). Re-run, or set a mode from Vantage." -f (Format-Mode $smart)) -ForegroundColor Red
    }
    Write-Host ""
}

function Show-CustomStatus {
    $smart   = Get-SmartFanMode
    $thermal = Get-ThermalMode
    $full    = Get-FullSpeedStatus
    $tables  = Get-ReadableFanTables

    Write-Host ""
    Write-Host "  Custom Mode status" -ForegroundColor Cyan
    Write-Host "  ------------------" -ForegroundColor DarkGray
    Write-Host ("   SmartFanMode        : {0}" -f (Format-Mode $smart))
    Write-Host ("   ThermalMode         : {0}" -f (Format-Mode $thermal))
    Write-Host ("   FullSpeed flag      : {0}" -f (Format-Reading $full ''))
    Write-Host ("   Custom Mode engaged : {0}" -f (Test-CustomModeEngaged))
    Write-Host ("   Readable fan tables : {0}" -f $tables.Count)
    foreach ($t in $tables) {
        Write-Host ("     Fan={0} Sensor={1}  Size={2}  {3}" -f $t.FanId, $t.SensorId, $t.Size, (Format-ProbeValue $t.Table)) -ForegroundColor DarkGray
    }
    Write-Host ""
}

# Parse a raw fan table into a temperature half + speed half, but ONLY when the
# structure is unambiguous. Returns $null when it cannot be read confidently —
# writing a blind table is how you cook a laptop.
function Split-FanTable {
    param($Bytes)

    if ($null -eq $Bytes -or $Bytes.Count -lt 4) { return $null }
    if ($Bytes.Count % 2 -ne 0) { return $null }

    $half  = $Bytes.Count / 2
    $temps = @($Bytes[0..($half - 1)]      | ForEach-Object { [int]$_ })
    $speed = @($Bytes[$half..($Bytes.Count - 1)] | ForEach-Object { [int]$_ })

    # A temperature column must be monotonically non-decreasing and physically
    # plausible; a speed column must be monotonically non-decreasing too.
    for ($i = 1; $i -lt $temps.Count; $i++) {
        if ($temps[$i] -lt $temps[$i - 1]) { return $null }
    }
    for ($i = 1; $i -lt $speed.Count; $i++) {
        if ($speed[$i] -lt $speed[$i - 1]) { return $null }
    }
    # 0 is legitimate for a leading idle point; anything above 105 C is not a
    # temperature column. Require at least one realistic trip point so an
    # all-zero / all-tiny array isn't mistaken for a curve.
    foreach ($t in $temps) {
        if ($t -lt 0 -or $t -gt 105) { return $null }
    }
    if (-not ($temps | Where-Object { $_ -ge 30 })) { return $null }

    return [pscustomobject]@{ Temps = $temps; Speeds = $speed }
}

function Invoke-CustomCurve {
    param([string]$Arg)

    $tables = Get-ReadableFanTables
    if ($tables.Count -eq 0) {
        Write-Host ""
        if (Test-CustomModeEngaged) {
            # Confirmed on BIOS HACN46WW 2026-08-24: Custom Mode engages and
            # Fan_Set_FullSpeed works, but the curve table stays empty even
            # inside Custom Mode. Fan_Set_Table has nothing to modify here.
            Write-Host "  [x] Custom Mode IS engaged, but this BIOS still exposes no fan curve" -ForegroundColor Red
            Write-Host "      table (FanTable_Len = 0 on every FanID x SensorID pair)." -ForegroundColor Red
            Write-Host "      Fan_Set_Table is not usable on HACN46WW — confirmed, not a guess." -ForegroundColor Red
            Write-Host ""
            Write-Host "      The full-speed toggle DOES work. Use that instead:" -ForegroundColor Cyan
            Write-Host "        .\Legion-FanControl.ps1 custom max" -ForegroundColor Cyan
        }
        else {
            Write-Host "  [x] No readable fan table. Enter Custom Mode first:" -ForegroundColor Red
            Write-Host "      .\Legion-FanControl.ps1 custom on" -ForegroundColor Cyan
        }
        Write-Host ""
        return
    }

    Write-Host ""
    Write-Host "  Stock fan tables (read from firmware):" -ForegroundColor Cyan
    foreach ($t in $tables) {
        Write-Host ("   Fan={0} Sensor={1} Size={2}" -f $t.FanId, $t.SensorId, $t.Size) -ForegroundColor White
        Write-Host ("     raw   {0}" -f (Format-ProbeValue $t.Table)) -ForegroundColor DarkGray
        $split = Split-FanTable -Bytes $t.Table
        if ($split) {
            Write-Host ("     temps {0}"  -f ($split.Temps  -join ' ')) -ForegroundColor DarkGray
            Write-Host ("     speeds {0}" -f ($split.Speeds -join ' ')) -ForegroundColor DarkGray
        } else {
            Write-Host "     (layout not recognised — will not modify this table)" -ForegroundColor Yellow
        }
    }
    Write-Host ""

    # 'dump' is read-only and is the default when no argument is given.
    if ([string]::IsNullOrWhiteSpace($Arg) -or $Arg -match '^(dump|show|read)$') {
        Write-Host "  Read-only dump. To shift the curve earlier (more airflow, sooner):" -ForegroundColor DarkGray
        Write-Host "    .\Legion-FanControl.ps1 custom curve -10     # trip 10 C earlier" -ForegroundColor Cyan
        Write-Host ""
        return
    }

    $shift = 0
    if (-not [int]::TryParse($Arg, [ref]$shift)) {
        Write-Host "  [x] 'custom curve' expects 'dump' or a negative degree shift (e.g. -10)." -ForegroundColor Red
        Write-Host ""
        return
    }

    if ($shift -ge 0) {
        Write-Host "  [x] Refusing a shift of $shift. Only NEGATIVE shifts are allowed —" -ForegroundColor Red
        Write-Host "      a positive shift makes the fans spin LATER, i.e. runs the laptop" -ForegroundColor Red
        Write-Host "      hotter than stock. That is not something this script will write." -ForegroundColor Red
        Write-Host ""
        return
    }
    if ($shift -lt -25) {
        Write-Host "  [x] Shift $shift is beyond the -25 C safety limit." -ForegroundColor Red
        Write-Host ""
        return
    }

    $written = 0
    foreach ($t in $tables) {
        $split = Split-FanTable -Bytes $t.Table
        if (-not $split) {
            Write-Host ("   Fan={0} Sensor={1}: SKIPPED (unrecognised layout)." -f $t.FanId, $t.SensorId) -ForegroundColor Yellow
            continue
        }

        # Shift the temperature column down; speeds are untouched, so every
        # point is >= stock RPM at the same temperature by construction.
        # Clamp at 0, never upward — raising a trip point would make that fan
        # step engage LATER than stock, which is the opposite of the intent.
        $newTemps = @($split.Temps | ForEach-Object {
            $v = $_ + $shift
            if ($v -lt 0) { 0 } else { $v }
        })

        $newTable = @()
        $newTable += ($newTemps      | ForEach-Object { [byte]$_ })
        $newTable += ($split.Speeds  | ForEach-Object { [byte]$_ })

        Write-Host ("   Fan={0} Sensor={1}  writing shifted curve ({2} C earlier)" -f $t.FanId, $t.SensorId, [math]::Abs($shift)) -ForegroundColor Yellow
        Write-Host ("     new temps {0}" -f ($newTemps -join ' ')) -ForegroundColor DarkGray

        try {
            $null = Get-FanMethodInstance |
                Invoke-CimMethod -MethodName Fan_Set_Table -Arguments @{ FanTable = [byte[]]$newTable } -ErrorAction Stop
            Write-Host "     write returned without error." -ForegroundColor DarkGray
        }
        catch {
            Write-Host "     Fan_Set_Table threw: $($_.Exception.Message.Trim())" -ForegroundColor Red
            continue
        }

        Start-Sleep -Seconds 1
        $after = Get-FanTable -FanId $t.FanId -SensorId $t.SensorId
        $matched = $false
        if (-not $after.Error -and $after.Table) {
            $expected = ($newTable | ForEach-Object { [int]$_ }) -join ','
            $actual   = ($after.Table | ForEach-Object { [int]$_ }) -join ','
            $matched  = ($expected -eq $actual)
        }
        if ($matched) {
            Write-Host "     [OK] Firmware read back the new table." -ForegroundColor Green
            $written++
        } else {
            Write-Host "     [NO-OP] Readback does not match — the EC rejected the table." -ForegroundColor Yellow
            Write-Host ("             readback {0}" -f (Format-ProbeValue $after.Table)) -ForegroundColor DarkGray
        }
    }

    Write-Host ""
    if ($written -gt 0) {
        Write-Host "  [OK] $written table(s) accepted. Watch 'monitor' under load to confirm RPM." -ForegroundColor Green
        Write-Host "       Restore the firmware curve with: .\Legion-FanControl.ps1 custom off" -ForegroundColor Cyan
    } else {
        Write-Host "  [FAIL] No table was accepted by the EC." -ForegroundColor Red
    }
    Write-Host ""
}

function Invoke-Custom {
    param([string]$Action, [string]$Arg)

    if (-not (Assert-Admin -Action 'custom')) { return }

    switch -Regex ($Action) {

        '^(status|state|show)$' {
            Show-CustomStatus
            break
        }

        '^(on|enable|unlock)$' {
            Write-Host ""
            Write-Host "  [WRITE] Unlocking Custom Mode" -ForegroundColor Yellow
            Write-Host "  In Custom Mode the HOST owns the fan curve, not the EC. Remember to" -ForegroundColor DarkGray
            Write-Host "  run 'custom off' when you're done." -ForegroundColor DarkGray

            $onAc = Get-OnAcPower
            if ($onAc -eq $false) {
                Write-Host "  [!] On battery. Custom/Performance profiles are AC-gated on this" -ForegroundColor Yellow
                Write-Host "      firmware and will likely refuse. Plug in the charger." -ForegroundColor Yellow
            }

            $ok = Invoke-WithVantageSuspended -Suspend $SuspendVantage.IsPresent -Body {
                Enter-CustomMode
            }

            if ($ok) {
                Write-Host ""
                Show-CustomStatus
                Write-Host "  Next:" -ForegroundColor Cyan
                Write-Host "    .\Legion-FanControl.ps1 custom fullspeed on   # pin fans at max" -ForegroundColor Cyan
                Write-Host "    .\Legion-FanControl.ps1 custom curve dump     # inspect the curve" -ForegroundColor Cyan
                Write-Host "    .\Legion-FanControl.ps1 custom off            # restore the previous mode" -ForegroundColor Cyan
                Write-Host ""
            }
            elseif (-not $SuspendVantage.IsPresent) {
                Write-Host ""
                Write-Host "  Next: rule out Lenovo Vantage reverting the unlock —" -ForegroundColor Cyan
                Write-Host "        .\Legion-FanControl.ps1 custom on -SuspendVantage" -ForegroundColor Cyan
                Write-Host ""
            }
            break
        }

        '^(off|disable|lock|exit)$' {
            Invoke-WithVantageSuspended -Suspend $SuspendVantage.IsPresent -Body {
                Exit-CustomMode
            }
            break
        }

        '^(max|boost)$' {
            # One-shot: unlock Custom Mode and pin the fans. This is the whole
            # working path on HACN46WW, and doing it in two commands is easy to
            # get wrong (fullspeed alone is dropped outside Custom Mode).
            Write-Host ""
            Write-Host "  [WRITE] Custom Mode + full speed (one shot)" -ForegroundColor Yellow

            $onAc = Get-OnAcPower
            if ($onAc -eq $false) {
                Write-Host "  [!] On battery — Custom Mode is AC-gated and will likely refuse." -ForegroundColor Yellow
            }

            $ok = Invoke-WithVantageSuspended -Suspend $SuspendVantage.IsPresent -Body {
                if (-not (Enter-CustomMode)) { return $false }
                Write-Host ""
                Set-FanFullSpeed -On $true
            }

            Write-Host ""
            if ($ok) {
                Write-Host "  Fans pinned at max. Release with:" -ForegroundColor Cyan
                Write-Host "    .\Legion-FanControl.ps1 custom off" -ForegroundColor Cyan
            } else {
                Write-Host "  Failed. Inspect state with: .\Legion-FanControl.ps1 custom status" -ForegroundColor Cyan
                if (-not $SuspendVantage.IsPresent) {
                    Write-Host "  Or rule out Vantage: .\Legion-FanControl.ps1 custom max -SuspendVantage" -ForegroundColor Cyan
                }
            }
            Write-Host ""
            break
        }

        '^(fullspeed|full)$' {
            $on = $null
            switch -Regex ($Arg) {
                '^(on|true|1|full)$' { $on = $true }
                '^(off|false|0)$'    { $on = $false }
                default {
                    Write-Host "  [x] 'custom fullspeed' expects 'on' or 'off'. Got: '$Arg'" -ForegroundColor Red
                    return
                }
            }

            if (-not (Test-CustomModeEngaged)) {
                Write-Host ""
                Write-Host "  [!] Not in Custom Mode — the EC will drop this write." -ForegroundColor Yellow
                Write-Host "      Run '.\Legion-FanControl.ps1 custom on' first." -ForegroundColor Yellow
                Write-Host "      Attempting anyway so the result is recorded..." -ForegroundColor DarkGray
            }

            Write-Host ""
            Write-Host ("  [WRITE] Custom Mode full speed -> {0}" -f $(if ($on) { 'ON' } else { 'OFF' })) -ForegroundColor Yellow
            Write-Host ""

            $ok = Invoke-WithVantageSuspended -Suspend $SuspendVantage.IsPresent -Body {
                Set-FanFullSpeed -On $on
            }

            Write-Host ""
            if ($ok -and $on) {
                Write-Host "  Fans are now under host control. Release them with:" -ForegroundColor Cyan
                Write-Host "    .\Legion-FanControl.ps1 custom fullspeed off" -ForegroundColor Cyan
                Write-Host "    .\Legion-FanControl.ps1 custom off            # and leave Custom Mode" -ForegroundColor Cyan
            }
            elseif (-not $ok) {
                Write-Host "  The EC still refused. Try the curve route instead:" -ForegroundColor Cyan
                Write-Host "    .\Legion-FanControl.ps1 custom curve dump" -ForegroundColor Cyan
                if (-not $SuspendVantage.IsPresent) {
                    Write-Host "  ...or rule out Vantage:" -ForegroundColor Cyan
                    Write-Host "    .\Legion-FanControl.ps1 custom fullspeed on -SuspendVantage" -ForegroundColor Cyan
                }
            }
            Write-Host ""
            break
        }

        '^(curve|table)$' {
            Invoke-WithVantageSuspended -Suspend $SuspendVantage.IsPresent -Body {
                Invoke-CustomCurve -Arg $Arg
            }
            break
        }

        default {
            Write-Host ""
            Write-Host "  [x] 'custom' expects: max | status | on | off | fullspeed <on|off> | curve [dump|-N]" -ForegroundColor Red
            Write-Host "      Got: '$Action'" -ForegroundColor Red
            Write-Host ""
        }
    }
}

# =============================================================================
# Subcommand: test  (experiments for a middle fan speed on DC / weak chargers)
#
#   power / methods are READ-ONLY. modes / custom / pulse WRITE, and always
#   restore the starting mode in a finally block (Ctrl+C included). They
#   refuse to run while the tray app is running: two writers on the same
#   firmware fight each other. Each run saves test-<name>-<stamp>.csv here.
# =============================================================================

function Get-TestSample {
    param([string]$Phase)
    [pscustomobject]@{
        Time      = (Get-Date -Format 'HH:mm:ss')
        Phase     = $Phase
        Smart     = Get-SmartFanMode
        Thermal   = Get-ThermalMode
        FullSpeed = Get-FullSpeedStatus
        Fan0      = Get-FanRpm -FanId 0
        Fan1      = Get-FanRpm -FanId 1
        CpuC      = Get-SensorTemp -SensorId 3
        GpuC      = Get-SensorTemp -SensorId 4
        OnAc      = Get-OnAcPower
    }
}

function Write-TestSample {
    param($S)
    Write-Host ("   {0}  {1,-14} smart {2,-3} thermal {3,-3} full {4,-5}  fan0 {5,5}  fan1 {6,5}  cpu {7,3}C  gpu {8,3}C" -f `
        $S.Time, $S.Phase, (Format-Reading $S.Smart ''), (Format-Reading $S.Thermal ''), (Format-Reading $S.FullSpeed ''), `
        (Format-Reading $S.Fan0 ''), (Format-Reading $S.Fan1 ''), (Format-Reading $S.CpuC ''), (Format-Reading $S.GpuC ''))
}

# Sample every $StepSeconds for $Seconds, printing as it goes. Throws if the
# CPU crosses -MaxTempC so the caller's finally block can take over.
function Watch-TestPhase {
    param([string]$Phase, [int]$Seconds, [int]$StepSeconds = 2)
    $out = @()
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) {
        $s = Get-TestSample -Phase $Phase
        Write-TestSample $s
        $out += $s
        if ($null -ne $s.CpuC -and $s.CpuC -ge $MaxTempC) {
            throw "CPU reached $($s.CpuC) C (limit $MaxTempC C). Test aborted."
        }
        Start-Sleep -Seconds $StepSeconds
    }
    return , $out
}

function Write-TestSummary {
    param($Samples)
    Write-Host ""
    Write-Host "   SUMMARY (rpm)" -ForegroundColor White
    Write-Host ("   {0,-16} {1,6} {2,6} {3,6}   {4,6} {5,6} {6,6}   {7}" -f 'phase', 'f0 min', 'f0 avg', 'f0 max', 'f1 min', 'f1 avg', 'f1 max', 'modes seen (smart/thermal)') -ForegroundColor DarkGray
    foreach ($g in ($Samples | Group-Object Phase)) {
        $f0 = $g.Group | Where-Object { $null -ne $_.Fan0 } | Measure-Object Fan0 -Minimum -Maximum -Average
        $f1 = $g.Group | Where-Object { $null -ne $_.Fan1 } | Measure-Object Fan1 -Minimum -Maximum -Average
        $modes = ($g.Group | ForEach-Object { "$($_.Smart)/$($_.Thermal)" } | Select-Object -Unique) -join ', '
        Write-Host ("   {0,-16} {1,6} {2,6:N0} {3,6}   {4,6} {5,6:N0} {6,6}   {7}" -f `
            $g.Name, $f0.Minimum, $f0.Average, $f0.Maximum, $f1.Minimum, $f1.Average, $f1.Maximum, $modes)
    }
}

function Save-TestLog {
    param([string]$Name, $Samples)
    if (-not $Samples -or $Samples.Count -eq 0) { return }
    $file = Join-Path $PSScriptRoot ("test-{0}-{1}.csv" -f $Name, (Get-Date -Format 'yyyyMMdd-HHmmss'))
    try {
        $Samples | Export-Csv -Path $file -NoTypeInformation -Encoding UTF8
        Write-Host ""
        Write-Host "  [SAVED] $file" -ForegroundColor Green
    } catch {
        Write-Host "  [!] Could not write $file : $($_.Exception.Message.Trim())" -ForegroundColor Yellow
    }
}

function Assert-NoTrayApp {
    if (Get-Process -Name 'LegionFanTray' -ErrorAction SilentlyContinue) {
        Write-Host ""
        Write-Host "  [!] LegionFanTray.exe is running. Exit it from the tray first -" -ForegroundColor Yellow
        Write-Host "      two writers on the same firmware will fight each other." -ForegroundColor Yellow
        Write-Host ""
        return $false
    }
    return $true
}

function Set-TestMode {
    param([int]$Mode)
    $null = Get-GamezoneInstance | Invoke-CimMethod -MethodName SetSmartFanMode -Arguments @{ Data = [uint32]$Mode }
}

function Set-TestFullSpeed {
    param([bool]$On)
    $null = Get-FanMethodInstance | Invoke-CimMethod -MethodName Fan_Set_FullSpeed -Arguments @{ Status = $On }
}

# Put the machine back where the test found it. Custom / unknown -> Balanced.
function Restore-TestState {
    param($ModeBefore)
    $target = 2
    if ($null -ne $ModeBefore -and [int]$ModeBefore -in 1, 2, 3) { $target = [int]$ModeBefore }
    try { Set-TestFullSpeed -On $false } catch { }
    try { Set-TestMode -Mode $target } catch { }
    Start-Sleep -Seconds 2
    Write-Host ""
    Write-Host ("  [RESTORED] SmartFanMode {0}, ThermalMode {1}, FullSpeed {2}" -f `
        (Format-Mode (Get-SmartFanMode)), (Format-Mode (Get-ThermalMode)), (Format-Reading (Get-FullSpeedStatus) '')) -ForegroundColor Cyan
}

function Invoke-TestPower {
    Write-Host ""
    Write-Host "  Power source (read-only)" -ForegroundColor Cyan
    Write-Host "  ------------------------" -ForegroundColor DarkGray
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $ps = [System.Windows.Forms.SystemInformation]::PowerStatus
        Write-Host ("   ACLineStatus (Windows)  : {0}" -f $ps.PowerLineStatus)
        Write-Host ("   Charge status / percent : {0} / {1:P0}" -f $ps.BatteryChargeStatus, $ps.BatteryLifePercent)
    } catch { Write-Host "   ACLineStatus            : n/a ($($_.Exception.Message.Trim()))" }

    try {
        foreach ($b in @(Get-CimInstance Win32_Battery -ErrorAction Stop)) {
            $meaning = switch ([int]$b.BatteryStatus) {
                1 { 'discharging' } 2 { 'on AC' } 3 { 'fully charged' }
                6 { 'charging' } 7 { 'charging (high)' } 8 { 'charging (low)' } 9 { 'charging (critical)' }
                default { 'other' }
            }
            Write-Host ("   Win32_Battery status    : {0} ({1}), {2}%" -f $b.BatteryStatus, $meaning, $b.EstimatedChargeRemaining)
        }
    } catch { Write-Host "   Win32_Battery           : n/a" }

    try {
        foreach ($b in @(Get-CimInstance -Namespace $WmiNamespace -ClassName BatteryStatus -ErrorAction Stop)) {
            Write-Host ("   PowerOnline / Charging / Discharging : {0} / {1} / {2}" -f $b.PowerOnline, $b.Charging, $b.Discharging)
            Write-Host ("   ChargeRate / DischargeRate           : {0} mW / {1} mW" -f $b.ChargeRate, $b.DischargeRate)
        }
    } catch { Write-Host "   root\WMI BatteryStatus  : n/a (needs admin)" }

    Write-Host ""
    Write-Host ("   => Get-OnAcPower verdict : {0}" -f (Get-OnAcPower)) -ForegroundColor White
    Write-Host "      A weak charger shows PowerOnline True with a DischargeRate above 0." -ForegroundColor DarkGray
    Write-Host ""
}

function Invoke-TestMethods {
    Write-Host ""
    Write-Host "  Every LENOVO_* WMI method and its parameters (read-only)" -ForegroundColor Cyan
    Write-Host "  Highlighted: names that could set a speed, level or curve." -ForegroundColor DarkGray
    $pattern = 'Speed|Level|Cooling|Feature|Curve|Table|Fan|Duty|PWM|RPM'
    $known = 'Fan_Set_FullSpeed|Fan_Set_Table|SetSmartFanMode|Set_Custom_Mode_Status'
    try {
        $classes = @(Get-CimClass -Namespace $WmiNamespace -ErrorAction Stop |
            Where-Object { $_.CimClassName -like 'LENOVO_*' } | Sort-Object CimClassName)
    } catch {
        Write-Host "  [x] Cannot enumerate $WmiNamespace : $($_.Exception.Message.Trim())" -ForegroundColor Red
        return
    }
    foreach ($c in $classes) {
        if (@($c.CimClassMethods).Count -eq 0) { continue }
        Write-Host ""
        Write-Host ("  [{0}]" -f $c.CimClassName) -ForegroundColor White
        foreach ($m in $c.CimClassMethods) {
            $in  = @($m.Parameters | Where-Object { $_.Qualifiers.Name -contains 'In' }  | ForEach-Object { "$($_.Name):$($_.CimType)" }) -join ', '
            $out = @($m.Parameters | Where-Object { $_.Qualifiers.Name -contains 'Out' } | ForEach-Object { "$($_.Name):$($_.CimType)" }) -join ', '
            $color = if ($m.Name -match $known) { 'DarkGray' }
                     elseif ($m.Name -match '^(Set|Fan_Set)' -and $m.Name -match $pattern) { 'Green' }
                     elseif ($m.Name -match $pattern) { 'Yellow' }
                     else { 'Gray' }
            Write-Host ("    {0,-40} in({1})  out({2})" -f $m.Name, $in, $out) -ForegroundColor $color
        }
    }
    Write-Host ""
    Write-Host "  Green = an untested setter worth a closer look. Grey = already known." -ForegroundColor DarkGray
    Write-Host ""
}

function Invoke-TestModes {
    $modeBefore = Get-SmartFanMode
    $log = @()
    Write-Host ""
    Write-Host "  Stock curves on this power state: Quiet -> Balanced -> Performance, ${HoldSeconds}s each" -ForegroundColor Cyan
    Write-Host ("  Starting mode: {0}   On AC: {1}" -f (Format-Mode $modeBefore), (Get-OnAcPower)) -ForegroundColor DarkGray
    try {
        if ([int]$modeBefore -eq $CustomModeValue) { Set-TestFullSpeed -On $false }
        foreach ($m in 1, 2, 3) {
            Write-Host ""
            Write-Host ("  -> {0}" -f $ModeNames[$m]) -ForegroundColor Yellow
            Set-TestMode -Mode $m
            $log += Watch-TestPhase -Phase $ModeNames[$m] -Seconds $HoldSeconds
            $last = $log[-1]
            if ([int]$last.Thermal -ne $m) {
                Write-Host ("     [!] ThermalMode is {0}, not {1}: the firmware did not apply it here." -f $last.Thermal, $m) -ForegroundColor Yellow
            }
        }
    } catch {
        Write-Host "  [x] $($_.Exception.Message)" -ForegroundColor Red
    } finally {
        Restore-TestState -ModeBefore $modeBefore
        Write-TestSummary $log
        Save-TestLog -Name 'modes' -Samples $log
    }
    Write-Host ""
    Write-Host "  If Performance shows thermal 3 with rpm between Balanced and 4300," -ForegroundColor DarkGray
    Write-Host "  it is a usable 'middle speed' on this power state." -ForegroundColor DarkGray
    Write-Host ""
}

function Invoke-TestCustom {
    $modeBefore = Get-SmartFanMode
    $log = @()
    Write-Host ""
    Write-Host "  Custom Mode on this power state: full speed OFF for ${HoldSeconds}s, then ON" -ForegroundColor Cyan
    Write-Host ("  Starting mode: {0}   On AC: {1}" -f (Format-Mode $modeBefore), (Get-OnAcPower)) -ForegroundColor DarkGray
    try {
        $log += Watch-TestPhase -Phase 'baseline' -Seconds 6

        Set-TestMode -Mode $CustomModeValue
        Start-Sleep -Seconds 2
        if ([int](Get-SmartFanMode) -ne $CustomModeValue) {
            Write-Host "  [FAIL] Custom Mode did not engage here (SmartFanMode is not 255)." -ForegroundColor Red
            return
        }
        Write-Host "  [OK] Custom Mode engaged." -ForegroundColor Green

        Set-TestFullSpeed -On $false
        Write-Host ""
        Write-Host "  -> Custom, full speed OFF (what does the EC do with no override?)" -ForegroundColor Yellow
        $log += Watch-TestPhase -Phase 'custom-off' -Seconds $HoldSeconds

        Set-TestFullSpeed -On $true
        Write-Host ""
        Write-Host "  -> Custom, full speed ON" -ForegroundColor Yellow
        $log += Watch-TestPhase -Phase 'custom-full' -Seconds 12

        $tables = Get-ReadableFanTables
        Write-Host ""
        Write-Host ("  Readable fan tables inside Custom here: {0}" -f $tables.Count) -ForegroundColor White
        foreach ($t in $tables) {
            Write-Host ("    Fan={0} Sensor={1} Size={2} Table={3}" -f $t.FanId, $t.SensorId, $t.Size, (Format-ProbeValue $t.Table)) -ForegroundColor Green
        }
    } catch {
        Write-Host "  [x] $($_.Exception.Message)" -ForegroundColor Red
    } finally {
        Restore-TestState -ModeBefore $modeBefore
        Write-TestSummary $log
        Save-TestLog -Name 'custom' -Samples $log
    }
    Write-Host ""
}

function Invoke-TestPulse {
    $modeBefore = Get-SmartFanMode
    $log = @()
    Write-Host ""
    Write-Host ("  Duty-cycle test: full speed ON {0}s / OFF {1}s for {2}s, inside Custom Mode" -f $OnSeconds, $OffSeconds, $DurationSeconds) -ForegroundColor Cyan
    Write-Host "  Listen: a steady middle tone is a win, an audible surge is not." -ForegroundColor DarkGray
    try {
        $log += Watch-TestPhase -Phase 'baseline' -Seconds 6 -StepSeconds 1

        Set-TestMode -Mode $CustomModeValue
        Start-Sleep -Seconds 2
        if ([int](Get-SmartFanMode) -ne $CustomModeValue) {
            Write-Host "  [FAIL] Custom Mode did not engage here (SmartFanMode is not 255)." -ForegroundColor Red
            return
        }

        $end = (Get-Date).AddSeconds($DurationSeconds)
        while ((Get-Date) -lt $end) {
            Set-TestFullSpeed -On $true
            $log += Watch-TestPhase -Phase 'pulse' -Seconds $OnSeconds -StepSeconds 1
            Set-TestFullSpeed -On $false
            $log += Watch-TestPhase -Phase 'pulse' -Seconds $OffSeconds -StepSeconds 1
        }
    } catch {
        Write-Host "  [x] $($_.Exception.Message)" -ForegroundColor Red
        try { Set-TestFullSpeed -On $true } catch { }
    } finally {
        Restore-TestState -ModeBefore $modeBefore
        Write-TestSummary $log
        $p = @($log | Where-Object { $_.Phase -eq 'pulse' -and $null -ne $_.Fan0 } | Select-Object -Skip 5)
        if ($p.Count -gt 0) {
            $m = $p | Measure-Object Fan0 -Minimum -Maximum
            Write-Host ("   Fan0 swing once settled: {0} rpm ({1}-{2})" -f ($m.Maximum - $m.Minimum), $m.Minimum, $m.Maximum) -ForegroundColor White
            Write-Host "   Under ~400 rpm swing sounds steady; above ~800 is a clear surge." -ForegroundColor DarkGray
        }
        Save-TestLog -Name ("pulse-{0}on{1}off" -f $OnSeconds, $OffSeconds) -Samples $log
    }
    Write-Host ""
}

# LenovoLegionToolkit's GodMode V1 layout for Fan_Set_Table (64 bytes):
#   [0] FSTM mode (LLT writes 1)  [1] FSID fan id  [2..5] FSTL uint32 = 0
#   [6..25] FSS0..FSS9, ten uint16 LE fan LEVELS 0-10 (not rpm)  [26..63] 0
# The firmware owns the temperature points. LLT writes this blind, without
# reading Fan_Get_Table first -- which is why our 'custom curve' never tried.
function New-FanTableBytes {
    param([int[]]$Levels, [int]$Mode = 1)
    $b = New-Object byte[] 64
    $b[0] = [byte]$Mode
    for ($i = 0; $i -lt 10; $i++) {
        $b[6 + 2 * $i] = [byte]$Levels[$i]
        $b[7 + 2 * $i] = 0
    }
    return , $b
}

function Set-TestFanTable {
    param([int[]]$Levels, [int]$Mode)
    $bytes = New-FanTableBytes -Levels $Levels -Mode $Mode
    $null = Get-FanMethodInstance | Invoke-CimMethod -MethodName Fan_Set_Table -Arguments @{ FanTable = [byte[]]$bytes } -ErrorAction Stop
}

function Show-FanTableData {
    try {
        foreach ($inst in @(Get-CimInstance -Namespace $WmiNamespace -ClassName 'LENOVO_FAN_TABLE_DATA' -ErrorAction Stop)) {
            Write-Host ("   LENOVO_FAN_TABLE_DATA: Fan_Id {0}  FanTable_Len {1}  FanTable_Data {2}  CurrentFanMaxSpeed {3}" -f `
                $inst.Fan_Id, $inst.FanTable_Len, (Format-ProbeValue $inst.FanTable_Data), $inst.CurrentFanMaxSpeed) -ForegroundColor DarkGray
        }
    } catch { Write-Host "   LENOVO_FAN_TABLE_DATA: $($_.Exception.Message.Trim())" -ForegroundColor DarkGray }
}

function Invoke-TestTable {
    $modeBefore = Get-SmartFanMode
    $level = [math]::Min(10, [math]::Max(3, $Level))
    $mode = if ($TableMode -eq 255) { 255 } else { 1 }
    $flat = @($level) * 10
    $default = 1..10
    $log = @()
    Write-Host ""
    Write-Host ("  Blind Fan_Set_Table: flat level {0} (all 10 points), byte0 = {1}, inside Custom Mode" -f $level, $mode) -ForegroundColor Cyan
    Write-Host "  Success looks like rpm settling somewhere other than 2300 / 4300." -ForegroundColor DarkGray
    try {
        $log += Watch-TestPhase -Phase 'baseline' -Seconds 6

        Set-TestMode -Mode $CustomModeValue
        Start-Sleep -Seconds 2
        if ([int](Get-SmartFanMode) -ne $CustomModeValue) {
            Write-Host "  [FAIL] Custom Mode did not engage here (SmartFanMode is not 255)." -ForegroundColor Red
            return
        }
        Set-TestFullSpeed -On $false

        Write-Host ""
        Write-Host ("  -> Fan_Set_Table(level {0} x 10)" -f $level) -ForegroundColor Yellow
        try {
            Set-TestFanTable -Levels $flat -Mode $mode
            Write-Host "     call returned without error." -ForegroundColor DarkGray
        } catch {
            Write-Host "     threw: $($_.Exception.Message.Trim())" -ForegroundColor Red
            return
        }
        Show-FanTableData
        $tables = Get-ReadableFanTables
        Write-Host ("   Readable fan tables now: {0}" -f $tables.Count) -ForegroundColor DarkGray
        $log += Watch-TestPhase -Phase ("level-{0}" -f $level) -Seconds $HoldSeconds

        Write-Host ""
        Write-Host "  -> Fan_Set_Table(default 1..10)" -ForegroundColor Yellow
        Set-TestFanTable -Levels $default -Mode $mode
        $log += Watch-TestPhase -Phase 'default-table' -Seconds 20
    } catch {
        Write-Host "  [x] $($_.Exception.Message)" -ForegroundColor Red
        try { Set-TestFullSpeed -On $true } catch { }
    } finally {
        try { Set-TestFanTable -Levels $default -Mode $mode } catch { }
        Restore-TestState -ModeBefore $modeBefore
        Write-TestSummary $log
        Save-TestLog -Name ("table-L{0}-m{1}" -f $level, $mode) -Samples $log
    }
    Write-Host ""
}

# --- test powerlimit ---------------------------------------------------------
# LENOVO_CPU_METHOD exposes the vendor's own sustained (long-term, PL1-like)
# and boost (short-term, PL2-like) CPU power limits. Less heat means the stock
# curve keeps up on its own and auto-max rarely needs full speed.
#
# Guard rails: the limit can only be LOWERED (never above the value read at
# the start), is snapped to the firmware's step and minimum, and the original
# values are saved to disk first so 'test powerlimit restore' can put them
# back even if a run is killed.

$CpuMethodClass = 'LENOVO_CPU_METHOD'
$PowerLimitFile = Join-Path $env:LOCALAPPDATA 'LegionFanTray\script-powerlimit-original.txt'

function Get-CpuPowerLimits {
    $inst = Get-CimInstance -Namespace $WmiNamespace -ClassName $CpuMethodClass -ErrorAction Stop
    $l = $inst | Invoke-CimMethod -MethodName CPU_Get_LongTerm_PowerLimit -ErrorAction Stop
    $s = $inst | Invoke-CimMethod -MethodName CPU_Get_ShortTerm_PowerLimit -ErrorAction Stop
    [pscustomobject]@{
        Long = [int]$l.CurrentLongTerm_PowerLimit;  LongMin = [int]$l.MinLongTerm_PowerLimit
        LongMax = [int]$l.MaxLongTerm_PowerLimit;   LongStep = [int]$l.step
        Short = [int]$s.CurrentShortTerm_PowerLimit; ShortMin = [int]$s.MinShortTerm_PowerLimit
        ShortMax = [int]$s.MaxShortTerm_PowerLimit;  ShortStep = [int]$s.step
    }
}

function Set-CpuPowerLimits {
    param([int]$Long, [int]$Short)
    $inst = Get-CimInstance -Namespace $WmiNamespace -ClassName $CpuMethodClass -ErrorAction Stop
    # Short-term first when lowering, so it never sits below the long-term limit.
    $null = $inst | Invoke-CimMethod -MethodName CPU_Set_ShortTerm_PowerLimit -Arguments @{ value = [uint32]$Short } -ErrorAction Stop
    $null = $inst | Invoke-CimMethod -MethodName CPU_Set_LongTerm_PowerLimit  -Arguments @{ value = [uint32]$Long }  -ErrorAction Stop
}

function Write-CpuPowerLimits {
    param($P, [string]$Label)
    Write-Host ("   {0,-9} long-term {1} (min {2}, max {3}, step {4})   short-term {5} (min {6}, max {7}, step {8})" -f `
        $Label, $P.Long, $P.LongMin, $P.LongMax, $P.LongStep, $P.Short, $P.ShortMin, $P.ShortMax, $P.ShortStep)
}

function Show-GpuPowerLimits {
    try {
        $inst = Get-CimInstance -Namespace $WmiNamespace -ClassName 'LENOVO_GPU_METHOD' -ErrorAction Stop
        $c = $inst | Invoke-CimMethod -MethodName GPU_Get_cTGP_PowerLimit -ErrorAction Stop
        $p = $inst | Invoke-CimMethod -MethodName GPU_Get_PPAB_PowerLimit -ErrorAction Stop
        Write-Host ("   GPU       cTGP {0} (min {1}, max {2})   PPAB {3} (min {4}, max {5})   [read-only here]" -f `
            $c.Current_cTGP_PowerLimit, $c.Min_cTGP_PowerLimit, $c.Max_cTGP_PowerLimit,
            $p.CurrentPPAB_PowerLimit, $p.MinPPAB_PowerLimit, $p.MaxPPAB_PowerLimit) -ForegroundColor DarkGray
    } catch { Write-Host "   GPU limits: $($_.Exception.Message.Trim())" -ForegroundColor DarkGray }
}

function Get-SnappedLimit {
    param([int]$Want, [int]$Min, [int]$Ceiling, [int]$Step)
    $v = [math]::Min($Want, $Ceiling)
    if ($Step -gt 1) { $v = $Min + [math]::Floor(($v - $Min) / $Step) * $Step }
    return [int][math]::Max($Min, $v)
}

# One busy loop per logical CPU, each self-terminating at the deadline so a
# killed test cannot leave load running.
function Start-CpuLoad {
    param([int]$Seconds)
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    1..[Environment]::ProcessorCount | ForEach-Object {
        Start-ThreadJob -ScriptBlock {
            $x = 1.0
            while ([DateTime]::UtcNow -lt $using:until) { $x = [math]::Sqrt($x + 12345.678) }
        }
    }
}

function Stop-CpuLoad {
    param($Jobs)
    if ($Jobs) { $Jobs | Stop-Job -ErrorAction SilentlyContinue; $Jobs | Remove-Job -Force -ErrorAction SilentlyContinue }
}

function Restore-CpuPowerLimits {
    if (-not (Test-Path $PowerLimitFile)) {
        Write-Host "  [OK] No saved power limits: nothing to restore." -ForegroundColor Green
        return
    }
    $parts = (Get-Content $PowerLimitFile -TotalCount 1) -split ','
    $long = [int]$parts[0]; $short = [int]$parts[1]
    try {
        Set-CpuPowerLimits -Long $long -Short $short
        $now = Get-CpuPowerLimits
        if ($now.Long -eq $long -and $now.Short -eq $short) {
            Write-Host ("  [RESTORED] CPU power limits long-term {0}, short-term {1}" -f $long, $short) -ForegroundColor Cyan
            Remove-Item $PowerLimitFile -ErrorAction SilentlyContinue
        } else {
            Write-Host ("  [!] Wrote long {0} / short {1}, reads back {2} / {3}. Saved values kept in {4}" -f `
                $long, $short, $now.Long, $now.Short, $PowerLimitFile) -ForegroundColor Yellow
        }
    } catch {
        Write-Host "  [x] Restore failed: $($_.Exception.Message.Trim()). Saved values kept in $PowerLimitFile" -ForegroundColor Red
    }
}

function Invoke-TestPowerLimit {
    param([string]$Sub)

    if ($Sub -match '^restore$') { Restore-CpuPowerLimits; return }

    Write-Host ""
    Write-Host "  CPU power limits (LENOVO_CPU_METHOD)" -ForegroundColor Cyan
    try { $orig = Get-CpuPowerLimits }
    catch {
        Write-Host "  [x] Cannot read the CPU power limits: $($_.Exception.Message.Trim())" -ForegroundColor Red
        return
    }
    Write-CpuPowerLimits $orig 'now'
    Show-GpuPowerLimits
    Write-Host ("   Mode      {0}   On AC: {1}" -f (Format-Mode (Get-SmartFanMode)), (Get-OnAcPower)) -ForegroundColor DarkGray

    if (Test-Path $PowerLimitFile) {
        Write-Host ""
        Write-Host "  [!] A previous run left saved limits in $PowerLimitFile." -ForegroundColor Yellow
        Write-Host "      Put them back first:  .\Legion-FanControl.ps1 test powerlimit restore" -ForegroundColor Yellow
        return
    }

    if ($Watts -le 0) {
        Write-Host ""
        Write-Host "  Read-only. To test a lower sustained limit under load, e.g.:" -ForegroundColor DarkGray
        Write-Host "    .\Legion-FanControl.ps1 test powerlimit -Watts 35 -HoldSeconds 90" -ForegroundColor Cyan
        Write-Host ""
        return
    }
    if (-not (Assert-NoTrayApp)) { return }

    # Only ever lower, never below the firmware minimum, snapped to its step.
    $long  = Get-SnappedLimit -Want $Watts -Min $orig.LongMin -Ceiling $orig.Long -Step $orig.LongStep
    $wantShort = if ($ShortWatts -gt 0) { $ShortWatts } else { $long }
    $short = Get-SnappedLimit -Want ([math]::Max($wantShort, $long)) -Min $orig.ShortMin -Ceiling $orig.Short -Step $orig.ShortStep
    if ($short -lt $long) { $short = $long }
    if ($Watts -ne $long) {
        Write-Host ("  [!] -Watts {0} adjusted to {1}: this test only lowers, and the current long-term limit is {2} (min {3})." -f `
            $Watts, $long, $orig.Long, $orig.LongMin) -ForegroundColor Yellow
    }
    if ($long -ge $orig.Long -and $short -ge $orig.Short) {
        Write-Host "  [!] -Watts $Watts is not below the current limits; nothing to test (this test only lowers)." -ForegroundColor Yellow
        return
    }

    $modeBefore = Get-SmartFanMode
    $log = @()
    $jobs = $null
    Write-Host ""
    Write-Host ("  Plan: {0}s under {1} at the stock limits, then {0}s at long-term {2} / short-term {3}." -f `
        $HoldSeconds, $(if ($NoLoad) { 'your own load' } else { "a $([Environment]::ProcessorCount)-thread CPU load" }), $long, $short) -ForegroundColor White

    try {
        Set-Content -Path $PowerLimitFile -Value ("{0},{1}" -f $orig.Long, $orig.Short) -ErrorAction Stop
    } catch {
        Write-Host "  [x] Cannot save the original limits ($($_.Exception.Message.Trim())). Not writing anything." -ForegroundColor Red
        return
    }

    try {
        if (-not $NoLoad) { $jobs = Start-CpuLoad -Seconds (2 * $HoldSeconds + 30) }

        Write-Host ""
        Write-Host "  -> stock limits" -ForegroundColor Yellow
        $log += Watch-TestPhase -Phase 'stock' -Seconds $HoldSeconds

        Write-Host ""
        Write-Host ("  -> writing long-term {0}, short-term {1} in {2}" -f $long, $short, (Format-Mode (Get-SmartFanMode))) -ForegroundColor Yellow
        Set-CpuPowerLimits -Long $long -Short $short
        Start-Sleep -Seconds 1
        $after = Get-CpuPowerLimits
        Write-CpuPowerLimits $after 'readback'
        $took = ($after.Long -eq $long)
        if (-not $took) {
            Write-Host "     [!] The firmware did not take the long-term limit in this mode." -ForegroundColor Yellow
            Write-Host "         LenovoLegionToolkit applies these in Custom Mode, so trying there." -ForegroundColor Yellow
            Write-Host "         NOTE: in Custom the fans do not follow a curve; the -MaxTempC guard is the safety net." -ForegroundColor Yellow
            Set-TestMode -Mode $CustomModeValue
            Start-Sleep -Seconds 2
            Set-TestFullSpeed -On $false
            Set-CpuPowerLimits -Long $long -Short $short
            Start-Sleep -Seconds 1
            $after = Get-CpuPowerLimits
            Write-CpuPowerLimits $after 'custom'
            $took = ($after.Long -eq $long)
            if (-not $took) { Write-Host "     [NO-OP] Not taken in Custom either." -ForegroundColor Red }
        }
        $phase = if ($took) { "limit-$long" } else { 'limit-not-taken' }
        $log += Watch-TestPhase -Phase $phase -Seconds $HoldSeconds
    } catch {
        Write-Host "  [x] $($_.Exception.Message)" -ForegroundColor Red
        try { Set-TestFullSpeed -On $true } catch { }
    } finally {
        Stop-CpuLoad $jobs
        Write-Host ""
        Restore-CpuPowerLimits
        Restore-TestState -ModeBefore $modeBefore
        Write-TestSummary $log
        foreach ($g in ($log | Group-Object Phase)) {
            $t = $g.Group | Where-Object { $null -ne $_.CpuC } | Select-Object -Last ([math]::Max(1, [int]($g.Count / 2))) | Measure-Object CpuC -Average -Maximum
            Write-Host ("   {0,-16} CPU temp, second half: avg {1:N0} C, max {2} C" -f $g.Name, $t.Average, $t.Maximum)
        }
        Save-TestLog -Name ("powerlimit-{0}" -f $long) -Samples $log
    }
    Write-Host ""
    Write-Host "  A win: noticeably lower CPU temp (and rpm) in the limited phase for an" -ForegroundColor DarkGray
    Write-Host "  acceptable loss of speed. The second phase starts warmer, so a small" -ForegroundColor DarkGray
    Write-Host "  difference is inside the noise." -ForegroundColor DarkGray
    Write-Host ""
}

function Invoke-Test {
    param([string]$Action, [string]$Sub)
    switch -Regex ($Action) {
        '^power$'   { Invoke-TestPower; break }
        '^methods$' { if (Assert-Admin -Action 'test methods') { Invoke-TestMethods }; break }
        '^powerlimit$' {
            if (-not (Assert-Admin -Action 'test powerlimit')) { break }
            Invoke-TestPowerLimit -Sub $Sub
            break
        }
        '^(modes|custom|pulse|table)$' {
            if (-not (Assert-Admin -Action "test $Action")) { break }
            if (-not (Assert-NoTrayApp)) { break }
            switch ($Action) {
                'modes'  { Invoke-TestModes }
                'custom' { Invoke-TestCustom }
                'pulse'  { Invoke-TestPulse }
                'table'  { Invoke-TestTable }
            }
            break
        }
        default {
            Write-Host ""
            Write-Host "  [x] 'test' expects: power | methods | modes | custom | pulse | table | powerlimit" -ForegroundColor Red
            Write-Host "      Got: '$Action'" -ForegroundColor Red
            Write-Host ""
        }
    }
}

# =============================================================================
# Subcommand: help  (default)
# =============================================================================

function Show-Usage {
    Write-Host ""
    Write-Host "  Legion-FanControl.ps1 — Lenovo Legion S7-15ACH6 fan/thermal tool" -ForegroundColor Cyan
    Write-Host "  ----------------------------------------------------------------" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "  USAGE:" -ForegroundColor White
    Write-Host "    .\Legion-FanControl.ps1 <command> [value] [value2] [-SuspendVantage]"
    Write-Host ""
    Write-Host "  COMMANDS:" -ForegroundColor White
    Write-Host "    monitor            Live read-only dashboard (CPU/GPU temp, fan RPM, mode)."
    Write-Host "                       Optional: -IntervalSeconds N   (default 2)"
    Write-Host ""
    Write-Host "    probe              [ADMIN] Read-only capability dump of every Lenovo WMI"
    Write-Host "                       class + the fan curve table matrix. Saves a timestamped"
    Write-Host "                       probe-*.txt next to the script so runs are diffable."
    Write-Host ""
    Write-Host "    custom max         [ADMIN] *** THE ONE THAT WORKS *** Unlock Custom Mode and"
    Write-Host "                       pin both fans at 4300 rpm in a single step. Confirmed on"
    Write-Host "                       BIOS HACN46WW: 2800 -> 4300 rpm. Release with 'custom off'."
    Write-Host ""
    Write-Host "    custom off         [ADMIN] Leave Custom Mode, restore the previous mode. ALWAYS run"
    Write-Host "                       this when you're done — the host owns the fans until you do."
    Write-Host "    custom status      [ADMIN] Show Custom Mode state and readable fan tables."
    Write-Host "    custom on          [ADMIN] Unlock Custom Mode only, without touching the fans."
    Write-Host "    custom fullspeed <on|off>"
    Write-Host "                       [ADMIN] Toggle full speed. Only honoured inside Custom Mode,"
    Write-Host "                       so 'custom max' is usually what you want."
    Write-Host "    custom curve dump  [ADMIN] Print the firmware fan curve tables. NOTE: HACN46WW"
    Write-Host "                       exposes no curve table even in Custom Mode, so the curve"
    Write-Host "                       subcommands do nothing on this BIOS. Kept for other models."
    Write-Host ""
    Write-Host "    mode <q|b|p>       [ADMIN] Set fan/thermal mode: quiet | balanced | performance."
    Write-Host "                       Verifies ThermalMode actually followed. NOTE: Performance"
    Write-Host "                       is AC-only, and it is a CURVE — it will not pin the fans."
    Write-Host ""
    Write-Host "    force <on|off>     [ADMIN] Raw Fan_Set_FullSpeed outside Custom Mode, with"
    Write-Host "                       RPM verification. Expected to no-op — use 'custom' instead."
    Write-Host ""
    Write-Host "    cap [percent]      [ADMIN] Cap CPU max processor state via powercfg."
    Write-Host "                       Default 99 (disables Turbo). Try 80 for more headroom."
    Write-Host ""
    Write-Host "    uncap              [ADMIN] Restore CPU max processor state to 100%."
    Write-Host ""
    Write-Host "    test power         Read-only: is the charger seen as AC, and is the battery"
    Write-Host "                       still draining (weak charger)?"
    Write-Host "    test methods       [ADMIN] Read-only: every LENOVO_* method + parameters,"
    Write-Host "                       highlighting any untested speed/level setter."
    Write-Host "    test modes         [ADMIN] Quiet -> Balanced -> Performance, -HoldSeconds each,"
    Write-Host "                       logging rpm. Shows whether Performance works on this power."
    Write-Host "    test custom        [ADMIN] Custom Mode with full speed OFF, then ON, logging rpm."
    Write-Host "    test pulse         [ADMIN] Toggle full speed -OnSeconds / -OffSeconds for"
    Write-Host "                       -DurationSeconds: can a duty cycle make a middle speed?"
    Write-Host "    test table         [ADMIN] Blind Fan_Set_Table with a flat -Level (3-10, fan"
    Write-Host "                       level not rpm; LenovoLegionToolkit layout), -TableMode 1|255."
    Write-Host "                       Restores the default 1..10 table afterwards."
    Write-Host "    test powerlimit    [ADMIN] Read the CPU/GPU power limits. With -Watts N: run"
    Write-Host "                       a CPU load at stock limits, then at long-term N (short-term"
    Write-Host "                       -ShortWatts, default N), comparing temps and rpm. Only ever"
    Write-Host "                       LOWERS; originals are saved first and always restored."
    Write-Host "                       -NoLoad uses your own load.  'test powerlimit restore'"
    Write-Host "                       puts saved limits back after an interrupted run."
    Write-Host "                       Write tests restore the starting mode, abort at -MaxTempC"
    Write-Host "                       (default 90), refuse while LegionFanTray.exe runs, and save"
    Write-Host "                       test-*.csv next to the script."
    Write-Host ""
    Write-Host "    help               Show this message (default)."
    Write-Host ""
    Write-Host "  GLOBAL SWITCH:" -ForegroundColor White
    Write-Host "    -SuspendVantage    Stop LenovoVantageService / ImControllerService /"
    Write-Host "                       LenovoFnAndFunctionKeys around the write, then restore"
    Write-Host "                       them. Use this to tell 'the firmware refused' apart from"
    Write-Host "                       'Vantage reverted it'. Services are always restarted."
    Write-Host ""
    Write-Host "  WHY THE FANS DON'T RAMP IN PERFORMANCE MODE:" -ForegroundColor Yellow
    Write-Host "    Modes 1/2/3 are EC-owned curves. Performance raises the curve but still"
    Write-Host "    holds ~2800-3800 rpm until temps demand more, and it DROPS Fan_Set_FullSpeed."
    Write-Host "    Fan forcing is gated behind CUSTOM MODE (SmartFanMode 255) on this firmware."
    Write-Host "    Confirmed working on 82K8 / HACN46WW, on AC, elevated:"
    Write-Host "      .\Legion-FanControl.ps1 custom max        # 2800 -> 4300 rpm" -ForegroundColor Cyan
    Write-Host "      .\Legion-FanControl.ps1 custom off        # when done" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  EXAMPLES:" -ForegroundColor White
    Write-Host "    .\Legion-FanControl.ps1 custom max                     # (elevated, on AC)"
    Write-Host "    .\Legion-FanControl.ps1 custom off                     # (elevated)"
    Write-Host "    .\Legion-FanControl.ps1 custom status                  # (elevated)"
    Write-Host "    .\Legion-FanControl.ps1 monitor -IntervalSeconds 1"
    Write-Host "    .\Legion-FanControl.ps1 probe                          # (elevated)"
    Write-Host "    .\Legion-FanControl.ps1 cap 99                         # (elevated)"
    Write-Host ""
}

# =============================================================================
# Dispatch
# =============================================================================

switch ($Command) {
    'monitor' { Invoke-Monitor }

    'probe'   { Invoke-Probe }

    'cap' {
        $percent = 99
        if ($Value) {
            if ([int]::TryParse($Value, [ref]$null)) { $percent = [int]$Value }
            else {
                Write-Host "  [x] 'cap' expects a numeric percent (e.g. 'cap 99'). Got: '$Value'" -ForegroundColor Red
                break
            }
        }
        Set-CpuCap -Percent $percent
    }

    'uncap' { Reset-CpuCap }

    'mode' {
        $target = $null
        switch -Regex ($Value) {
            '^(q|quiet|1)$'             { $target = 1 }
            '^(b|balanced|balance|2)$'  { $target = 2 }
            '^(p|perf|performance|powerful|3)$' { $target = 3 }
            default {
                Write-Host "  [x] 'mode' expects quiet | balanced | performance (e.g. 'mode performance'). Got: '$Value'" -ForegroundColor Red
            }
        }
        if ($null -ne $target) { Invoke-SetMode -TargetMode $target }
    }

    'custom' {
        $action = if ([string]::IsNullOrWhiteSpace($Value)) { 'status' } else { $Value }
        Invoke-Custom -Action $action -Arg $Value2
    }

    'test' {
        Invoke-Test -Action $Value -Sub $Value2
    }

    'force' {
        $on = $null
        switch -Regex ($Value) {
            '^(on|true|1|full)$'  { $on = $true }
            '^(off|false|0)$'     { $on = $false }
            default {
                Write-Host "  [x] 'force' expects 'on' or 'off' (e.g. 'force on'). Got: '$Value'" -ForegroundColor Red
            }
        }
        if ($null -ne $on) { Invoke-ForceFan -On $on }
    }

    default { Show-Usage }
}
