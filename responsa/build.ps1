# IT-780: builds dist\responsa-mcp.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# Nothing is downloaded. x86, to match the 32-bit Responsa (cross-process list reads use the 32-bit layout).
#   .\build.ps1          build the server
#   .\build.ps1 -Test    also build and run the offline tests (no Responsa needed)
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4 compiler not found: $csc" }
$dist = Join-Path $here 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$src = Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$refs = '/r:System.Web.Extensions.dll', '/r:System.Core.dll'
$exe = Join-Path $dist 'responsa-mcp.exe'
& $csc /nologo /target:exe /platform:x86 /optimize+ /codepage:65001 "/out:$exe" @refs @src
if ($LASTEXITCODE) { throw "build failed" }
Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB) SHA256 $((Get-FileHash $exe).Hash)"

if ($Test) {
    $testSrc = @($src | Where-Object { (Split-Path $_ -Leaf) -ne 'Program.cs' }) + (Join-Path $here 'test\OfflineTests.cs')
    $texe = Join-Path $dist 'responsa-mcp-tests.exe'
    & $csc /nologo /target:exe /platform:x86 /codepage:65001 "/out:$texe" @refs @testSrc
    if ($LASTEXITCODE) { throw "test build failed" }
    & $texe (Join-Path $here 'test\samples')
    if ($LASTEXITCODE) { throw "offline tests failed" }
}
