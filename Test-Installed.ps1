[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$exe = Join-Path $env:LOCALAPPDATA 'Programs\CodexApprovalTray\CodexApprovalTray.exe'

function Invoke-App {
    param([string]$Arguments, [AllowNull()][string]$InputText)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $exe; $start.Arguments = $Arguments
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true
    $p = [Diagnostics.Process]::Start($start)
    if ($InputText) { $p.StandardInput.Write($InputText) }
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(5000)) { throw 'Resident app command timed out.' }
    $text = $p.StandardOutput.ReadToEnd()
    $code = $p.ExitCode; $p.Dispose()
    if ($code -ne 0) { throw "Resident app command failed: $Arguments" }
    return ($text | ConvertFrom-Json)
}
$initial = Invoke-App '--status'
if (-not $initial.running) { throw 'Tray app is not running.' }
$prefix = 'tray-smoke-' + [Guid]::NewGuid().ToString('N')
$passed = @()
try {
    $null = Invoke-App '--pause'
    if (-not (Invoke-App '--status').paused) { throw 'Pause did not apply.' }
    $passed += 'pause applied'
    # Start four hook processes before waiting: distinct windows/sessions sharing one folder.
    $children = foreach ($index in 1..4) {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $exe; $start.Arguments = '--hook'
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true
        $p = [Diagnostics.Process]::Start($start)
        $inputObject = @{ hook_event_name = 'PermissionRequest'; session_id = "$prefix-$index"; turn_id = 'turn'; cwd = 'C:\same-folder'; tool_name = 'Bash' }
        $p.StandardInput.Write(($inputObject | ConvertTo-Json -Compress)); $p.StandardInput.Close()
        $p
    }
    foreach ($p in $children) {
        if (-not $p.WaitForExit(5000) -or $p.ExitCode -ne 0 -or $p.StandardOutput.ReadToEnd().Trim() -ne '{}') {
            throw 'Concurrent hook invocation failed.'
        }
        $p.Dispose()
    }
    $delivered = (Invoke-App '--status').alerts
    if ($delivered -ne ($initial.alerts + 4)) { throw "Concurrent delivery count was $delivered; expected $($initial.alerts + 4)." }
    $passed += 'four simultaneous sessions retained independently while paused'
    $null = Invoke-App '--hook' (@{ hook_event_name = 'Stop'; session_id = "$prefix-1"; turn_id = 'turn' } | ConvertTo-Json -Compress)
    if ((Invoke-App '--status').alerts -ne ($initial.alerts + 3)) { throw 'Turn cleanup affected another session.' }
    $passed += 'turn cleanup affected only its own session'
    foreach ($index in 2..4) {
        $null = Invoke-App '--hook' (@{ hook_event_name = 'SessionEnd'; session_id = "$prefix-$index" } | ConvertTo-Json -Compress)
    }
    if ((Invoke-App '--status').alerts -ne $initial.alerts) { throw 'Session cleanup left stale notices.' }
    $passed += 'session cleanup restored original alert count'
    if ($initial.alerts -eq 0) {
        $null = Invoke-App '--hook' (@{ hook_event_name = 'PermissionRequest'; session_id = "$prefix-dismiss"; turn_id = 'turn'; tool_name = 'Bash' } | ConvertTo-Json -Compress)
        $null = Invoke-App '--dismiss-all'
        if ((Invoke-App '--status').alerts -ne 0) { throw 'Dismiss all failed.' }
        $passed += 'dismiss all cleared notices'
    }
} finally {
    foreach ($index in @('1','2','3','4','dismiss')) {
        $null = Invoke-App '--hook' (@{ hook_event_name = 'SessionEnd'; session_id = "$prefix-$index" } | ConvertTo-Json -Compress)
    }
    $null = Invoke-App $(if ($initial.paused) { '--pause' } else { '--resume' })
}
if ((Invoke-App '--status').paused -ne $initial.paused) { throw 'Original pause setting was not restored.' }
$passed += 'original pause setting restored'
[pscustomobject]@{ Passed = $passed; Status = (Invoke-App '--status') }
