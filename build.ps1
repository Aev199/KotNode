$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build src/AutoCAD/KotNode.AutoCAD.csproj -c Release -m:1 -p:BuildInParallel=false
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed' }
    dotnet build tests/KotNode.Tests.csproj -c Release -m:1 -p:BuildInParallel=false
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
    dotnet tests/bin/Release/net8.0/KotNode.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    New-Item -ItemType Directory -Path plugin -Force | Out-Null
    Copy-Item src/AutoCAD/bin/Release/net48/KotNode.AutoCAD.dll plugin -Force
    Copy-Item src/Core/bin/Release/net48/KotNode.Core.dll plugin -Force
    Copy-Item src/AutoCAD/bin/Release/net48/KotNode.AutoCAD.pdb plugin -Force
    Copy-Item src/Core/bin/Release/net48/KotNode.Core.pdb plugin -Force
    Write-Host 'Ready: plugin/KotNode.AutoCAD.dll. Load with NETLOAD in AutoCAD 2021.'
} finally { Pop-Location }
