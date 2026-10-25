# Publishes MediaRiffle for one platform and makes its release files in -OutDir. Used by the GitLab pipeline and the
# GitHub workflows alike; run from the repository root.
#
#   win-x64 / win-arm64:     MediaRiffle-v<ver>-<rid>.zip (portable / IIS) and MediaRiffle-v<ver>-<rid>-setup.exe
#                            (installs a Windows service), signed when SIG_PFX_B64 is set
#   linux-x64 / linux-arm64: mediariffle_<ver>-1_<amd64|arm64>.deb (Ubuntu/Debian) and
#                            MediaRiffle-v<ver>-<rid>.tar.gz (other systemd distros, with install.sh)
#
# Example: ./build/Package.ps1 -Rid linux-x64 -OutDir release-files
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')][string]$Rid,
    [string]$OutDir = 'release-files',
    # Defaults to <Version> in the project file; CI builds pass a -ci label instead
    [string]$Version,
    # A label for the file names only (CI artifacts), e.g. v0.1.0-ci12-abc1234
    [string]$Label
)

$ErrorActionPreference = 'Stop'
$project = 'src/MediaRiffle.Server/MediaRiffle.Server.csproj'
if (-not $Version) { $Version = ([regex]::Match((Get-Content $project -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value }
if (-not $Version) { throw "No <Version> in $project" }
if (-not $Label) { $Label = "v$Version" }
$publish = "publish/$Rid"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$out = (Resolve-Path $OutDir).Path

Write-Host "== Publishing MediaRiffle $Version for $Rid"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish $project -c Release -r $Rid --self-contained true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
# Development settings have no place in a release
Remove-Item "$publish/appsettings.Development.json" -ErrorAction SilentlyContinue
# The license goes with every copy (BSD 3-Clause: binary redistributions must carry it)
Copy-Item LICENSE $publish

if ($Rid -like 'win-*') {
    # Shipped beside the program: downloads and installs ffmpeg for it; and Admin -> Updates' updater (the installer
    # registers its task)
    Copy-Item deploy/windows/Install-Ffmpeg.ps1 $publish
    Copy-Item deploy/windows/Update-MediaRiffle.ps1 $publish
    ./build/Sign-Files.ps1 -Path "$publish/MediaRiffle.Server.exe"
    Compress-Archive -Path "$publish/*" -DestinationPath "$out/MediaRiffle-$Label-$Rid.zip" -Force
    $setup = "$out/MediaRiffle-$Label-$Rid-setup.exe"
    ./build/Build-Installer.ps1 -SourceDir $publish -Version $Version -Arch ($Rid -replace '^win-', '') -OutFile $setup
    ./build/Sign-Files.ps1 -Path $setup
}
else {
    $arch = if ($Rid -eq 'linux-x64') { 'amd64' } else { 'arm64' }
    # Debian's file name convention (~ sorts a pre-release before its release); CI builds carry their label instead
    $debName = if ($Label -eq "v$Version") { "mediariffle_$($Version.Replace('-', '~'))-1_$arch.deb" } else { "mediariffle_$Label`_$arch.deb" }
    # Both written by a .NET tool, so the Unix modes and root ownership are right whatever OS builds them
    dotnet run --file build/tools/LinuxPackages.cs -- $publish $Version $arch "$out/$debName" "$out/MediaRiffle-$Label-$Rid.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "LinuxPackages failed (exit $LASTEXITCODE)" }
}
Get-ChildItem $out | Where-Object { $_.Name -like "*$Label*" -or $_.Name -like "mediariffle_*" } | ForEach-Object { Write-Host "  $($_.Name)  $([math]::Round($_.Length / 1MB, 1)) MB" }
