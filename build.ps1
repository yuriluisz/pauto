param(
    [switch]$TestOnly
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$distDir = Join-Path $root 'dist'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fxDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$winMdDir = 'C:\Windows\System32\WinMetadata'

if (-not (Test-Path $csc)) {
    throw "C# compiler not found at $csc"
}

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

$logicSrc = Join-Path $root 'src\AutoPauseLogic.cs'
$testSrc = Join-Path $root 'tests\AutoPauseLogicTests.cs'
$testExe = Join-Path $distDir 'pauto-tests.exe'

Write-Host 'Compiling unit tests...'
& $csc /nologo /target:exe /codepage:65001 /warnaserror /out:$testExe $logicSrc $testSrc
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host 'Running unit tests...'
& $testExe
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($TestOnly) {
    exit 0
}

$programSrc = Join-Path $root 'src\Program.cs'
$appExe = Join-Path $distDir 'pauto.exe'

Write-Host 'Compiling dist\pauto.exe...'
& $csc /nologo /target:winexe /codepage:65001 /optimize+ /warnaserror `
    /out:$appExe `
    "/r:$fxDir\System.dll" `
    "/r:$fxDir\System.Core.dll" `
    "/r:$fxDir\System.Drawing.dll" `
    "/r:$fxDir\System.Windows.Forms.dll" `
    "/r:$fxDir\System.Runtime.dll" `
    "/r:$fxDir\System.Threading.Tasks.dll" `
    "/r:$fxDir\System.Runtime.InteropServices.WindowsRuntime.dll" `
    "/r:$winMdDir\Windows.Media.winmd" `
    "/r:$winMdDir\Windows.Foundation.winmd" `
    $logicSrc $programSrc

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Build succeeded: $appExe"
