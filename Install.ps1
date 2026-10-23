[CmdletBinding()]
param([switch]$NoLaunch, [switch]$NoStartup)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build.ps1')
& (Join-Path $PSScriptRoot 'Test.ps1')

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\CodexApprovalTray'
$exe = Join-Path $installDir 'CodexApprovalTray.exe'
$codexDir = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$hooksPath = Join-Path $codexDir 'hooks.json'
$configPath = Join-Path $codexDir 'config.toml'
if ((Test-Path -LiteralPath $configPath) -and
    (Select-String -LiteralPath $configPath -Pattern '^\s*\[\[?hooks[.\]]' -Quiet)) {
    throw 'Inline hooks already exist in config.toml. Merge into that representation to avoid duplicate hook layers.'
}
if ($exe -match '\s') { throw 'This minimal installer needs an install path without spaces for shell-independent hook invocation.' }

$hooksConfig = if (Test-Path -LiteralPath $hooksPath) {
    Get-Content -LiteralPath $hooksPath -Raw | ConvertFrom-Json
} else { [pscustomobject]@{} }
if ($null -eq $hooksConfig) { throw 'The existing hooks file is empty or invalid; it was left unchanged.' }
if (-not $hooksConfig.PSObject.Properties['hooks']) {
    $hooksConfig | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{})
}
if ($null -eq $hooksConfig.hooks) { throw 'The existing hooks table is null; it was left unchanged.' }
$command = $exe.Replace('\', '/') + ' --hook'
foreach ($eventName in @('PermissionRequest','Stop','Interrupt','SessionEnd')) {
    $property = $hooksConfig.hooks.PSObject.Properties[$eventName]
    $groups = if ($property) { @($property.Value) } else { @() }
    $exists = @($groups | Where-Object {
        @($_.hooks | Where-Object { $_.command -ceq $command }).Count -gt 0
    }).Count -gt 0
    if (-not $exists) {
        $groups += [pscustomobject]@{
            hooks = @([pscustomobject]@{
                type = 'command'
                command = $command
                timeout = 2
                statusMessage = 'Codex Approval Tray'
            })
        }
    }
    if ($property) { $property.Value = @($groups) }
    else { $hooksConfig.hooks | Add-Member -NotePropertyName $eventName -NotePropertyValue @($groups) }
}
$newJson = ($hooksConfig | ConvertTo-Json -Depth 60) + [Environment]::NewLine
$newJson | ConvertFrom-Json | Out-Null

$running = @(Get-Process -Name CodexApprovalTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
if ($running.Count -gt 0) {
    $exitProcess = Start-Process -FilePath $exe -ArgumentList '--exit' -WindowStyle Hidden -PassThru
    $exitProcess.WaitForExit(2000) | Out-Null
    foreach ($process in $running) {
        if (-not $process.WaitForExit(2000)) { throw 'Exit Codex Approval Tray before reinstalling.' }
    }
}
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build\CodexApprovalTray.exe') -Destination $exe
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $installDir 'README.md')
New-Item -ItemType Directory -Path $codexDir -Force | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
if (-not (Test-Path -LiteralPath $hooksPath) -or
    [IO.File]::ReadAllText($hooksPath) -cne $newJson) {
    if (Test-Path -LiteralPath $hooksPath) {
        $backup = $hooksPath + '.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-ffff')
        Copy-Item -LiteralPath $hooksPath -Destination $backup
    }
    [IO.File]::WriteAllText($hooksPath, $newJson, $utf8)
}
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (-not $NoStartup) {
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name CodexApprovalTray -Value ('"' + $exe + '"') -PropertyType String -Force | Out-Null
}
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Approval Tray.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'Approval alerts for local Codex sessions'
$shortcut.WindowStyle = 7
$shortcut.Save()
if (-not $NoLaunch) { Start-Process -FilePath $exe -WindowStyle Hidden }
[pscustomobject]@{ Executable = $exe; Hooks = $hooksPath; Shortcut = $shortcutPath; StartWithWindows = -not $NoStartup }
Write-Output 'Installed. Restart Codex, then use /hooks to review and trust the four Codex Approval Tray handlers.'
