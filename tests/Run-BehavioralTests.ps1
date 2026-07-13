[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$obj = Join-Path $repoRoot 'obj'
New-Item -Path $obj -ItemType Directory -Force | Out-Null
$framework64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework32 = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$csc = if (Test-Path -LiteralPath $framework64) { $framework64 } else { $framework32 }
if (-not (Test-Path -LiteralPath $csc)) {
    throw 'Could not find csc.exe for behavioral tests.'
}

$output = Join-Path $obj 'TempProfileFixer.BehavioralTests.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -Filter '*.cs' -File | ForEach-Object { $_.FullName })
$generatedVersionSource = Join-Path $obj 'GeneratedBehavioralVersionInfo.cs'
Set-Content -LiteralPath $generatedVersionSource -Encoding UTF8 -Value @(
    'using System.Reflection;',
    '[assembly: AssemblyVersion("0.2.0.0")]',
    '[assembly: AssemblyFileVersion("0.2.0.0")]',
    '[assembly: AssemblyInformationalVersion("0.2.0")]'
)
$sources += $generatedVersionSource
$sources += Join-Path $repoRoot 'tests\BehavioralTests.cs'

& $csc /nologo /target:exe /platform:anycpu /optimize+ /main:TempProfileFixer.Tests.BehavioralTests `
    "/out:$output" `
    "/resource:$(Join-Path $repoRoot 'assets\TempProfileFixer.png'),TempProfileFixer.Assets.TempProfileFixer.png" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Management.dll `
    /reference:System.Windows.Forms.dll `
    $sources
if ($LASTEXITCODE -ne 0) {
    throw "Behavioral-test compile failed with exit code $LASTEXITCODE."
}

& $output
if ($LASTEXITCODE -ne 0) {
    throw "Behavioral tests failed with exit code $LASTEXITCODE."
}
