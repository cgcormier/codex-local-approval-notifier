[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'build\CodexApprovalTray.exe'
$resultDir = Join-Path $PSScriptRoot 'test-results'
New-Item -ItemType Directory -Path $resultDir -Force | Out-Null

function Invoke-TrayTest {
    param([string]$Arguments, [AllowNull()][string]$InputText)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $exe
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    if ($null -ne $InputText) { $process.StandardInput.Write($InputText) }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(6000)) { throw 'Test helper did not finish in six seconds.' }
    $result = [pscustomobject]@{ Code = $process.ExitCode; Output = $process.StandardOutput.ReadToEnd().Trim(); Error = $process.StandardError.ReadToEnd() }
    $process.Dispose()
    return $result
}

$resultPath = Join-Path $resultDir ('self-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss-ffff') + '.json')
$self = Invoke-TrayTest -Arguments ('--self-test "' + $resultPath + '"')
$results = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($self.Code -ne 0 -or $results.error) { throw ($results | ConvertTo-Json -Depth 5) }
$hookTimes = @()
foreach ($inputText in @('not-json','{}','{"hook_event_name":"Stop","session_id":"test-completed","turn_id":"test"}',
    '{"hook_event_name":"PermissionRequest","session_id":"test-empty-tool","turn_id":"test"}')) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $hook = Invoke-TrayTest -Arguments '--hook' -InputText $inputText
    $watch.Stop(); $hookTimes += $watch.Elapsed.TotalMilliseconds
    if ($hook.Code -ne 0 -or $hook.Output -ne '{}' -or $hook.Error) { throw 'Hook did not return only successful neutral JSON.' }
}
# The test permission notice is cleaned up if a resident instance is present.
$null = Invoke-TrayTest -Arguments '--hook' -InputText '{"hook_event_name":"SessionEnd","session_id":"test-empty-tool"}'
[pscustomobject]@{
    CoreChecksPassed = @($results.passed).Count
    HookChecksPassed = 4
    HookMaxMilliseconds = [Math]::Round(($hookTimes | Measure-Object -Maximum).Maximum)
    Preview = [IO.Path]::ChangeExtension($resultPath, '.png')
    Results = $resultPath
}
