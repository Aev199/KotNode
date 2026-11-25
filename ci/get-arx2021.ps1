$ErrorActionPreference = 'Stop'
# Autodesk's original 2021 Windows x64 SDK. Only use it on the runner.
$url = 'https://download.autodesk.com/esd/objectarx/2021/objectarx_for_autocad_2021_win_64bit_dlm.sfx.exe'
$archive = Join-Path $env:RUNNER_TEMP 'objectarx_for_autocad_2021_win_64bit_dlm.sfx.exe'
$extract = Join-Path $env:RUNNER_TEMP 'arx2021'
New-Item -ItemType Directory -Force $extract | Out-Null
& curl.exe --fail --location --retry 3 --retry-delay 3 --connect-timeout 30 --max-time 600 --output $archive $url
if ($LASTEXITCODE -ne 0) { throw "Autodesk SDK download failed ($LASTEXITCODE)." }
$hash=(Get-FileHash $archive -Algorithm SHA256).Hash
Write-Host "SDK archive SHA256: $hash"
# Pin the archive observed on both official downloads, and verify its publisher.
if ($hash -ne '6C0A808F61B6C421FF05213452B8DDA8C7C0E30ACA5C05F22FDD686F3CEF8B16') { throw 'Unexpected SDK archive hash; review the official download.' }
$signature=Get-AuthenticodeSignature $archive
Write-Host "SFX signature: $($signature.Status) $($signature.SignerCertificate.Subject)"
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Autodesk') {
    throw 'Cannot execute an SFX without a valid Autodesk signature.'
}
# Autodesk's extraction-only command, no installation.
$process=Start-Process -FilePath $archive -ArgumentList @('-suppresslaunch','-d',('"' + $extract + '"')) -PassThru
if (!$process.WaitForExit(180000)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw 'Autodesk SFX extraction timed out.'
}
if ($process.ExitCode -ne 0) { throw "Autodesk extraction failed ($($process.ExitCode))." }
$root=Get-ChildItem $extract -Recurse -File -Filter dbxHeaders.h | ForEach-Object {
    $candidate=$_.Directory.Parent.FullName
    if ((Test-Path (Join-Path $candidate 'inc\AcDbMgd.dll')) -and
        (Test-Path (Join-Path $candidate 'utils\HlrApi\inc')) -and
        (Test-Path (Join-Path $candidate 'utils\HlrApi\lib-x64\AsdkHlrApi24.lib'))) { $candidate }
} | Select-Object -First 1
if (!$root) { throw 'ObjectARX SDK layout or HLR development files not found.' }
Write-Host "ObjectARX SDK: $root"
"ARX2021=$root" | Out-File $env:GITHUB_ENV -Append -Encoding utf8
exit 0
