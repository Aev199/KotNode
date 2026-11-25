$ErrorActionPreference='Stop'
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installer=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\setup.exe'
$components=@('Microsoft.VisualStudio.Component.VC.14.29.16.11.x86.x64','Microsoft.VisualStudio.Component.VC.14.29.16.11.CLI.Support')
$path=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
if (!$path) { throw 'Visual Studio not installed.' }
$ready=& $vswhere -latest -products '*' -requires $components -property installationPath | Select-Object -First 1
if (!$ready) {
    $args=@('modify','--installPath',('"' + $path + '"'),'--quiet','--norestart','--wait')
    foreach($component in $components){ $args += @('--add',$component) }
    $proc=Start-Process -FilePath $installer -ArgumentList $args -Wait -PassThru
    if ($proc.ExitCode -notin 0,3010) { throw "Visual Studio component install failed: $($proc.ExitCode)" }
}
$ready=& $vswhere -latest -products '*' -requires $components -property installationPath | Select-Object -First 1
if (!$ready) { throw 'v142 C++/CLI components not detected after installation.' }
Write-Host "v142 C++/CLI ready: $ready"
