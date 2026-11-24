param(
    [Parameter(Mandatory=$true)][string]$CompilerPath,
    [Parameter(Mandatory=$true)][string]$TestRoot
)
$ErrorActionPreference = 'Stop'
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
if ($TestRoot.StartsWith('\\') -or (Test-Path -LiteralPath $TestRoot)) { throw 'Choose a NEW disposable local test directory.' }
if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($TestRoot)).DriveType -ne [IO.DriveType]::Fixed) { throw 'Tests require a fixed local drive; mapped/network drives are not allowed.' }
New-Item -ItemType Directory -Path $TestRoot | Out-Null
$app = Join-Path $PSScriptRoot '..\Source\bin\Release\net48\ClearCopy.exe'
$bin = Join-Path $TestRoot 'bin'
New-Item -ItemType Directory -Path $bin | Out-Null
Copy-Item -LiteralPath $app -Destination $bin
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Fixtures\legacy354job.xml') -Destination $bin
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path "$framework\WPF" $_) }
$references += '/reference:' + [IO.Path]::GetFullPath($app)
$failures = @()
foreach ($name in @('Phase1Regression','TransferCheck','SafetyCheck','CheckpointCheck')) {
    $exe = Join-Path $bin "$name.exe"
    & $CompilerPath /nologo /target:exe /platform:x64 /langversion:14.0 "/out:$exe" @references (Join-Path $PSScriptRoot "$name.cs")
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile $name" }
    Copy-Item -LiteralPath ($app + '.config') -Destination ($exe + '.config')
    $fixture = Join-Path $TestRoot $name
    if ($name -eq 'SafetyCheck') {
        New-Item -ItemType Directory -Path $fixture | Out-Null
        $linkTarget = Join-Path $fixture 'link-target'
        New-Item -ItemType Directory -Path $linkTarget | Out-Null
        New-Item -ItemType Junction -Path (Join-Path $fixture 'junction') -Target $linkTarget | Out-Null
    }
    & $exe $fixture 70 2>&1 | Tee-Object -FilePath (Join-Path $TestRoot "$name.txt")
    $code = $LASTEXITCODE
    "HARNESS $name EXIT $code" | Tee-Object -FilePath (Join-Path $TestRoot 'summary.txt') -Append
    if ($code -ne 0) { $failures += "$name ($code)" }
}
if ($failures.Count) { throw ('Test failures: ' + ($failures -join ', ')) }
