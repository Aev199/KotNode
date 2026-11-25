param(
    [Parameter(Mandatory=$true)][string]$ArxSdk,
    [string]$PlatformToolset='v142'
)
$ErrorActionPreference='Stop'
$ArxSdk=(Resolve-Path $ArxSdk).Path
if (!(Test-Path (Join-Path $ArxSdk 'inc\dbxHeaders.h')) -or !(Test-Path (Join-Path $ArxSdk 'utils\HlrApi\inc'))) {
    throw 'Specify the extracted ObjectARX 2021 SDK root (inc, lib-x64, utils/HlrApi).'
}
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path $vswhere)) { throw 'Visual Studio with C++ and C++/CLI is required.' }
$msbuild=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (!$msbuild) { throw 'MSBuild not found.' }
Push-Location $PSScriptRoot
try {
    & $msbuild src/NativeHlr/KotNode.HlrBridge.vcxproj /t:Rebuild /p:Configuration=Release /p:Platform=x64 "/p:PlatformToolset=$PlatformToolset" "/p:ArxSdk=$ArxSdk" /m:1
    if ($LASTEXITCODE -ne 0) { throw 'Native HLR build failed.' }
    New-Item -ItemType Directory -Force -Path plugin | Out-Null
    Copy-Item src/NativeHlr/bin/KotNode.HlrBridge.dll plugin -Force
    Write-Host 'Ready: plugin/KotNode.HlrBridge.dll'
} finally { Pop-Location }
