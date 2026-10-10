# Shared helpers of the backup and restore scripts of the production stack. Windows PowerShell 5.1 compatible, ASCII only.
# Nothing here reads or prints a secret.

$ErrorActionPreference = 'Stop'
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:ComposeFile = Join-Path $script:RepoRoot 'docker\docker-compose.prod.yml'
# An image every stack already has, used as a plain tool box (tar) for the storage volume.
$script:ToolImage = 'postgres:17'
$script:DumpName = 'postgres.dump'
$script:ArchiveName = 'objectstorage.tar'
$script:ManifestName = 'manifest.json'
$script:ManifestTool = 'backup-prod.ps1'

# Runs a native command and returns its standard output; a non-zero exit code is an error that carries the end of the
# command's own messages. Docker writes progress to standard error: under ErrorActionPreference Stop Windows PowerShell
# 5.1 would turn that into a terminating error.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $all = @(& $Command 2>&1) } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) {
        $messages = @($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" } | Select-Object -Last 5) -join ' | '
        throw "$What failed (exit code $LASTEXITCODE): $messages"
    }
    return @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
}

function Invoke-Docker {
    $arguments = $args
    return Invoke-Native "docker $($arguments[0]) $($arguments[1])" { & docker @arguments }
}

# The ids of the containers of one service of a compose project (running or not).
function Get-ServiceContainers([string]$ProjectName, [string]$Service) {
    return @(Invoke-Docker ps -aq --filter "label=com.docker.compose.project=$ProjectName" --filter "label=com.docker.compose.service=$Service" | Where-Object { $_ })
}

function Get-ServiceContainer([string]$ProjectName, [string]$Service) {
    $ids = @(Get-ServiceContainers $ProjectName $Service)
    if ($ids.Count -ne 1) { throw "Project '$ProjectName' has $($ids.Count) containers of the service '$Service' (expected 1). Is the stack up?" }
    return [string]$ids[0]
}

function Get-ContainerInfo([string]$Id) { return ((Invoke-Docker inspect $Id) -join "`n" | ConvertFrom-Json)[0] }

function Wait-Healthy([string]$Id, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $state = (Get-ContainerInfo $Id).State
        if ($state.Running -and $state.Health -and ($state.Health.Status -eq 'healthy')) { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "The container $Id is not healthy after $Seconds s."
}

# Runs SQL in the database of the stack as the superuser over the local socket of the container (no password anywhere)
# and returns the rows as text (columns separated by '|'). The statement travels as a file, so its quotes survive every
# shell on the way.
function Invoke-StackSql([string]$PostgresId, [string]$Sql) {
    $file = Join-Path ([System.IO.Path]::GetTempPath()) ('omnichannel-sql-' + [guid]::NewGuid().ToString('N') + '.sql')
    [System.IO.File]::WriteAllText($file, $Sql, (New-Object System.Text.UTF8Encoding($false)))
    try {
        [void](Invoke-Docker cp $file "${PostgresId}:/tmp/stack-query.sql")
        return @(Invoke-Docker exec $PostgresId psql -U postgres -d omnichannel -v ON_ERROR_STOP=1 -At -f /tmp/stack-query.sql | Where-Object { $_ })
    } finally { Remove-Item $file -Force -ErrorAction SilentlyContinue }
}

function Get-AppliedMigrations([string]$PostgresId) {
    return @(Invoke-StackSql $PostgresId 'select "MigrationId" from "__EFMigrationsHistory" order by 1;')
}

function Get-Sha256([string]$Path) { return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# docker compose for one project of the production stack. The environment file is read by compose itself.
function Invoke-Compose([string]$ProjectName, [string]$EnvFile) {
    $arguments = $args
    $file = $script:ComposeFile
    [void](Invoke-Native "docker compose $($arguments -join ' ')" { & docker compose -p $ProjectName --env-file $EnvFile -f $file @arguments })
}
