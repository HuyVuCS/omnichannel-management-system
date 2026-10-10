# Backup of the production stack (docker/docker-compose.prod.yml): the PostgreSQL database and the object storage.
# Windows PowerShell 5.1 compatible, ASCII only. docs/runbook.md "Backup".
#
#   powershell -ExecutionPolicy Bypass -File scripts\deploy\backup-prod.ps1 [-Destination <folder>] [-ProjectName omnichannel-prod]
#
# Writes <Destination>\omnichannel-backup-<yyyyMMdd-HHmmss>\ (time of +07:00) with
#   postgres.dump       pg_dump -Fc of the database "omnichannel", taken inside the postgres container as the superuser
#                       over the local socket - no password is used anywhere
#   objectstorage.tar   the storage volume, archived while the storage service is stopped (seconds; uploads and export
#                       files fail or are retried meanwhile), then the service is started again
#   manifest.json       time, commit of the running images, applied migrations, size and SHA-256 of both files
# and appends one line to <Destination>\backup.log. RabbitMQ is not backed up: its messages are transient, the outbox
# is in PostgreSQL (runbook "After a restore").
#
# - It reads no environment file and copies none: the backup holds no password of the stack. It does hold the data -
#   customers, orders, password hashes: keep the destination private.
# - Retention, after a successful backup: every backup of the 7 most recent days that have one and the newest backup
#   of each of the 4 most recent Sundays are kept; older ones are removed. Only folders this script created (its
#   name pattern and its manifest) are ever removed.
# - Exit code 0 only when everything was written and verified; a failed run leaves no backup folder.
param(
    [string]$Destination = (Join-Path $env:USERPROFILE 'OmnichannelBackups'),
    [string]$ProjectName = 'omnichannel-prod'
)

. (Join-Path $PSScriptRoot '_stack.ps1')

$NamePattern = '^omnichannel-backup-\d{8}-\d{6}$'
$KeepDays = 7
$KeepSundays = 4

function Write-Log([string]$Text) {
    $line = '{0} {1} {2}' -f ([DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(7)).ToString('yyyy-MM-dd HH:mm:ss zzz')), $ProjectName, $Text
    Add-Content -Path (Join-Path $Destination 'backup.log') -Value $line -Encoding ASCII
}

# The backups of this project in the destination, newest first: folders with the name pattern whose manifest was written
# by this script.
function Get-Backups {
    $found = foreach ($folder in @(Get-ChildItem -Path $Destination -Directory | Where-Object { $_.Name -match $NamePattern })) {
        $manifestPath = Join-Path $folder.FullName $script:ManifestName
        if (-not (Test-Path $manifestPath)) { continue }
        try { $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json } catch { continue }
        if (($manifest.tool -ne $script:ManifestTool) -or ($manifest.project -ne $ProjectName)) { continue }
        # The time is in the folder name (+07:00), which every PowerShell version reads the same way.
        $created = [datetime]::ParseExact($folder.Name.Substring(19), 'yyyyMMdd-HHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
        [pscustomobject]@{ Path = $folder.FullName; Name = $folder.Name; Created = $created; Day = $created.ToString('yyyy-MM-dd'); Sunday = ($created.DayOfWeek -eq [DayOfWeek]::Sunday) }
    }
    return @($found | Sort-Object Created -Descending)
}

function Remove-OldBackups {
    $backups = Get-Backups
    $days = @($backups | ForEach-Object { $_.Day } | Select-Object -Unique | Select-Object -First $KeepDays)
    $sundays = @($backups | Where-Object { $_.Sunday } | ForEach-Object { $_.Day } | Select-Object -Unique | Select-Object -First $KeepSundays)
    $keep = @{}
    foreach ($backup in $backups) {
        if ($days -contains $backup.Day) { $keep[$backup.Path] = $true }
    }
    foreach ($day in $sundays) { $keep[@($backups | Where-Object { $_.Day -eq $day })[0].Path] = $true }
    $removed = 0
    foreach ($backup in $backups) {
        if (-not $keep.ContainsKey($backup.Path)) {
            Remove-Item -LiteralPath $backup.Path -Recurse -Force
            $removed++
        }
    }
    return $removed
}

$work = $null
$helper = $null
try {
    if (-not (Test-Path $Destination)) { [void](New-Item -ItemType Directory -Path $Destination) }
    $Destination = (Resolve-Path $Destination).Path
    if ($Destination.StartsWith($script:RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'The destination must be outside the repository.' }

    $postgres = Get-ServiceContainer $ProjectName 'postgres'
    $storage = Get-ServiceContainer $ProjectName 'objectstorage'
    $now = [DateTimeOffset]::UtcNow
    $local = $now.ToOffset([TimeSpan]::FromHours(7))
    $name = 'omnichannel-backup-' + $local.ToString('yyyyMMdd-HHmmss')
    $final = Join-Path $Destination $name
    $work = $final + '.partial'
    if ((Test-Path $final) -or (Test-Path $work)) { throw "A backup named $name exists already." }
    [void](New-Item -ItemType Directory -Path $work)
    Write-Host "Backup of $ProjectName -> $final"

    # 1. The database. The dump is written inside the container and copied out: binary data never passes a pipe.
    $migrations = @(Get-AppliedMigrations $postgres)
    [void](Invoke-Docker exec $postgres pg_dump -U postgres -d omnichannel -Fc -f /tmp/backup.dump)
    try { [void](Invoke-Docker cp "${postgres}:/tmp/backup.dump" (Join-Path $work $script:DumpName)) }
    finally { [void](Invoke-Docker exec $postgres rm -f /tmp/backup.dump) }
    Write-Host "  database dumped ($($migrations.Count) migrations applied)"

    # 2. The object storage: a consistent copy needs the service stopped. The archive is written inside a helper
    # container and copied out after the service runs again.
    $volume = "${ProjectName}_objectstorage-data"
    [void](Invoke-Docker volume inspect $volume)
    $helper = "$ProjectName-backup-helper"
    try { [void](Invoke-Docker rm -f $helper) } catch { }
    $stopped = [System.Diagnostics.Stopwatch]::StartNew()
    [void](Invoke-Docker stop $storage)
    try {
        [void](Invoke-Docker run --name $helper --network none --entrypoint tar -v "${volume}:/data:ro" $script:ToolImage -cf /tmp/archive.tar -C /data .)
    } finally {
        [void](Invoke-Docker start $storage)
        Wait-Healthy $storage 120
        $stopped.Stop()
    }
    $downtime = [math]::Round($stopped.Elapsed.TotalSeconds, 1)
    [void](Invoke-Docker cp "${helper}:/tmp/archive.tar" (Join-Path $work $script:ArchiveName))
    Write-Host "  object storage archived (the storage service was down for $downtime s)"

    # 3. The manifest.
    $revisions = @{}
    foreach ($service in 'api', 'workers') {
        $ids = @(Get-ServiceContainers $ProjectName $service)
        $labels = @($ids | ForEach-Object { [string](Get-ContainerInfo $_).Config.Labels.'org.opencontainers.image.revision' } | Select-Object -Unique)
        $revisions[$service] = if ($labels.Count -eq 1 -and $labels[0]) { $labels[0] } else { 'unknown' }
    }
    $files = foreach ($file in $script:DumpName, $script:ArchiveName) {
        $item = Get-Item (Join-Path $work $file)
        if ($item.Length -le 0) { throw "$file is empty." }
        [ordered]@{ name = $file; sizeBytes = $item.Length; sha256 = (Get-Sha256 $item.FullName) }
    }
    $manifest = [ordered]@{
        tool                         = $script:ManifestTool
        project                      = $ProjectName
        createdUtc                   = $now.ToString('yyyy-MM-ddTHH:mm:ssZ')
        createdLocal                 = $local.ToString('yyyy-MM-ddTHH:mm:sszzz')
        gitCommit                    = [ordered]@{ api = $revisions['api']; workers = $revisions['workers'] }
        migrations                   = @($migrations)
        files                        = @($files)
        storageDowntimeSeconds       = $downtime
    }
    [System.IO.File]::WriteAllText((Join-Path $work $script:ManifestName), ($manifest | ConvertTo-Json -Depth 5), (New-Object System.Text.ASCIIEncoding))

    Rename-Item -LiteralPath $work -NewName $name
    $work = $null
    $removed = Remove-OldBackups
    $size = [math]::Round((($files | ForEach-Object { $_.sizeBytes } | Measure-Object -Sum).Sum / 1MB), 1)
    Write-Log "OK $name ${size}MB storage-down ${downtime}s removed-old $removed"
    Write-Host "Done: $final ($size MB; $removed old backups removed)." -ForegroundColor Green
    $code = 0
}
catch {
    $message = "$($_.Exception.Message)"
    Write-Host "BACKUP FAILED: $message" -ForegroundColor Red
    if ($work -and (Test-Path $work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    try { Write-Log "FAILED $message" } catch { }
    $code = 1
}
finally {
    if ($helper) { try { [void](Invoke-Docker rm -f $helper) } catch { } }
}
exit $code
