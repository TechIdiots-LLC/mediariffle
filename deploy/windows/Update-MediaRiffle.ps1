# Updates MediaRiffle to the release an admin picked in Admin -> Updates. Shipped beside the program; the installer
# runs it with -Register, which creates the "MediaRiffle Update" scheduled task: as SYSTEM, it runs this when
# MediaRiffle writes event 4100 to the Application log after asking for an update (the service's own account can't
# install anything), and at startup, for a request left waiting.
#
# The request (%ProgramData%\MediaRiffle\update\request) names only a version (and GitLab or GitHub); this downloads
# that release's setup and SHA256SUMS from the project's own address, checks the setup against them (and, when the
# installed program is signed, that the setup is signed by the same publisher), saves the database and runs the
# setup silently, which keeps the settings and data. It then waits for the new version to start (it writes its version
# to update\running) and, if it doesn't, puts the database back and runs the old version's setup again. Never an
# older version than the one installed. How it goes is in update\status.json, for Admin, and in
# %ProgramData%\MediaRiffle-update\update.log.
#
#   Update-MediaRiffle.ps1              do the update asked for, if any (the task)
#   Update-MediaRiffle.ps1 -Register    create the task (the installer), unless it's there
#   Update-MediaRiffle.ps1 -Unregister  remove it (the uninstaller)
param([switch]$Register, [switch]$Unregister)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with its progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

$TaskName = 'MediaRiffle Update'
$Service = 'MediaRiffle'
$UninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MediaRiffle'
$Dir = Join-Path $env:ProgramData 'MediaRiffle\update'
$Request = Join-Path $Dir 'request'
$Status = Join-Path $Dir 'status.json'
$Running = Join-Path $Dir 'running'
# SYSTEM and administrators only: the downloads and the copy of this script that runs
$Work = Join-Path $env:ProgramData 'MediaRiffle-update'
$Log = Join-Path $Work 'update.log'
$StartSeconds = 180

if ($Unregister) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    exit 0
}

if ($Register) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { exit 0 }
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action = New-ScheduledTaskAction -Execute $ps -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $class = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler
    $onEvent = New-CimInstance -CimClass $class -ClientOnly
    $onEvent.Enabled = $true
    $onEvent.Subscription = "<QueryList><Query Id='0' Path='Application'><Select Path='Application'>*[System[Provider[@Name='$Service'] and EventID=4100]]</Select></Query></QueryList>"
    $atStartup = New-ScheduledTaskTrigger -AtStartup
    $atStartup.Delay = 'PT2M'
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($onEvent, $atStartup) -Principal $principal -Settings $settings `
        -Description 'Updates MediaRiffle to the release asked for in Admin -> Updates.' | Out-Null
    exit 0
}

# -- The update --------------------------------------------------------------------------------------

# Its own folder, which only SYSTEM and administrators can change
if (-not (Test-Path $Work)) {
    New-Item -ItemType Directory $Work | Out-Null
    icacls $Work /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
}
# The installer replaces this script while it runs: it runs from a copy there
$self = Join-Path $Work 'Update-MediaRiffle.ps1'
if ($PSCommandPath -ne $self) {
    Copy-Item $PSCommandPath $self -Force
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $self
    exit $LASTEXITCODE
}

$mutex = New-Object Threading.Mutex($false, 'Global\MediaRiffleUpdate')
if (-not $mutex.WaitOne(0)) { exit 0 }

function Say([string]$text) { Add-Content -Path $Log -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $text" -Encoding UTF8 }

$script:Id = ''; $script:Version = ''; $script:From = ''
# status.json for Admin: written beside the request in the service's folder, and moved into place, so never through
# a link put there
function Set-Status([string]$state, [string]$message) {
    $json = [ordered]@{ id = $script:Id; version = $script:Version; from = $script:From; state = $state; message = $message
        updatedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); log = $Log } | ConvertTo-Json -Compress
    $tmp = Join-Path $Dir ".status.$([guid]::NewGuid().ToString('N'))"
    [IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item $tmp $Status -Force
    Say "${state}: $message"
}

function Test-Link([string]$path) { (Test-Path $path) -and ((Get-Item $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) }

# 1.2.3-rc.1 before 1.2.3; numeric pre-release parts by number
function Compare-Version([string]$a, [string]$b) {
    $pa = $a.Split('-', 2); $pb = $b.Split('-', 2)
    $c = ([version]$pa[0]).CompareTo([version]$pb[0])
    if ($c -ne 0) { return $c }
    if ($pa.Count -eq 1 -and $pb.Count -eq 1) { return 0 }
    if ($pa.Count -eq 1) { return 1 }    # a release after its pre-releases
    if ($pb.Count -eq 1) { return -1 }
    $xa = $pa[1].Split('.'); $xb = $pb[1].Split('.')
    for ($i = 0; $i -lt [Math]::Min($xa.Count, $xb.Count); $i++) {
        $na = 0; $nb = 0
        $ia = [int]::TryParse($xa[$i], [ref]$na); $ib = [int]::TryParse($xb[$i], [ref]$nb)
        if ($ia -and $ib) { $c = $na.CompareTo($nb) } elseif ($ia) { $c = -1 } elseif ($ib) { $c = 1 } else { $c = [string]::CompareOrdinal($xa[$i], $xb[$i]) }
        if ($c -ne 0) { return [Math]::Sign($c) }
    }
    return $xa.Count.CompareTo($xb.Count)
}

try {
    if (-not (Test-Path $Dir -PathType Container) -or (Test-Link $Dir)) { exit 0 }
    if (-not (Test-Path $Request -PathType Leaf) -or (Test-Link $Request)) { exit 0 }
    $lines = Get-Content $Request -TotalCount 10
    Remove-Item $Request -Force
    $field = @{}
    foreach ($line in $lines) { if ($line -match '^(\w+)=(.*)$') { $field[$Matches[1]] = $Matches[2].Trim() } }
    $script:Id = ($field['id'] -replace '[^0-9a-f]', '')
    $source = $field['source']
    Say "Request $($script:Id): MediaRiffle $($field['version']) from $source"
    if ($field['version'] -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { Set-Status 'failed' "The request didn't name a version."; exit 1 }
    $script:Version = $field['version']
    $base = if ($source -eq 'github') { "https://github.com/TechIdiots-LLC/mediariffle/releases/download/v$($script:Version)" }
            else { "https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/releases/v$($script:Version)/downloads" }

    $installed = Get-ItemProperty $UninstallKey -ErrorAction SilentlyContinue
    if (-not $installed) { Set-Status 'failed' "MediaRiffle isn't installed from its setup here."; exit 1 }
    $script:From = $installed.DisplayVersion
    if ((Compare-Version $script:Version $script:From) -le 0) { Set-Status 'failed' "$($script:Version) isn't newer than the installed $($script:From)."; exit 1 }
    $arch = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $setupName = "MediaRiffle-v$($script:Version)-win-$arch-setup.exe"

    function Get-Release([string]$url, [string]$version, [string]$file) {
        $folder = Join-Path $Work $version
        New-Item -ItemType Directory $folder -Force | Out-Null
        $path = Join-Path $folder $file
        Invoke-WebRequest "$url/$file" -OutFile $path -UseBasicParsing
        $path
    }
    function Test-Sum([string]$sums, [string]$path) {
        $name = Split-Path $path -Leaf
        $want = Get-Content $sums | ForEach-Object { $p = $_ -split '\s+\*?', 2; if ($p.Count -eq 2 -and $p[1] -eq $name) { $p[0] } } | Select-Object -First 1
        $want -and ($want -eq (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant())
    }

    Get-ChildItem $Work -Directory | Remove-Item -Recurse -Force
    Set-Status 'downloading' "Downloading MediaRiffle $($script:Version) ($setupName)."
    try {
        $sums = Get-Release $base $script:Version 'SHA256SUMS'
        $setup = Get-Release $base $script:Version $setupName
    } catch {
        Set-Status 'failed' "Couldn't download $setupName and SHA256SUMS of $($script:Version) from ${base}: $($_.Exception.Message)"; exit 1
    }
    if (-not (Test-Sum $sums $setup)) { Set-Status 'failed' "The downloaded $setupName doesn't match the release's SHA256SUMS: not installed."; exit 1 }
    # A signed install is only replaced by a setup signed by the same publisher
    $exe = Join-Path $installed.InstallLocation 'MediaRiffle.Server.exe'
    $was = if (Test-Path $exe) { Get-AuthenticodeSignature $exe } else { $null }
    if ($was -and $was.Status -eq 'Valid') {
        $now = Get-AuthenticodeSignature $setup
        if ($now.Status -ne 'Valid' -or $now.SignerCertificate.Subject -ne $was.SignerCertificate.Subject) {
            Set-Status 'failed' "$setupName isn't signed by $($was.SignerCertificate.Subject), as the installed MediaRiffle is: not installed."; exit 1
        }
    }

    # The installed version's setup, to go back to if the new one doesn't start: checked against its SHA256SUMS when
    # its release has them (releases before the updater don't)
    $oldBase = $base.Replace("v$($script:Version)", "v$($script:From)")
    $old = $null
    try {
        $old = Get-Release $oldBase $script:From "MediaRiffle-v$($script:From)-win-$arch-setup.exe"
        try {
            $oldSums = Get-Release $oldBase $script:From 'SHA256SUMS'
            if (-not (Test-Sum $oldSums $old)) { Say "The $($script:From) setup doesn't match its SHA256SUMS: no going back to it"; $old = $null }
        } catch { }
    } catch { Say "Couldn't download the $($script:From) setup: no going back to it if $($script:Version) doesn't start" }

    # The database, saved before the new version can change it and put back with the old one (an older version can't
    # read a database a newer one has upgraded). Only in its usual place: the service may edit its settings, so a
    # DataDirectory set there isn't followed with SYSTEM's rights
    $dataDir = Join-Path $env:ProgramData 'MediaRiffle'
    $db = Join-Path $dataDir 'mediariffle.db'
    $settingsFile = Join-Path $dataDir 'appsettings.json'
    $custom = (Test-Path $settingsFile) -and ((Get-Content $settingsFile -Raw) -match '"DataDirectory"\s*:\s*"[^"]+"')
    $saved = Join-Path $Work 'db'
    function Save-Database {
        Remove-Item $saved -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory $saved | Out-Null
        if ($custom) { Say "DataDirectory is set: the database isn't saved before the update"; return }
        if ((Test-Link $dataDir) -or -not (Test-Path $db -PathType Leaf) -or (Test-Link $db)) { Say "No database at $db to save"; return }
        foreach ($f in $db, "$db-wal", "$db-shm") { if (Test-Path $f -PathType Leaf) { Copy-Item $f $saved } }
        Say "Saved $db"
    }
    function Restore-Database {
        if (-not (Test-Path (Join-Path $saved 'mediariffle.db')) -or (Test-Link $dataDir)) { return }
        Stop-Service $Service -Force -ErrorAction SilentlyContinue
        foreach ($f in $db, "$db-wal", "$db-shm") { Remove-Item $f -Force -ErrorAction SilentlyContinue }
        Get-ChildItem $saved -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $dataDir $_.Name) }
        Say "Put back $db as it was before the update"
    }

    function Install-Setup([string]$path) {
        $p = Start-Process -FilePath $path -ArgumentList '/S' -Wait -PassThru
        Say "$(Split-Path $path -Leaf) /S: exit $($p.ExitCode)"
        $p.ExitCode -eq 0
    }
    function Wait-Started([string]$version) {
        for ($i = 0; $i -lt $StartSeconds; $i += 3) {
            $v = if (Test-Path $Running) { (Get-Content $Running -TotalCount 1).Trim() } else { '' }
            $svc = Get-Service $Service -ErrorAction SilentlyContinue
            if ($v -eq $version -and $svc -and $svc.Status -eq 'Running') { return $true }
            Start-Sleep -Seconds 3
        }
        $false
    }
    function Restore([string]$why) {
        if (-not $old) { Set-Status 'failed' "$why The old version's setup couldn't be downloaded to go back to; see $Log."; exit 1 }
        Set-Status 'rolling_back' "$why Putting $($script:From) back."
        Remove-Item $Running -Force -ErrorAction SilentlyContinue
        Restore-Database
        if ((Install-Setup $old) -and (Wait-Started $script:From)) { Set-Status 'rolled_back' "$why $($script:From) was put back; see $Log." }
        else { Set-Status 'failed' "$why Putting $($script:From) back failed too; see $Log." }
        exit 1
    }

    Set-Status 'installing' "Installing MediaRiffle $($script:Version); the server restarts."
    Remove-Item $Running -Force -ErrorAction SilentlyContinue
    # Stopped first, so the database is saved as it was left (the setup starts it again)
    Stop-Service $Service -Force -ErrorAction SilentlyContinue
    Save-Database
    if (-not (Install-Setup $setup)) { Restore "Installing $($script:Version) failed." }
    Set-Status 'checking' "Waiting for MediaRiffle $($script:Version) to start."
    if (-not (Wait-Started $script:Version)) { Restore "MediaRiffle $($script:Version) didn't start within $StartSeconds seconds." }
    Set-Status 'done' "Updated from $($script:From) to $($script:Version)."
    Get-ChildItem $Work -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
} catch {
    Say "Error: $($_.Exception.Message)"
    if ($script:Version) { Set-Status 'failed' "The update stopped: $($_.Exception.Message)" }
    exit 1
} finally {
    $mutex.ReleaseMutex()
}
