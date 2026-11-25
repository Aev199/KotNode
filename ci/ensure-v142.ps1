$ErrorActionPreference='Stop'
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installer=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\setup.exe'
$components=@('Microsoft.VisualStudio.Component.VC.14.29.16.11.x86.x64','Microsoft.VisualStudio.Component.VC.14.29.16.11.CLI.Support')
$path=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
if (!$path) { throw 'Visual Studio not installed.' }
$ready=& $vswhere -latest -products '*' -requires $components -property installationPath | Select-Object -First 1
if (!$ready) {
    $args=@('modify','--installPath',('"' + $path + '"'),'--quiet','--norestart')
    foreach($component in $components){ $args += @('--add',$component) }
    # setup.exe does not support --wait (only the bootstrapper does).
    Write-Host "Installing v142 components into $path"
    $proc=Start-Process -FilePath $installer -ArgumentList $args -Wait -PassThru
    if ($proc.ExitCode -notin 0,3010) {
        Get-ChildItem $env:TEMP -Filter 'dd_setup*log' | Sort-Object LastWriteTime -Descending | Select-Object -First 2 | ForEach-Object {
            Write-Host $_.Name
            Get-Content $_.FullName -Tail 60
        }
        throw "Visual Studio component install failed: $($proc.ExitCode)"
    }
    # Some installer versions delegate work to a service; verify actual registration.
    for ($i=0; $i -lt 60; $i++) {
        $ready=& $vswhere -latest -products '*' -requires $components -property installationPath | Select-Object -First 1
        if ($ready) { break }
        Start-Sleep -Seconds 10
    }
}
$ready=& $vswhere -latest -products '*' -requires $components -property installationPath | Select-Object -First 1
if (!$ready) { throw 'v142 C++/CLI components not detected after installation.' }
Write-Host "v142 C++/CLI ready: $ready"

exit 0
