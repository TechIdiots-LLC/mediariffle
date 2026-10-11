# Downloads ffmpeg (the "release essentials" build from gyan.dev), checks it against the SHA-256 gyan.dev publishes,
# installs it to C:\Tools\ffmpeg (bin\ffmpeg.exe, bin\ffprobe.exe), points MediaRiffle's settings at it and restarts the
# service. Run again to update ffmpeg. Run as administrator:
#
#   powershell -ExecutionPolicy Bypass -File "C:\Program Files\MediaRiffle\Install-Ffmpeg.ps1"
#
# gyan.dev's builds are x64; on Windows on ARM they run under emulation.
param(
    [string]$Destination = 'C:\Tools\ffmpeg',
    [string]$Settings = (Join-Path $env:ProgramData 'MediaRiffle\appsettings.json'),
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with its progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this as administrator.' }

$base = 'https://www.gyan.dev/ffmpeg/builds'
$version = (Invoke-RestMethod "$base/release-version").ToString().Trim()
Write-Host "ffmpeg $version (gyan.dev release essentials)"

$work = Join-Path ([IO.Path]::GetTempPath()) "ffmpeg-$([guid]::NewGuid())"
New-Item -ItemType Directory $work | Out-Null
try {
    $zip = Join-Path $work 'ffmpeg.zip'
    Write-Host 'Downloading...'
    Invoke-WebRequest "$base/ffmpeg-release-essentials.zip" -OutFile $zip -UseBasicParsing
    $expected = (Invoke-RestMethod "$base/ffmpeg-release-essentials.zip.sha256").ToString().Trim().Split(' ')[0].ToLowerInvariant()
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "The download doesn't match gyan.dev's checksum (got $actual, expected $expected); not installing it." }
    Write-Host 'Checksum OK.'

    Expand-Archive $zip -DestinationPath (Join-Path $work 'x')
    # The zip holds one folder, ffmpeg-<version>-essentials_build, with bin\ inside
    $unpacked = Get-ChildItem (Join-Path $work 'x') -Directory | Select-Object -First 1
    if (-not (Test-Path (Join-Path $unpacked.FullName 'bin\ffprobe.exe'))) { throw 'ffprobe.exe not found in the download.' }

    # The service has ffmpeg open while it streams: stop it while ffmpeg is replaced
    $service = Get-Service MediaRiffle -ErrorAction SilentlyContinue
    $wasRunning = $service -and $service.Status -eq 'Running'
    if ($wasRunning) { Write-Host 'Stopping MediaRiffle...'; Stop-Service MediaRiffle }

    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    New-Item -ItemType Directory -Force (Split-Path $Destination) | Out-Null
    Move-Item $unpacked.FullName $Destination
    $bin = Join-Path $Destination 'bin'
    Write-Host "Installed to $bin"
    & (Join-Path $bin 'ffprobe.exe') -version | Select-Object -First 1

    # Point the settings at it, if they say somewhere else
    if (Test-Path $Settings) {
        $text = [IO.File]::ReadAllText($Settings)
        $json = $bin.Replace('\', '\\')
        if ($text -match '"FfmpegFolder"\s*:\s*"([^"]*)"') {
            if ($Matches[1] -ne $json) {
                # In a -replace replacement only $ is special, so the JSON-escaped path goes in as it is
                $text = $text -replace '"FfmpegFolder"\s*:\s*"[^"]*"', ('"FfmpegFolder": "' + $json + '"')
                [IO.File]::WriteAllText($Settings, $text)
                Write-Host "Set FfmpegFolder in $Settings"
            }
        }
        else {
            Write-Warning "No FfmpegFolder in $Settings; add `"FfmpegFolder`": `"$json`" under MediaRiffle > Transcoding."
        }
    }

    if ($service -and -not $NoRestart) {
        Write-Host 'Starting MediaRiffle...'
        Start-Service MediaRiffle
        Write-Host 'Done. In MediaRiffle, Admin > Scan now reads any files that failed before ffmpeg was there.'
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
