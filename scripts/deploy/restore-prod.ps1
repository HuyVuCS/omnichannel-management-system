# Restores a backup made by backup-prod.ps1 into a NEW stack of docker/docker-compose.prod.yml.
# Windows PowerShell 5.1 compatible, ASCII only. docs/runbook.md "Restore".
#
#   powershell -ExecutionPolicy Bypass -File scripts\deploy\restore-prod.ps1 -BackupPath <backup folder> `
#       -ProjectName <name> -EnvFile <environment file of the new stack>
#
# - It refuses a project that has a container or a volume: it never writes over a stack that exists. Restoring in place
#   of the live stack is a procedure of the runbook, decided by the user - not something this script does.
# - The environment file is the new stack's own (scripts\deploy\new-prod-env.ps1, or the file of the lost stack): the
#   init script creates the database roles with its passwords; the backup holds none. The users, the first Admin
#   included, keep the passwords they had at backup time.
# - Order: verify the SHA-256 of both files; start postgres; pg_restore as the superuser over the local socket; unpack
#   the storage archive into the new volume; start the storage; run "migrate"; start everything and wait until healthy.
param(
    [Parameter(Mandatory = $true)][string]$BackupPath,
    [Parameter(Mandatory = $true)][string]$ProjectName,
    [Parameter(Mandatory = $true)][string]$EnvFile
)

. (Join-Path $PSScriptRoot '_stack.ps1')

$helper = $null
try {
    $started = [System.Diagnostics.Stopwatch]::StartNew()
    $BackupPath = (Resolve-Path $BackupPath).Path
    $EnvFile = (Resolve-Path $EnvFile).Path
    if ($ProjectName -notmatch '^[a-z0-9][a-z0-9_-]*$') { throw '-ProjectName must be a compose project name: lower-case letters, digits, - and _.' }

    # 1. The backup is what its manifest says.
    $manifest = Get-Content (Join-Path $BackupPath $script:ManifestName) -Raw | ConvertFrom-Json
    if ($manifest.tool -ne $script:ManifestTool) { throw 'The manifest was not written by backup-prod.ps1.' }
    foreach ($file in @($manifest.files)) {
        $path = Join-Path $BackupPath $file.name
        if (-not (Test-Path $path)) { throw "$($file.name) is missing in the backup." }
        if ((Get-Sha256 $path) -cne [string]$file.sha256) { throw "$($file.name) does not have the SHA-256 of the manifest: the backup is damaged." }
    }
    Write-Host "Backup of $($manifest.createdLocal) (project $($manifest.project), $(@($manifest.migrations).Count) migrations): checksums match."

    # 2. Only into a stack that does not exist.
    $containers = @(Invoke-Docker ps -aq --filter "label=com.docker.compose.project=$ProjectName" | Where-Object { $_ })
    $volumes = @(Invoke-Docker volume ls -q | Where-Object { $_ -like "${ProjectName}_*" })
    if (($containers.Count -gt 0) -or ($volumes.Count -gt 0)) {
        throw "The project '$ProjectName' exists ($($containers.Count) containers, $($volumes.Count) volumes). This script restores only into a stack that does not exist."
    }

    # 3. The database: a new volume, the roles from the init script, then the dump.
    Invoke-Compose $ProjectName $EnvFile up -d --wait postgres
    $postgres = Get-ServiceContainer $ProjectName 'postgres'
    [void](Invoke-Docker cp (Join-Path $BackupPath $script:DumpName) "${postgres}:/tmp/restore.dump")
    try { [void](Invoke-Docker exec $postgres pg_restore -U postgres -d omnichannel --exit-on-error /tmp/restore.dump) }
    finally { [void](Invoke-Docker exec $postgres rm -f /tmp/restore.dump) }
    $restored = @(Get-AppliedMigrations $postgres)
    if ((@($restored) -join ',') -cne (@($manifest.migrations) -join ',')) { throw 'The restored database does not have the migrations of the manifest.' }
    Write-Host "  database restored ($($restored.Count) migrations)"

    # 4. The object storage: its volume is created with the container (not started), filled, then the service starts.
    Invoke-Compose $ProjectName $EnvFile create objectstorage
    $volume = "${ProjectName}_objectstorage-data"
    $helper = "$ProjectName-restore-helper"
    [void](Invoke-Docker create --name $helper --network none --entrypoint tar -v "${volume}:/data" $script:ToolImage -xf /tmp/archive.tar -C /data)
    [void](Invoke-Docker cp (Join-Path $BackupPath $script:ArchiveName) "${helper}:/tmp/archive.tar")
    [void](Invoke-Docker start -a $helper)
    Invoke-Compose $ProjectName $EnvFile up -d --wait objectstorage
    Write-Host '  object storage restored'

    # 5. Migrations of this version of the code that are newer than the backup (normally none), then everything.
    Invoke-Compose $ProjectName $EnvFile --profile migrate run --rm migrate
    $after = @(Get-AppliedMigrations $postgres)
    if ($after.Count -ne $restored.Count) { Write-Host "  NOTE: $($after.Count - $restored.Count) migration(s) newer than the backup were applied." -ForegroundColor Yellow }
    else { Write-Host '  migrate: nothing pending' }
    Invoke-Compose $ProjectName $EnvFile up -d --wait
    $started.Stop()
    Write-Host "Restored into '$ProjectName' and healthy after $([math]::Round($started.Elapsed.TotalSeconds)) s." -ForegroundColor Green
    Write-Host 'Messages that were in RabbitMQ at backup time are not part of a backup: runbook "After a restore".'
    $code = 0
}
catch {
    Write-Host "RESTORE FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Nothing of another stack was touched. What this run created for the new project stays for inspection (runbook "Restore").'
    $code = 1
}
finally {
    if ($helper) { try { [void](Invoke-Docker rm -f $helper) } catch { } }
}
exit $code
