# Shared helpers of the backup, restore and secret-rotation scripts of the production stack. Windows PowerShell 5.1
# compatible, ASCII only. Nothing here reads an environment file or prints a secret.

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

# The tables whose row counts a backup manifest records and a restore compares. A table that does not exist is an error
# of the statement, never a skipped row.
$script:CountedTables = 'Orders', 'OrderItems', 'Inventories', 'InventoryMovements', 'ProcessingTasks', 'ProcessingAttempts',
    'OutboxEvents', 'Products', 'Customers', 'Alerts', 'AspNetUsers'

# Table -> number of rows, in the order of the list above, from one statement.
function Get-TableRowCounts([string]$PostgresId) {
    $sql = (@($script:CountedTables | ForEach-Object { "select '$_', count(*) from `"$_`"" }) -join ' union all ') + ';'
    $counts = [ordered]@{}
    foreach ($row in @(Invoke-StackSql $PostgresId $sql)) {
        $parts = $row.Split('|')
        $counts[$parts[0]] = [long]$parts[1]
    }
    $missing = @($script:CountedTables | Where-Object { -not $counts.Contains($_) })
    if ($missing.Count -gt 0) { throw "No row count for: $($missing -join ', ')" }
    return $counts
}

# A tool-box container that only waits: the storage volume is archived, unpacked and counted in it with "docker exec".
function Start-VolumeHelper([string]$Name, [string]$VolumeMount) {
    [void](Invoke-Docker run -d --name $Name --network none --entrypoint sleep -v $VolumeMount $script:ToolImage 7200)
}

# The number of entries tar lists: of an archive file in the helper, or (without -Archive) of the volume mounted at /data
# archived the way a backup archives it - so both numbers count the same thing.
function Get-TarEntryCount([string]$HelperName, [string]$Archive) {
    $command = if ($Archive) { "tar -tf $Archive | wc -l" } else { 'tar -cf - -C /data . | tar -tf - | wc -l' }
    return [long]((@(Invoke-Docker exec $HelperName sh -c $command) -join '').Trim())
}

# What PostgreSQL stores for a password (SCRAM-SHA-256, RFC 5803 form), computed here: the password itself is never
# sent to the server. Letters and digits only, so no SASLprep step is needed.
function Get-ScramVerifier([string]$Password) {
    if ($Password -cnotmatch '^[A-Za-z0-9]+$') { throw 'The password must be letters and digits only.' }
    $iterations = 4096
    $salt = New-Object byte[] 16
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($salt) } finally { $rng.Dispose() }
    $kdf = [System.Security.Cryptography.Rfc2898DeriveBytes]::new([System.Text.Encoding]::ASCII.GetBytes($Password), $salt, $iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $salted = $kdf.GetBytes(32) } finally { $kdf.Dispose() }
    $hmac = New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (, $salted)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $storedKey = $sha.ComputeHash($hmac.ComputeHash([System.Text.Encoding]::ASCII.GetBytes('Client Key')))
        $serverKey = $hmac.ComputeHash([System.Text.Encoding]::ASCII.GetBytes('Server Key'))
    } finally { $hmac.Dispose(); $sha.Dispose() }
    return ('SCRAM-SHA-256${0}:{1}${2}:{3}' -f $iterations, [Convert]::ToBase64String($salt), [Convert]::ToBase64String($storedKey), [Convert]::ToBase64String($serverKey))
}

# Gives a database role a new password without a prompt: as the superuser over the local socket, with the verifier
# instead of the password, and with statement logging off for that session - a statement that fails leaves neither in
# the PostgreSQL log. Nothing of it is on a command line; the statement file is removed on both sides.
function Set-StackRolePassword([string]$PostgresId, [string]$Role, [string]$Password) {
    if ($Role -cnotmatch '^[a-z_][a-z0-9_]*$') { throw "'$Role' is not a role name." }
    $verifier = Get-ScramVerifier $Password
    $sql = "set log_min_error_statement = panic; set log_min_duration_statement = -1; set log_statement = 'none';`n" +
        "alter role $Role password '$verifier';"
    try { [void](Invoke-StackSql $PostgresId $sql) }
    catch { throw "The password of the role '$Role' was not changed (psql failed; its message is not repeated here)." }
    finally { try { [void](Invoke-Docker exec $PostgresId rm -f /tmp/stack-query.sql) } catch { } }
}

function Get-Sha256([string]$Path) { return (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# docker compose for one project of the production stack. The environment file is read by compose itself.
function Invoke-Compose([string]$ProjectName, [string]$EnvFile) {
    $arguments = $args
    $file = $script:ComposeFile
    [void](Invoke-Native "docker compose $($arguments -join ' ')" { & docker compose -p $ProjectName --env-file $EnvFile -f $file @arguments })
}
