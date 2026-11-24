param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if ($CompilerPath) {
    $CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
    $output = Join-Path $PSScriptRoot 'bin\Release\net48'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $references = @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'Microsoft.VisualBasic.dll', 'System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
    $references += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'UIAutomationClient.dll', 'UIAutomationTypes.dll', 'UIAutomationProvider.dll') | ForEach-Object { '/reference:' + (Join-Path "$frameworkPath\WPF" $_) }
    $sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'ClearCopy'), (Join-Path $PSScriptRoot 'Properties') -Filter '*.cs' -File -Recurse | Select-Object -ExpandProperty FullName
    & $CompilerPath /nologo /target:winexe /platform:x64 /unsafe+ /optimize+ /langversion:14.0 '/main:ClearCopy.Program' "/out:$output\ClearCopy.exe" "/win32icon:$PSScriptRoot\app.ico" "/win32manifest:$PSScriptRoot\app.manifest" "/resource:$PSScriptRoot\ClearCopy.ico,ClearCopy.ico" @references @sources
    if ($LASTEXITCODE -ne 0) { throw 'ClearCopy standalone compiler build failed.' }
    @'
<?xml version="1.0" encoding="utf-8"?>
<configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup><runtime><AppContextSwitchOverrides value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false"/></runtime></configuration>
'@ | Set-Content -LiteralPath "$output\ClearCopy.exe.config" -Encoding utf8
    return
}
dotnet build (Join-Path $PSScriptRoot 'ClearCopy.csproj') -c Release -p:AutomaticallyUseReferenceAssemblyPackages=false "-p:FrameworkPathOverride=$frameworkPath" -p:ImplicitUsings=disable --ignore-failed-sources --nologo
if ($LASTEXITCODE -ne 0) { throw 'ClearCopy build failed.' }
