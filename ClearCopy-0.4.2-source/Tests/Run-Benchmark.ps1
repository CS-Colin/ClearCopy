param(
    [Parameter(Mandatory=$true)][string]$CompilerPath,
    [Parameter(Mandatory=$true)][string]$BaselineApplication,
    [Parameter(Mandatory=$true)][string]$DevelopmentApplication,
    [Parameter(Mandatory=$true)][string]$TestRoot,
    [int]$Files = 512,
    [int]$Rounds = 3
)
$ErrorActionPreference = 'Stop'
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
if ($TestRoot.StartsWith('\\') -or (Test-Path -LiteralPath $TestRoot)) { throw 'Choose a NEW disposable local benchmark directory.' }
if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($TestRoot)).DriveType -ne [IO.DriveType]::Fixed) { throw 'Benchmarks require a fixed local drive; mapped/network drives are not allowed.' }
New-Item -ItemType Directory -Path $TestRoot | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$common = @('System.dll','System.Core.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$bins = @{}
foreach ($variant in @('v354','v036')) {
    $app = if ($variant -eq 'v354') { $BaselineApplication } else { $DevelopmentApplication }
    $app = (Resolve-Path -LiteralPath $app).Path
    $bin = Join-Path $TestRoot "$variant-bin"
    New-Item -ItemType Directory -Path $bin | Out-Null
    Copy-Item -LiteralPath $app -Destination (Join-Path $bin 'ClearCopy.exe')
    Copy-Item -LiteralPath ($app + '.config') -Destination (Join-Path $bin 'Benchmark.exe.config')
    $exe = Join-Path $bin 'Benchmark.exe'
    & $CompilerPath /nologo /target:exe /platform:x64 /langversion:14.0 "/out:$exe" @common "/reference:$app" (Join-Path $PSScriptRoot 'Phase1Benchmark.cs')
    if ($LASTEXITCODE -ne 0) { throw "Benchmark compiler failed: $variant" }
    $bins[$variant] = $exe
}
'Variant,Round,Scenario,Files,Milliseconds,AllocatedBytes,Gen2Collections,HashReadBytes,Copied,Skipped,Matched' | Set-Content -LiteralPath (Join-Path $TestRoot 'measurements.csv')
for ($round = 0; $round -le $Rounds; $round++) {
    $order = if ($round % 2) { @('v036','v354') } else { @('v354','v036') }
    foreach ($variant in $order) {
        $count = if ($round -eq 0) { 64 } else { $Files }
        $fixture = Join-Path $TestRoot "$variant-round$round"
        $lines = & $bins[$variant] $fixture $count
        if ($LASTEXITCODE -ne 0) { throw "Invalid benchmark: $variant round $round" }
        foreach ($line in $lines) {
            "$variant,$round,$line" | Add-Content -LiteralPath (Join-Path $TestRoot 'measurements.csv')
            "$variant round $round $line"
        }
    }
}
