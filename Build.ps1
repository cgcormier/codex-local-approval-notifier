[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDir = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /warnaserror+ `
    /win32manifest:"$PSScriptRoot\app.manifest" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    /out:"$outputDir\CodexApprovalTray.exe" "$PSScriptRoot\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
Get-Item -LiteralPath "$outputDir\CodexApprovalTray.exe" | Select-Object FullName,Length
