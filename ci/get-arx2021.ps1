$ErrorActionPreference = 'Stop'
# Autodesk's original 2021 Windows x64 SDK. Download on the runner;
# do not publish Autodesk SDK files or cache them in the public repository.
$url = 'https://download.autodesk.com/esd/objectarx/2021/objectarx_for_autocad_2021_win_64bit_dlm.sfx.exe'
$archive = Join-Path $env:RUNNER_TEMP 'objectarx_for_autocad_2021_win_64bit_dlm.sfx.exe'
$extract = Join-Path $env:RUNNER_TEMP 'arx2021'
New-Item -ItemType Directory -Force $extract | Out-Null
& curl.exe --fail --location --retry 3 --retry-delay 3 --connect-timeout 30 --max-time 600 --output $archive $url
if ($LASTEXITCODE -ne 0) { throw "Autodesk SDK download failed ($LASTEXITCODE). See official SDK download page." }
if ((Get-Item $archive).Length -lt 1MB) { throw 'SDK download is unexpectedly small.' }
Write-Host "SDK archive SHA256: $((Get-FileHash $archive -Algorithm SHA256).Hash)"
$seven = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
if (!(Test-Path $seven)) { throw '7-Zip not found.' }
# Autodesk's SFX is a PE executable; force 7z format rather than extracting PE resources.
& $seven x -t7z $archive "-o$extract" -y
if ($LASTEXITCODE -gt 1) { Write-Host 'Embedded 7z extraction failed; will use the Autodesk extractor if needed.' }
function Find-Sdk {
    Get-ChildItem $extract -Recurse -File -Filter dbxHeaders.h | ForEach-Object {
        $candidate = $_.Directory.Parent.FullName
        if ((Test-Path (Join-Path $candidate 'inc\AcDbMgd.dll')) -and
            (Test-Path (Join-Path $candidate 'utils\HlrApi\inc')) -and
            (Test-Path (Join-Path $candidate 'utils\HlrApi\lib-x64'))) { $candidate }
    } | Select-Object -First 1
}
$root = Find-Sdk
if (!$root) {
    # Official extraction-only command; do not run a product installer.
    $signature=Get-AuthenticodeSignature $archive
    Write-Host "SFX signature: $($signature.Status) $($signature.SignerCertificate.Subject)"
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Autodesk') {
        throw 'Cannot execute an SFX without a valid Autodesk signature.'
    }
    $process=Start-Process -FilePath $archive -ArgumentList @('-suppresslaunch','-d',('"' + $extract + '"')) -PassThru
    if (!$process.WaitForExit(180000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw 'Autodesk SFX extraction timed out.'
    }
    Write-Host "Autodesk extraction exit code: $($process.ExitCode)"
    $root=Find-Sdk
}
if (!$root) {
    $nested = @(Get-ChildItem $extract -Recurse -File | Where-Object Extension -in '.zip','.7z')
    foreach ($file in $nested) {
        $dest = Join-Path $file.DirectoryName ($file.BaseName + '-expanded')
        & $seven x $file.FullName "-o$dest" -y
        if ($LASTEXITCODE -gt 1) { throw "Nested extraction failed: $($file.Name)" }
    }
    $root = Find-Sdk
}
if (!$root) {
    Get-ChildItem $extract -Depth 2 | Select-Object FullName | Format-Table -AutoSize
    throw 'ObjectARX SDK layout or HLR development files not found.'
}
Write-Host "ObjectARX SDK: $root"
Get-ChildItem (Join-Path $root 'utils\HlrApi\lib-x64') -File | Select-Object Name,Length | Format-Table
"ARX2021=$root" | Out-File $env:GITHUB_ENV -Append -Encoding utf8
