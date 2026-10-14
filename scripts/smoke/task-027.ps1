# Acceptance of doc section 16 (task 027 Part B): one script that walks the fifteen acceptance criteria against a running
# stack and prints, besides the step table, a table "criterion -> steps -> result".
# Windows PowerShell 5.1 and PowerShell 7 (Linux too: the CI job "acceptance"). ASCII only.
#
#   -Mode Full      a THROWAWAY stack (also CI): every block, with the drills - load scenarios of scripts/load/run.ps1
#                   (duplicates, contention), the DLQ drill (SIM-DOWN- external SKU), a signed webhook, read-only psql /
#                   rabbitmqctl in the stack's containers and the invariants of scripts/load/invariants.sql at the end.
#                   Needs -Project (compose project) and -EnvFile (its environment file: first Admin, webhook secret); the
#                   stack must run docker/docker-compose.acceptance.yml (SKU rules on, short retry delays). Refuses the
#                   projects omnichannel-prod and omnichannel and any public origin.
#   -Mode NoDrills  development or (with -Live) the live system: no load scenario, no container access, no DLQ drill; the
#                   retry case (SIM-TEMP-) only when the simulator's SKU rules are on (detected; otherwise reported as
#                   "skipped: SKU rules off", never as passed).
#   -Live           required for any write to a public origin (not this machine). NoDrills only.
#
# The target: SMOKE_BASE_URL (+ SMOKE_ADMIN_EMAIL / SMOKE_ADMIN_PASSWORD or SMOKE_ENV_FILE) as in _common.ps1; without
# SMOKE_BASE_URL the local development API (and Workers host) is used or started. With -EnvFile the admin is read from it.
# Data it leaves: users, products, customers, orders, one image and one export named ACC027-{n}-...; the smoke mappings
# are deactivated and the smoke users locked at the end, also after a failed step. It never prints a token, a password or
# a presigned link.
#
#   powershell -ExecutionPolicy Bypass -File scripts\smoke\task-027.ps1 -Mode NoDrills
#   pwsh scripts/smoke/task-027.ps1 -Mode Full -Project omni027 -EnvFile docker/.env.prod.027
param(
    [ValidateSet('Full', 'NoDrills')][string]$Mode = 'NoDrills',
    [string]$Project,
    [string]$EnvFile,
    [switch]$Live
)

. (Join-Path $PSScriptRoot '_common.ps1')

$Full = ($Mode -eq 'Full')
$PublicOrigin = -not ([uri]$script:BaseUrl).IsLoopback
if ($Full) {
    if ((-not $Project) -or (-not $EnvFile)) { throw '-Mode Full needs -Project (the compose project of a throwaway stack) and -EnvFile (its environment file).' }
    if (($Project -eq 'omnichannel-prod') -or ($Project -eq 'omnichannel')) { throw '-Mode Full never runs against production or development: give the compose project of a throwaway stack.' }
    if ($PublicOrigin -or $Live) { throw '-Mode Full runs against a throwaway stack on this machine only (no public origin, no -Live).' }
    if (-not (Test-Path -LiteralPath $EnvFile)) { throw "Environment file '$EnvFile' does not exist." }
    $EnvFile = (Resolve-Path -LiteralPath $EnvFile).Path
    if ((-not $env:SMOKE_ADMIN_EMAIL) -and (-not $env:SMOKE_ENV_FILE)) { $env:SMOKE_ENV_FILE = $EnvFile }
}
if ($PublicOrigin -and (-not $Live)) { throw "$script:BaseUrl is a public origin: this script writes ACC027- data; give -Live to do that on purpose." }

$TaskWaitSeconds = 90
# The breaker of a channel opens after 5 failures within 30 s and stays open 30 s (StockSyncWorker:CircuitBreaker). The
# short retry delays of the acceptance stack put the five attempts of the DLQ drill inside one sampling window, so the
# drill waits the break out before the channel is used again (in production the attempts are minutes apart).
$BreakerSeconds = 31
$Channels = @('website', 'pos', 'digital-platform')

# ---- criteria and steps --------------------------------------------------------------------------------------------
$Criteria = [ordered]@{
    1  = 'Orders from Website / POS / Digital Platform'
    2  = 'Orders normalised and put on RabbitMQ'
    3  = 'Duplicate orders not processed twice'
    4  = 'Concurrent orders do not oversell'
    5  = 'Total / Reserved / Available stock correct'
    6  = 'Movement + outbox event in one transaction'
    7  = 'Stock sync with retry and DLQ'
    8  = 'Near-real-time dashboard'
    9  = 'Trace order -> task, worker, instance, attempt, error'
    10 = 'Low-stock alert by threshold'
    11 = 'Inventory and processing history'
    12 = 'Excel export'
    13 = 'Product images in S3-compatible storage'
    14 = 'API authentication / authorization'
    15 = 'Tests for the important flows'
}
$script:StepNo = 0
$script:Steps = New-Object System.Collections.Generic.List[object]

function Check([int[]]$Criterion, [string]$Name, $Expected, $Actual) {
    $script:StepNo++
    $before = $script:Results.Count
    Assert-Step ('{0:00} {1}' -f $script:StepNo, $Name) $Expected $Actual
    $script:Steps.Add([pscustomobject]@{ No = $script:StepNo; Criteria = $Criterion; OK = $script:Results[$before].OK; Skipped = $false })
}

function Skip([int[]]$Criterion, [string]$Name, [string]$Reason) {
    $script:StepNo++
    Assert-Step ('{0:00} {1}' -f $script:StepNo, $Name) "skipped: $Reason" "skipped: $Reason"
    $script:Steps.Add([pscustomobject]@{ No = $script:StepNo; Criteria = $Criterion; OK = $true; Skipped = $true })
}

function Fail-Block([int[]]$Criterion, [string]$Block, $ErrorRecord) {
    Check $Criterion "unexpected error in $($Block): $($ErrorRecord.Exception.Message)" 'no error' 'error'
}

# "01-04, 07" from step numbers.
function Format-Ranges([int[]]$Numbers) {
    $parts = @()
    $sorted = @($Numbers | Sort-Object -Unique)
    $i = 0
    while ($i -lt $sorted.Count) {
        $j = $i
        while (($j + 1 -lt $sorted.Count) -and ($sorted[$j + 1] -eq $sorted[$j] + 1)) { $j++ }
        $parts += if ($j -gt $i) { '{0:00}-{1:00}' -f $sorted[$i], $sorted[$j] } else { '{0:00}' -f $sorted[$i] }
        $i = $j + 1
    }
    return ($parts -join ', ')
}

function Write-CriteriaTable {
    # By entry: an ordered dictionary indexed with an integer answers by position, not by key.
    $rows = foreach ($entry in $Criteria.GetEnumerator()) {
        $key = [int]$entry.Key
        $own = @($script:Steps | Where-Object { $_.Criteria -contains $key })
        $result = if ($key -eq 15) { 'see docs/acceptance.md (test suites)' }
        elseif ($own.Count -eq 0) { 'NO STEP' }
        elseif (@($own | Where-Object { -not $_.OK }).Count -gt 0) { 'FAIL' }
        elseif (@($own | Where-Object { -not $_.Skipped }).Count -eq 0) { 'SKIPPED' }
        else {
            $skipped = @($own | Where-Object { $_.Skipped }).Count
            if ($skipped -gt 0) { "PASS ($skipped skipped)" } else { 'PASS' }
        }
        [pscustomobject]@{ Criterion = ('{0,2} {1}' -f $key, $entry.Value); Steps = (Format-Ranges @($own | ForEach-Object { $_.No })); Result = $result }
    }
    Write-Host ''
    Write-Host "Acceptance criteria (doc section 16), mode $Mode"
    $columns = @(
        @{ Label = 'Criterion'; Expression = { $_.Criterion }; Width = 56 },
        @{ Label = 'Steps'; Expression = { $_.Steps }; Width = 40 },
        @{ Label = 'Result'; Expression = { $_.Result }; Width = 38 }
    )
    $rows | Format-Table -Property $columns -Wrap | Out-String -Width 160 | Write-Host
}

# ---- helpers --------------------------------------------------------------------------------------------------------

# One request without following a redirect and without throwing on an error status (Status 0 = no HTTP answer).
function Invoke-Raw([string]$Method, [string]$Url, [hashtable]$Headers, [byte[]]$Body, [string]$ContentType) {
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Method = $Method
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    if ($Headers) { foreach ($name in $Headers.Keys) { $request.Headers.Add($name, [string]$Headers[$name]) } }
    if ($null -ne $Body) {
        $request.ContentType = $ContentType
        $request.ContentLength = $Body.Length
        $stream = $request.GetRequestStream()
        $stream.Write($Body, 0, $Body.Length)
        $stream.Close()
    }
    $response = $null
    try { $response = $request.GetResponse() }
    catch [System.Net.WebException] { $response = $_.Exception.Response }
    if ($null -eq $response) { return [pscustomobject]@{ Status = 0; Headers = @{}; Bytes = [byte[]]@() } }
    try {
        $headers = @{}
        foreach ($name in $response.Headers.AllKeys) { $headers[$name.ToLowerInvariant()] = [string]$response.Headers[$name] }
        $buffer = New-Object System.IO.MemoryStream
        $response.GetResponseStream().CopyTo($buffer)
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $headers; Bytes = $buffer.ToArray() }
    } finally { $response.Close() }
}

# A time of an API answer as a UTC DateTimeOffset. Windows PowerShell keeps the ISO text; PowerShell 7 turns it into a
# DateTime of its own (local kind).
function ConvertTo-Utc($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTime]) { return ([DateTimeOffset]$Value).ToUniversalTime() }
    return [DateTimeOffset]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}

# Asks for a processing task once per second until it is Succeeded or Failed; returns the last answer (or $null).
function Wait-Task([string]$TaskId, [string]$Token, [int]$Seconds = $TaskWaitSeconds) {
    if (-not $TaskId) { return $null }
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $task = (Invoke-Api GET "/api/processing/tasks/$TaskId" $Token).Body
        if ($task -and (($task.status -ceq 'Succeeded') -or ($task.status -ceq 'Failed'))) { return $task }
        Start-Sleep -Seconds 1
    }
    return $null
}

function Get-TaskStatus($Task) { if ($Task) { return [string]$Task.status } else { return 'not finished' } }

# The stock-sync tasks of one channel and correlation id, once at least $Expected of them are finished.
function Wait-SyncTasks([string]$ChannelId, [string]$CorrelationId, [string]$Token, [int]$Expected, [int]$Seconds = $TaskWaitSeconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $items = @()
    while ((Get-Date) -lt $deadline) {
        $items = @((Invoke-Api GET "/api/processing/tasks?taskType=StockSync&channelId=$ChannelId&correlationId=$([uri]::EscapeDataString($CorrelationId))&pageSize=100" $Token).Body.items)
        $open = @($items | Where-Object { ($_.status -cne 'Succeeded') -and ($_.status -cne 'Failed') }).Count
        if (($items.Count -ge $Expected) -and ($open -eq 0)) { return $items }
        Start-Sleep -Seconds 1
    }
    return $items
}

# Submits an order through the intake; returns the answer.
function Submit-Order([string]$Slug, [string]$Token, [string]$ExternalId, [string]$CustomerId, [string]$ProductId, [int]$Quantity, [string]$CorrelationId) {
    $headers = if ($CorrelationId) { @{ 'X-Correlation-Id' = $CorrelationId } } else { $null }
    return Invoke-Api POST "/api/orders/$Slug" $Token @{ externalOrderId = $ExternalId; customerId = $CustomerId; items = @(@{ productId = $ProductId; stock = $Quantity }) } $headers
}

function Get-Product([string]$ProductId, [string]$Token) { return (Invoke-Api GET "/api/products/$ProductId" $Token).Body }

function Get-Figures($Product) { if ($Product) { return "$($Product.totalStock)|$($Product.reservedStock)|$($Product.availableStock)" } else { return 'no product' } }

function New-Product([string]$Token, [string]$Sku, [int]$Threshold, [int]$Stock) {
    $product = Invoke-Api POST '/api/products' $Token @{ sku = $Sku; name = "Acceptance $Sku"; price = 25000; lowStockThreshold = $Threshold }
    if ($product.Status -ne 201) { throw "creating product $Sku answered HTTP $($product.Status)" }
    if ($Stock -gt 0) {
        $import = Invoke-Api POST '/api/inventory/import' $Token @{ documentNumber = "$Sku-GRN"; items = @(@{ productId = [string]$product.Body.productId; stock = $Stock }) }
        if ($import.Status -ne 200) { throw "importing $Stock units of $Sku answered HTTP $($import.Status)" }
    }
    return [string]$product.Body.productId
}

# Alerts of a product and type, newest first.
function Get-ProductAlerts([string]$Token, [string]$ProductId, [string]$Type) {
    $r = Invoke-Api GET "/api/alerts?productId=$ProductId&type=$Type&status=Open&status=Acknowledged&status=Resolved&pageSize=100" $Token
    return @(@($r.Body.items) | Sort-Object { ConvertTo-Utc $_.lastOccurredAt } -Descending)
}

function Get-NewestAlert([string]$Token, [string]$ProductId, [string]$Type) {
    $alerts = @(Get-ProductAlerts $Token $ProductId $Type)
    if ($alerts.Count -ge 1) { return $alerts[0] } else { return $null }
}

# ---- container access (Full only; read-only) -------------------------------------------------------------------------
function Invoke-Docker([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $lines = @(& docker @Arguments 2>$null) } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "docker $($Arguments[0]) failed (exit code $LASTEXITCODE)." }
    return $lines
}

function Get-ServiceContainer([string]$Service) {
    $ids = @(Invoke-Docker @('ps', '-q', '--filter', "label=com.docker.compose.project=$Project", '--filter', "label=com.docker.compose.service=$Service") | Where-Object { $_ })
    if ($ids.Count -lt 1) { throw "No running '$Service' container in the compose project $Project." }
    return [string]$ids[0]
}

# A read-only statement in the stack's PostgreSQL container; rows as "a|b|c" text. The statement goes in a file (no
# quoting through the shells).
function Invoke-Sql([string]$Sql) {
    $file = Join-Path ([System.IO.Path]::GetTempPath()) ('omni-acc-' + [guid]::NewGuid().ToString('N') + '.sql')
    [System.IO.File]::WriteAllText($file, $Sql, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $postgres = Get-ServiceContainer 'postgres'
        [void](Invoke-Docker @('cp', $file, "$($postgres):/tmp/omni-acc.sql"))
        return , @(Invoke-Docker @('exec', $postgres, 'psql', '-h', '127.0.0.1', '-U', 'postgres', '-d', 'omnichannel', '-v', 'ON_ERROR_STOP=1', '-At', '-f', '/tmp/omni-acc.sql'))
    } finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

# Messages in one queue of the stack's broker (rabbitmqctl, read-only); -1 when the queue does not exist.
function Get-QueueMessages([string]$Queue) {
    $rabbit = Get-ServiceContainer 'rabbitmq'
    foreach ($line in (Invoke-Docker @('exec', $rabbit, 'rabbitmqctl', '-q', 'list_queues', 'name', 'messages'))) {
        $parts = @(([string]$line).Trim() -split '\s+')
        if (($parts.Count -eq 2) -and ($parts[0] -ceq $Queue)) { return [int]$parts[1] }
    }
    return -1
}

# ---- realtime hub -------------------------------------------------------------------------------------------------
# Opens a WebSocket to the notifications hub (JSON protocol) for a token; $null when it could not.
function Open-Hub([string]$Token) {
    $negotiate = Invoke-Api POST '/api/hubs/notifications/negotiate?negotiateVersion=1' $Token
    if ($negotiate.Status -ne 200) { return $null }
    $uri = New-Object System.UriBuilder("$script:BaseUrl/api/hubs/notifications")
    $uri.Scheme = if ($uri.Scheme -eq 'https') { 'wss' } else { 'ws' }
    $uri.Query = "id=$([uri]::EscapeDataString([string]$negotiate.Body.connectionToken))&access_token=$([uri]::EscapeDataString($Token))"
    $socket = New-Object System.Net.WebSockets.ClientWebSocket
    if (-not $socket.ConnectAsync($uri.Uri, [System.Threading.CancellationToken]::None).Wait(15000)) { $socket.Dispose(); return $null }
    $handshake = [System.Text.Encoding]::UTF8.GetBytes('{"protocol":"json","version":1}' + [char]0x1E)
    [void]$socket.SendAsync((New-Object System.ArraySegment[byte] -ArgumentList @(, $handshake)), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).Wait(5000)
    $script:HubText = ''
    $script:HubSeen = @{}
    return $socket
}

# Reads the hub until every pattern of $Patterns (regular expressions over the received text) was seen, or $Seconds are
# over. Records the local UTC time at which each pattern was first seen in $script:HubSeen.
function Watch-Hub($Socket, [string[]]$Patterns, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $buffer = New-Object byte[] 16384
    while ((Get-Date) -lt $deadline) {
        foreach ($pattern in $Patterns) {
            if ((-not $script:HubSeen.ContainsKey($pattern)) -and ($script:HubText -match $pattern)) { $script:HubSeen[$pattern] = [DateTimeOffset]::UtcNow }
        }
        if (@($Patterns | Where-Object { -not $script:HubSeen.ContainsKey($_) }).Count -eq 0) { return $true }
        $source = New-Object System.Threading.CancellationTokenSource
        $source.CancelAfter(1000)
        try {
            $receive = $Socket.ReceiveAsync((New-Object System.ArraySegment[byte] -ArgumentList @(, $buffer)), $source.Token)
            $receive.Wait()
            $script:HubText += [System.Text.Encoding]::UTF8.GetString($buffer, 0, $receive.Result.Count)
        } catch {
            if ($Socket.State -ne [System.Net.WebSockets.WebSocketState]::Open) { return $false }
        } finally { $source.Dispose() }
    }
    return $false
}

# ---- an xlsx file without Office: the text of its worksheets and shared strings ---------------------------------------
function Get-XlsxText([byte[]]$Bytes) {
    Add-Type -AssemblyName System.IO.Compression
    $archive = New-Object System.IO.Compression.ZipArchive((New-Object System.IO.MemoryStream(, $Bytes)), [System.IO.Compression.ZipArchiveMode]::Read)
    try {
        $text = ''
        foreach ($entry in $archive.Entries) {
            if (($entry.FullName -like 'xl/worksheets/*.xml') -or ($entry.FullName -eq 'xl/sharedStrings.xml')) {
                $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8)
                try { $text += $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        }
        return $text
    } finally { $archive.Dispose() }
}

function Test-StartsWithPk([byte[]]$Bytes) { return (($Bytes.Length -gt 2) -and ($Bytes[0] -eq 0x50) -and ($Bytes[1] -eq 0x4B)) }

function Test-SameBytes([byte[]]$A, [byte[]]$B) {
    if ($A.Length -ne $B.Length) { return $false }
    for ($i = 0; $i -lt $A.Length; $i++) { if ($A[$i] -ne $B[$i]) { return $false } }
    return $true
}

# A token with one character of its signature changed, and one whose payload claims another role under the old signature.
function Get-ForgedTokens([string]$Token) {
    $parts = $Token.Split('.')
    $signature = $parts[2]
    $last = $signature.Substring($signature.Length - 2, 1)
    $other = if ($last -ceq 'A') { 'B' } else { 'A' }
    $badSignature = "$($parts[0]).$($parts[1])." + $signature.Substring(0, $signature.Length - 2) + $other + $signature.Substring($signature.Length - 1)
    $payload = $parts[1].Replace('-', '+').Replace('_', '/')
    while ($payload.Length % 4) { $payload += '=' }
    $json = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($payload))
    $elevated = $json -replace '"(Staff|Manager)"', '"Admin"'
    $encoded = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($elevated)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    return @{ Signature = $badSignature; Payload = "$($parts[0]).$encoded.$signature"; PayloadChanged = ($elevated -cne $json) }
}

# ---- the run --------------------------------------------------------------------------------------------------------
$png = [System.Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==')
$n = Get-Random -Minimum 10000 -Maximum 99999
$prefix = "ACC027-$n"
$admin = $null; $manager = $null; $staff = $null
$managerUser = $null; $staffUser = $null
$mappings = New-Object System.Collections.Generic.List[object]
$channelIds = @{}
$matrix = [ordered]@{}
$skuRules = $null
$socket = $null

try {
    Start-Smoke "Task 027 - acceptance of doc section 16 ($Mode$(if ($Live) { ', live' }))"
    if (-not $script:ForeignEnvironment) { Start-SmokeWorkers }

    # ---- B0. Accounts and channels ------------------------------------------------------------------------------------
    try {
        $cred = Get-AdminCredential
        $admin = Get-Token $cred.Email $cred.Password
        $cred = $null
        $password = New-TestPassword
        $managerUser = Invoke-Api POST '/api/users' $admin @{ email = "$($prefix.ToLowerInvariant())-manager@example.com"; password = $password; role = 'Manager' }
        $manager = if ($managerUser.Status -eq 201) { Get-Token "$($prefix.ToLowerInvariant())-manager@example.com" $password } else { $null }
        $password = New-TestPassword
        $staffUser = Invoke-Api POST '/api/users' $admin @{ email = "$($prefix.ToLowerInvariant())-staff@example.com"; password = $password; role = 'Staff' }
        $staff = if ($staffUser.Status -eq 201) { Get-Token "$($prefix.ToLowerInvariant())-staff@example.com" $password } else { $null }
        $password = $null
        $matrix['Admin: create a user (POST /api/users)'] = "$($managerUser.Status)"
        Check @(14) 'Admin creates a Manager and a Staff account; both log in' '201 201 True True' "$($managerUser.Status) $($staffUser.Status) $([bool]$manager) $([bool]$staff)"
        foreach ($channel in @((Invoke-Api GET '/api/channels' $staff).Body)) { $channelIds[[string]$channel.slug] = [string]$channel.channelId }
        Check @(1) 'the three channels exist (website, pos, digital-platform)' 3 @($Channels | Where-Object { $channelIds[$_] }).Count
        $customer = Invoke-Api POST '/api/customers' $staff @{ name = "$prefix customer"; phone = $null; email = "$($prefix.ToLowerInvariant())@example.com"; address = $null }
        $customerId = [string]$customer.Body.customerId
        Check @(1) 'Staff creates the customer of the orders' 201 $customer.Status
    } catch { Fail-Block @(1, 14) 'B0' $_ }
    if ((-not $admin) -or (-not $manager) -or (-not $staff)) { throw 'No accounts: the remaining blocks need them.' }

    # ---- B1. Orders from the three channels; normalised; on RabbitMQ (criteria 1, 2) ---------------------------------
    $p1 = $null; $b1Orders = @{}; $b1Tasks = @{}
    try {
        $p1 = New-Product $manager "$prefix-P1" 0 20
        $before = Get-Product $p1 $staff
        $outboxBefore = [int](Invoke-Api GET '/api/dashboard/summary' $staff).Body.outbox.pending
        foreach ($slug in $Channels) {
            $submitted = Submit-Order $slug $staff "$prefix-B1-$slug" $customerId $p1 1
            $matrix["Staff: order intake (POST /api/orders/$slug)"] = "$($submitted.Status)"
            Check @(1) "Staff submits an order on $slug -> 202, not a duplicate" '202 False' "$($submitted.Status) $($submitted.Body.duplicate)"
            $task = Wait-Task ([string]$submitted.Body.taskId) $staff
            $b1Tasks[$slug] = $task
            Check @(1, 2) "... its task Succeeded, the order Reserved" 'Succeeded Reserved' "$(Get-TaskStatus $task) $(if ($task) { [string](Invoke-Api GET "/api/orders/$($task.orderId)" $staff).Body.status })"
            if ($task) { $b1Orders[$slug] = [string]$task.orderId }
            $attempt = if ($task -and @($task.attempts).Count -ge 1) { @($task.attempts)[0] } else { $null }
            Check @(2, 9) '... the task names its message; one attempt by the OrderWorker with its instance id' 'True 1 OrderWorker True' "$([bool]($task -and $task.messageId)) $(if ($task) { @($task.attempts).Count }) $(if ($attempt) { $attempt.workerName }) $([bool]($attempt -and $attempt.workerInstanceId))"
            Check @(2) '... its message (order.created) left the outbox: Published, with PublishedAt' 'order.created Published True' "$(if ($task -and $task.message) { "$($task.message.eventType) $($task.message.status) $([bool]$task.message.publishedAt)" } else { 'no message' })"
            $order = if ($b1Orders[$slug]) { (Invoke-Api GET "/api/orders/$($b1Orders[$slug])" $staff).Body } else { $null }
            $line = if ($order -and @($order.items).Count -eq 1) { "$(@($order.items)[0].productId) x $(@($order.items)[0].stock) @ $([int]@($order.items)[0].unitPrice)" } else { 'no single line' }
            Check @(2) '... normalised: channel, customer, one line by product with the server price' "$slug $customerId $p1 x 1 @ 25000" "$(if ($order) { "$($order.channel.slug) $($order.customer.customerId)" }) $line"
            if ($Full -and $task -and $task.message) {
                $row = @(Invoke-Sql "select `"Status`", `"PublishedAt`" is not null from `"OutboxEvents`" where `"OutboxEventId`" = '$($task.message.outboxEventId)';")
                Check @(2) '... psql: its outbox row is Published with PublishedAt' 'Published|t' $(if ($row.Count -ge 1) { [string]$row[0] } else { 'no row' })
            }
        }
        if (-not $Full) {
            $deadline = (Get-Date).AddSeconds(30)
            do { $pending = [int](Invoke-Api GET '/api/dashboard/summary' $staff).Body.outbox.pending; if ($pending -gt $outboxBefore) { Start-Sleep -Seconds 1 } } while (($pending -gt $outboxBefore) -and ((Get-Date) -lt $deadline))
            Check @(2) 'the dashboard''s pending outbox count is back to its value before the orders' $true ($pending -le $outboxBefore)
        }

        if ($Full) {
            # A signed webhook on website: the channel's own shape, normalised through the mapping by external product id.
            $mapping = Invoke-ApiProblem POST "/api/channels/$($channelIds['website'])/products" $manager @{ productId = $p1; externalProductId = "$prefix-WEB-P1" } 'en'
            if ($mapping.Status -eq 201) { $mappings.Add([pscustomobject]@{ Channel = $channelIds['website']; Body = $mapping.Body }) }
            [void](Wait-Task ([string]$mapping.Body.syncTaskId) $staff)
            $json = '{"externalOrderId":"' + "$prefix-HOOK" + '","customer":{"externalCustomerId":"' + "$prefix-C" + '","name":"' + "$prefix webhook customer" + '","phone":"0901 234 567"},"items":[{"externalProductId":"' + "$prefix-WEB-P1" + '","quantity":2}]}'
            $bodyFile = Join-Path ([System.IO.Path]::GetTempPath()) "omni-acc-$n-hook.json"
            [System.IO.File]::WriteAllText($bodyFile, $json, (New-Object System.Text.UTF8Encoding($false)))
            try {
                $sent = & (Join-Path $script:RepoRoot 'scripts/send-webhook.ps1') -Channel website -BodyFile $bodyFile -BaseUrl $script:BaseUrl -EnvFile $EnvFile -PassThru
            } finally { Remove-Item $bodyFile -ErrorAction SilentlyContinue }
            Check @(1, 2) 'a signed webhook (website) -> 202' 202 $sent.Status
            $hookTask = Wait-Task ([string]$sent.Body.taskId) $manager
            $hookOrder = if ($hookTask -and $hookTask.orderId) { (Invoke-Api GET "/api/orders/$($hookTask.orderId)" $manager).Body } else { $null }
            $hookLine = if ($hookOrder -and @($hookOrder.items).Count -eq 1) { "$(@($hookOrder.items)[0].productId) x $(@($hookOrder.items)[0].stock)" } else { 'no single line' }
            Check @(2) '... the same normalised order: channel, customer by name, the mapped product' "Reserved website $prefix webhook customer $p1 x 2" "$(if ($hookOrder) { "$($hookOrder.status) $($hookOrder.channel.slug) $($hookOrder.customer.name)" }) $hookLine"
        } else {
            Skip @(1, 2) 'a signed webhook (website)' 'Full only (needs the stack''s webhook secret)'
        }
    } catch { Fail-Block @(1, 2) 'B1' $_ }

    # ---- B2. Duplicates (criterion 3) --------------------------------------------------------------------------------
    try {
        $again = Submit-Order 'website' $staff "$prefix-B1-website" $customerId $p1 1
        Check @(3) 'the same external order id again on website -> 202, duplicate, the same task' "202 True $(if ($b1Tasks['website']) { $b1Tasks['website'].taskId })" "$($again.Status) $($again.Body.duplicate) $($again.Body.taskId)"
        $other = Submit-Order 'pos' $staff "$prefix-B1-website" $customerId $p1 1
        $otherTask = Wait-Task ([string]$other.Body.taskId) $staff
        Check @(3) '... the same id on pos is another order -> 202, not a duplicate, Reserved' '202 False Succeeded' "$($other.Status) $($other.Body.duplicate) $(Get-TaskStatus $otherTask)"
        $listed = (Invoke-Api GET "/api/orders?search=$prefix-B1-website&pageSize=50" $staff).Body
        $slugs = @(@($listed.items) | ForEach-Object { [string]$_.channel.slug } | Sort-Object) -join ','
        Check @(3) '... orders with that external id: one per channel' 'pos,website' $slugs
        if ($Full) {
            & (Join-Path $script:RepoRoot 'scripts/load/run.ps1') -Project $Project -BaseUrl $script:BaseUrl -EnvFile $EnvFile -Scenario duplicates -Size small
            Check @(3) 'load scenario "duplicates" (50 ids sent twice at once = 100 requests): 50 orders, invariants hold' 0 $LASTEXITCODE
        } else {
            Skip @(3) 'load scenario "duplicates"' 'Full only'
        }
    } catch { Fail-Block @(3) 'B2' $_ }

    # ---- B3. Concurrency, stock figures, movement + outbox (criteria 4, 5, 6) ------------------------------------------
    try {
        $after = Get-Product $p1 $staff
        # 20 imported; one unit each for website, pos, digital-platform and the second pos order (B2); the webhook took 2.
        $reserved = 4 + $(if ($Full) { 2 } else { 0 })
        Check @(5) "P1 before the orders: total|reserved|available" '20|0|20' (Get-Figures $before)
        Check @(5) "P1 after them (by the API): $reserved reserved, available = total - reserved" "20|$reserved|$(20 - $reserved)" (Get-Figures $after)
        $moves = @((Invoke-Api GET "/api/inventory/movements?productId=$p1&pageSize=100" $staff).Body.items)
        Check @(5, 6) "P1's movements: one Import of 20, one Reserve per order ($($reserved - $(if ($Full) { 1 } else { 0 })) reservations)" "Import:20 Reserve:$($reserved - $(if ($Full) { 1 } else { 0 }))" "Import:$(@($moves | Where-Object { $_.type -ceq 'Import' } | ForEach-Object { [int]$_.stock }) -join '+') Reserve:$(@($moves | Where-Object { $_.type -ceq 'Reserve' }).Count)"
        if ($Full) {
            $rows = @(Invoke-Sql "select count(*) from `"InventoryMovements`" m where m.`"ProductId`" = '$p1' and (select count(*) from `"OutboxEvents`" e where e.`"InventoryMovementId`" = m.`"InventoryMovementId`" and e.`"EventType`" = 'inventory.changed') <> 1;")
            Check @(6) '... psql: each of them has exactly one inventory.changed outbox event' '0' $(if ($rows.Count -ge 1) { [string]$rows[0] } else { 'no answer' })
            & (Join-Path $script:RepoRoot 'scripts/load/run.ps1') -Project $Project -BaseUrl $script:BaseUrl -EnvFile $EnvFile -Scenario contention -Size small
            Check @(4, 5, 6) 'load scenario "contention" (60 orders for one product with stock 20): 20 reserved, 40 refused, invariants hold' 0 $LASTEXITCODE
        } else {
            Skip @(4) 'load scenario "contention"' 'Full only'
        }
    } catch { Fail-Block @(4, 5, 6) 'B3' $_ }

    # ---- B4 (part 1). Retry of a temporary failure; the SKU rules ------------------------------------------------------
    try {
        $p4 = New-Product $manager "$prefix-P4" 0 5
        $mapping = Invoke-ApiProblem POST "/api/channels/$($channelIds['pos'])/products" $manager @{ productId = $p4; externalSku = "SIM-TEMP-$prefix" } 'en'
        if ($mapping.Status -eq 201) { $mappings.Add([pscustomobject]@{ Channel = $channelIds['pos']; Body = $mapping.Body }) }
        $tempTask = Wait-Task ([string]$mapping.Body.syncTaskId) $staff
        $tempAttempts = @(if ($tempTask) { $tempTask.attempts })
        $skuRules = ($tempAttempts.Count -ge 2)
        if ($skuRules) {
            Check @(7) 'SIM-TEMP- mapping on pos: the sync task Succeeded at its second attempt' 'Succeeded 2' "$(Get-TaskStatus $tempTask) $($tempAttempts.Count)"
            Check @(7, 9) '... its first attempt failed with channel_unavailable (and a message)' 'channel_unavailable True' "$($tempAttempts[0].errorCode) $([bool]$tempAttempts[0].errorMessage)"
        } else {
            Check @(7) 'SIM-TEMP- mapping on pos: the sync task Succeeded' 'Succeeded' (Get-TaskStatus $tempTask)
            Skip @(7) 'the retry of a temporary failure (SIM-TEMP-)' 'SKU rules off'
        }
    } catch { Fail-Block @(7) 'B4 retry' $_ }

    # ---- B4 (part 2) + B5 trace. Retries to the limit, DLQ, trace, correction and manual retry (Full) ------------------
    $p5 = $null
    if ($Full) {
        try {
            if (-not $skuRules) { throw 'The SKU rules are off on a Full stack: is docker/docker-compose.acceptance.yml part of it?' }
            $dlq = 'stock.sync.marketplace.dlq'
            $dlqBefore = Get-QueueMessages $dlq
            $dp = $channelIds['digital-platform']
            $p5 = New-Product $manager "$prefix-P5" 0 10
            $correlation = "$prefix-down"
            $downMapping = Invoke-ApiProblem POST "/api/channels/$dp/products" $manager @{ productId = $p5; externalSku = "SIM-DOWN-$prefix" } 'en'
            if ($downMapping.Status -eq 201) { $mappings.Add([pscustomobject]@{ Channel = $dp; Body = $downMapping.Body }) }
            $order = Submit-Order 'website' $staff "$prefix-B5-down" $customerId $p5 1 $correlation
            $orderTask = Wait-Task ([string]$order.Body.taskId) $staff
            $initial = Wait-Task ([string]$downMapping.Body.syncTaskId) $staff
            $fromOrder = @(Wait-SyncTasks $dp $correlation $staff 1)
            # Every sync task of the pair, once none is unfinished: besides the mapping's and the order's, the import of
            # P5's stock can become one too when the worker consumes it after the mapping exists.
            $deadline = (Get-Date).AddSeconds($TaskWaitSeconds)
            do {
                $pairTasks = @((Invoke-Api GET "/api/processing/tasks?taskType=StockSync&channelId=$dp&productId=$p5&pageSize=100" $manager).Body.items)
                $open = @($pairTasks | Where-Object { ($_.status -cne 'Succeeded') -and ($_.status -cne 'Failed') }).Count
                if ($open -gt 0) { Start-Sleep -Seconds 1 }
            } while (($open -gt 0) -and ((Get-Date) -lt $deadline))
            $failed = @($pairTasks | Where-Object { $_.status -ceq 'Failed' })
            Check @(7) 'SIM-DOWN- mapping on digital-platform: every sync task of the pair Failed (the mapping''s, the order''s, ...)' "Failed Failed 0 $($pairTasks.Count)" "$(Get-TaskStatus $initial) $(if ($fromOrder.Count -ge 1) { $fromOrder[0].status } else { 'none' }) $open $($failed.Count)"
            $downTask = if ($fromOrder.Count -ge 1) { (Invoke-Api GET "/api/processing/tasks/$($fromOrder[0].taskId)" $manager).Body } else { $null }
            $attempts = @(if ($downTask) { $downTask.attempts })
            # Temporary errors only: channel_unavailable, and channel_circuit_open once the channel's breaker opened after
            # repeated failures (doc 13; the short retry delays of this stack put the attempts in one sampling window).
            $temporary = @($attempts | Where-Object { @('channel_unavailable', 'channel_circuit_open') -ccontains $_.errorCode }).Count
            Check @(7) '... the order''s after 5 attempts (StockSyncWorker:MaxAttempts), the first channel_unavailable, each a temporary error' '5 channel_unavailable 5' "$($attempts.Count) $(if ($attempts.Count -ge 1) { $attempts[0].errorCode }) $temporary"
            $deadline = (Get-Date).AddSeconds(30)
            do {
                $dlqNow = Get-QueueMessages $dlq
                if ($dlqNow -lt $dlqBefore + $failed.Count) { Start-Sleep -Seconds 1 }
            } while (($dlqNow -lt $dlqBefore + $failed.Count) -and ((Get-Date) -lt $deadline))
            Check @(7) "... one message per failed task in $dlq (rabbitmqctl)" ($dlqBefore + $failed.Count) $dlqNow
            $alert = Get-NewestAlert $staff $p5 'StockSyncFailed'
            Check @(7) '... one StockSyncFailed alert of the pair, Open' "Open $dp" "$(if ($alert) { "$($alert.status) $($alert.channelId)" } else { 'none' })"

            # B5: the trace of that order's flow.
            Check @(9) 'trace: the order (website) Reserved, its task Succeeded under the correlation id' "Reserved Succeeded $correlation" "$(if ($orderTask) { [string](Invoke-Api GET "/api/orders/$($orderTask.orderId)" $staff).Body.status }) $(Get-TaskStatus $orderTask) $(if ($orderTask) { $orderTask.correlationId })"
            Check @(9) '... the stock-sync task of that flow: id, type, status, channel, product, correlation id' "True StockSync Failed $dp $p5 $correlation" "$([bool]($downTask -and $downTask.taskId)) $(if ($downTask) { "$($downTask.taskType) $($downTask.status) $($downTask.channelId) $($downTask.productId) $($downTask.correlationId)" })"
            Check @(9) '... its attempts numbered 1-5, each by the StockSyncWorker with an instance id, an error code and a message' '1,2,3,4,5 5 5 5' "$(@($attempts | ForEach-Object { $_.attemptNo }) -join ',') $(@($attempts | Where-Object { $_.workerName -ceq 'StockSyncWorker' }).Count) $(@($attempts | Where-Object { $_.workerInstanceId }).Count) $(@($attempts | Where-Object { $_.errorCode -and $_.errorMessage }).Count)"
            $events = (Invoke-Api GET "/api/system-events?correlationId=$([uri]::EscapeDataString($correlation))&pageSize=100" $manager).Body
            Check @(9) '... GET /api/system-events?correlationId= lists the processing.failed event of that task' 1 @(@($events.items) | Where-Object { ($_.eventType -ceq 'processing.failed') -and ([string]$_.taskId -ceq [string]$downTask.taskId) }).Count

            # The channel recovers: the break of its circuit breaker is waited out, the mapping corrected, the tasks retried.
            Write-Host "Waiting $BreakerSeconds s for the circuit breaker of digital-platform to close..."
            Start-Sleep -Seconds $BreakerSeconds
            $fixed = Invoke-ApiProblem PUT "/api/channels/$dp/products/$($downMapping.Body.channelProductId)" $manager @{ externalSku = "DP-$prefix-P5"; status = 'Active' } 'en'
            Check @(7) 'the mapping corrected -> 200; its new sync task Succeeded' '200 Succeeded' "$($fixed.Status) $(Get-TaskStatus (Wait-Task ([string]$fixed.Body.syncTaskId) $staff))"
            $staffRetry = Invoke-ApiProblem POST "/api/processing/tasks/$($downTask.taskId)/retry" $staff $null 'en'
            $matrix['Staff: retry a failed task (POST /api/processing/tasks/{id}/retry)'] = "$($staffRetry.Status)"
            Check @(7, 14) 'Staff may not retry the failed task -> 403' 403 $staffRetry.Status
            $retried = @($failed | ForEach-Object { (Invoke-ApiProblem POST "/api/processing/tasks/$($_.taskId)/retry" $manager $null 'en').Status })
            $matrix['Manager: retry a failed task'] = "$(@($retried)[0])"
            Check @(7) "Manager retries the $($failed.Count) failed tasks -> 202 each" $failed.Count @($retried | Where-Object { $_ -eq 202 }).Count
            $after = @($failed | ForEach-Object { Get-TaskStatus (Wait-Task ([string]$_.taskId) $staff) })
            Check @(7) '... all Succeeded' $failed.Count @($after | Where-Object { $_ -ceq 'Succeeded' }).Count
            $resolved = Get-NewestAlert $staff $p5 'StockSyncFailed'
            Check @(7) '... and the alert is resolved by the system' 'Resolved' $(if ($resolved) { [string]$resolved.status } else { 'none' })
        } catch { Fail-Block @(7, 9) 'B4 DLQ drill' $_ }
    } else {
        Skip @(7) 'retries to the limit, DLQ, correction and manual retry (SIM-DOWN-)' 'Full only (drill; production retry delays add up to minutes)'
        Skip @(9) 'trace of a stock-sync failure (SIM-DOWN- flow)' 'Full only'
    }

    # ---- B5. Near-real-time and the trace of an order that could not be reserved (criteria 8, 9) ----------------------
    try {
        $socket = Open-Hub $manager
        Check @(8) 'a WebSocket to /api/hubs/notifications is open (JSON handshake sent)' 'Open' $(if ($socket) { [string]$socket.State } else { 'none' })
        $hubOrder = Submit-Order 'website' $staff "$prefix-B5-live" $customerId $p1 1
        $hubTaskId = [string]$hubOrder.Body.taskId
        $patterns = @(
            ('"taskId":"' + $hubTaskId + '"[^}]*"status":"Succeeded"'),
            ('"type":"inventory\.changed"[^}]*' + $p1),
            '"type":"order\.created"'
        )
        $all = if ($socket) { Watch-Hub $socket $patterns 30 } else { $false }
        $hubTask = Wait-Task $hubTaskId $staff
        $end = if ($hubTask) { ConvertTo-Utc $hubTask.updatedAt } else { $null }
        $lag = if ($end -and $script:HubSeen.ContainsKey($patterns[0])) { ($script:HubSeen[$patterns[0]] - $end).TotalSeconds } else { $null }
        Check @(8) 'the hints of an order arrive: processing.task.changed (Succeeded), inventory.changed of its product, order.created' $true $all
        Check @(8) '... the task hint within 5 s of the end of the task' $true (($null -ne $lag) -and ($lag -le 5))
        if ($null -ne $lag) { Write-Host ("   (task end -> hint received: {0:N2} s)" -f $lag) }

        # An order that cannot be reserved: a business failure the trace must explain.
        $correlation = "$prefix-short"
        $short = Submit-Order 'pos' $staff "$prefix-B5-short" $customerId $p1 1000 $correlation
        $shortTask = Wait-Task ([string]$short.Body.taskId) $staff
        $shortOrder = if ($shortTask -and $shortTask.orderId) { (Invoke-Api GET "/api/orders/$($shortTask.orderId)" $staff).Body } else { $null }
        Check @(9) 'an order of 1000 units -> InsufficientStock; its task Succeeded' 'InsufficientStock Succeeded' "$(if ($shortOrder) { $shortOrder.status }) $(Get-TaskStatus $shortTask)"
        # @( ) around the whole: an "if" unrolls a one-element array, and Windows PowerShell gives an object no Count.
        $orderTaskOfDetail = @(if ($shortOrder) { $shortOrder.tasks | Where-Object { [string]$_.taskId -ceq [string]$short.Body.taskId } })
        $first = if ($orderTaskOfDetail.Count -ge 1 -and @($orderTaskOfDetail[0].attempts).Count -ge 1) { @($orderTaskOfDetail[0].attempts)[0] } else { $null }
        Check @(9) '... GET /api/orders/{id}: its task with type, correlation id, message id and attempt 1 by the OrderWorker with an instance id' "OrderProcessing $correlation True 1 OrderWorker True" "$(if ($orderTaskOfDetail.Count -ge 1) { "$($orderTaskOfDetail[0].taskType) $($orderTaskOfDetail[0].correlationId) $([bool]$orderTaskOfDetail[0].messageId)" }) $(if ($first) { "$($first.attemptNo) $($first.workerName) $([bool]$first.workerInstanceId)" })"
        $events = (Invoke-Api GET "/api/system-events?correlationId=$([uri]::EscapeDataString($correlation))&pageSize=100" $manager).Body
        Check @(9) '... GET /api/system-events?correlationId= lists its order.insufficient_stock event' 1 @(@($events.items) | Where-Object { $_.eventType -ceq 'order.insufficient_stock' }).Count
        $matrix['Staff: read the system events (GET /api/system-events)'] = "$((Invoke-Api GET '/api/system-events?pageSize=1' $staff).Status)"
    } catch { Fail-Block @(8, 9) 'B5' $_ } finally { if ($socket) { $socket.Dispose(); $socket = $null } }

    # ---- B6. Low stock, history (criteria 10, 11) --------------------------------------------------------------------
    $p6 = $null; $p6Sku = "$prefix-P6"; $lowId = $null
    try {
        $p6 = New-Product $manager $p6Sku 5 6
        Check @(10) 'P6 (threshold 5) with 6 units: no open LowStock alert' 'Resolved' $(if ($a = Get-NewestAlert $staff $p6 'LowStock') { [string]$a.status } else { 'none' })
        $mapping = Invoke-ApiProblem POST "/api/channels/$($channelIds['website'])/products" $manager @{ productId = $p6; externalSku = "WEB-$p6Sku" } 'en'
        if ($mapping.Status -eq 201) { $mappings.Add([pscustomobject]@{ Channel = $channelIds['website']; Body = $mapping.Body }) }
        [void](Wait-Task ([string]$mapping.Body.syncTaskId) $staff)
        $order = Submit-Order 'website' $staff "$prefix-B6" $customerId $p6 2
        $orderTask = Wait-Task ([string]$order.Body.taskId) $staff
        $low = Get-NewestAlert $staff $p6 'LowStock'
        $lowId = if ($low) { [string]$low.alertId } else { $null }
        Check @(10) 'an order of 2 leaves 4 (<= 5): a LowStock alert of P6, Open, with stock and threshold' "Succeeded LowStock Open $p6 4 5" "$(Get-TaskStatus $orderTask) $(if ($low) { "$($low.type) $($low.status) $($low.productId) $($low.availableStock) $($low.lowStockThreshold)" })"
        $staffAck = Invoke-ApiProblem POST "/api/alerts/$lowId/acknowledge" $staff $null 'en'
        $matrix['Staff: acknowledge an alert'] = "$($staffAck.Status)"
        Check @(10, 14) 'Staff may not acknowledge it -> 403' 403 $staffAck.Status
        $ack = Invoke-ApiProblem POST "/api/alerts/$lowId/acknowledge" $manager $null 'en'
        $matrix['Manager: acknowledge an alert'] = "$($ack.Status)"
        Check @(10, 14) 'Manager acknowledges it -> 200, Acknowledged' '200 Acknowledged' "$($ack.Status) $([string](Invoke-Api GET "/api/alerts/$lowId" $staff).Body.status)"
        $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "$p6Sku-GRN2"; documentSeries = 'ACC'; items = @(@{ productId = $p6; stock = 10 }) }
        Check @(10) 'an import of 10 -> the alert is resolved by the system' '200 Resolved' "$($import.Status) $([string](Invoke-Api GET "/api/alerts/$lowId" $staff).Body.status)"

        $moves = @(@((Invoke-Api GET "/api/inventory/movements?productId=$p6&pageSize=100" $staff).Body.items) | Sort-Object { ConvertTo-Utc $_.createdAt })
        $orderId = if ($orderTask) { [string]$orderTask.orderId } else { '' }
        Check @(11) 'P6''s movement history in order: Import 6, Reserve 2, Import 10' 'Import:6 Reserve:2 Import:10' (@($moves | ForEach-Object { "$($_.type):$($_.stock)" }) -join ' ')
        $documents = if ($moves.Count -eq 3) { "$($moves[0].referenceType) $($moves[0].documentNumber) | $($moves[1].referenceType) $($moves[1].referenceId) | $($moves[2].documentSeries)/$($moves[2].documentNumber) $([bool]$moves[2].createdByEmail)" } else { 'not three movements' }
        Check @(11) '... with their documents: the import document, the order, series/number and who' "StockImport $p6Sku-GRN | Order $orderId | ACC/$p6Sku-GRN2 True" $documents
        $deadline = (Get-Date).AddSeconds($TaskWaitSeconds)
        do {
            $tasks = @((Invoke-Api GET "/api/processing/tasks?productId=$p6&pageSize=100" $staff).Body.items)
            $open = @($tasks | Where-Object { ($_.status -cne 'Succeeded') -and ($_.status -cne 'Failed') }).Count
            if (($tasks.Count -lt 3) -or ($open -gt 0)) { Start-Sleep -Seconds 1 }
        } while ((($tasks.Count -lt 3) -or ($open -gt 0)) -and ((Get-Date) -lt $deadline))
        # At least three: a Workers host in Development also reconciles active mappings every minute.
        Check @(11) '... its processing history: the stock syncs of the mapping, the order and the import (3 or more), all Succeeded, each with its StockSyncWorker attempt' 'True True True' "$($tasks.Count -ge 3) $(@($tasks | Where-Object { $_.status -cne 'Succeeded' }).Count -eq 0) $(@($tasks | Where-Object { -not ($_.attemptCount -ge 1 -and $_.lastAttempt -and $_.lastAttempt.workerName -ceq 'StockSyncWorker') }).Count -eq 0)"
    } catch { Fail-Block @(10, 11) 'B6' $_ }

    # ---- B7. Excel export, image in S3-compatible storage (criteria 12, 13) -----------------------------------------
    try {
        $staffExport = Invoke-Api POST '/api/reports/inventory/export' $staff @{ search = $p6Sku; lowStockOnly = $false; timeZone = 'Asia/Ho_Chi_Minh' }
        $matrix['Staff: request an export'] = "$($staffExport.Status)"
        Check @(12, 14) 'Staff may not request an export -> 403' 403 $staffExport.Status
        $export = Invoke-Api POST '/api/reports/inventory/export' $manager @{ search = $p6Sku; lowStockOnly = $false; timeZone = 'Asia/Ho_Chi_Minh' }
        $matrix['Manager: request an export'] = "$($export.Status)"
        $exportTask = Wait-Task ([string]$export.Body.taskId) $manager
        Check @(12) 'Manager requests an inventory export -> 202; its task Succeeded' '202 Succeeded' "$($export.Status) $(Get-TaskStatus $exportTask)"
        $link = Invoke-Api GET "/api/reports/exports/$($export.Body.taskId)/download" $manager
        $file = if ($link.Status -eq 200) { Invoke-Raw GET ([string]$link.Body.url) $null $null $null } else { $null }
        $link = $null
        Check @(12) '... the download link -> 200, an .xlsx file (ZIP signature)' '200 True' "$(if ($file) { $file.Status }) $(if ($file) { Test-StartsWithPk $file.Bytes })"
        $sheet = if ($file -and (Test-StartsWithPk $file.Bytes)) { Get-XlsxText $file.Bytes } else { '' }
        Check @(12) '... its worksheet contains P6''s SKU' $true ($sheet.Contains($p6Sku))

        $presign = Invoke-Api POST "/api/products/$p6/image/presign" $manager @{ contentType = 'image/png'; sizeBytes = $png.Length }
        $put = if ($presign.Status -eq 200) { Invoke-Raw PUT ([string]$presign.Body.uploadUrl) $null $png 'image/png' } else { $null }
        Check @(13) 'image: presign -> 200, PUT to the storage -> 200' '200 200' "$($presign.Status) $(if ($put) { $put.Status })"
        $confirm = Invoke-ApiProblem PUT "/api/products/$p6/image" $manager @{ objectKey = $presign.Body.objectKey } 'en'
        $presign = $null
        $image = if ($confirm.Body) { $confirm.Body.image } else { $null }
        $fields = if ($image) { (@($image.PSObject.Properties | ForEach-Object { $_.Name } | Sort-Object) -join ',') } else { 'no image' }
        Check @(13) '... confirm -> 200; the product has a link and only key data: contentType, expiresAt, sizeBytes, updatedAt, url' '200 contentType,expiresAt,sizeBytes,updatedAt,url' "$($confirm.Status) $fields"
        $download = if ($image) { Invoke-Raw GET ([string]$image.url) $null $null $null } else { $null }
        $image = $null
        Check @(13) '... GET the link -> 200 with the uploaded bytes' '200 True' "$(if ($download) { $download.Status }) $(if ($download) { Test-SameBytes $download.Bytes $png })"
        if ($Full) {
            $columns = @(Invoke-Sql "select string_agg(column_name || ':' || data_type, ',' order by column_name) filter (where column_name like 'Image%'), count(*) filter (where data_type = 'bytea') from information_schema.columns where table_name = 'Products';")
            Check @(13) '... psql: Products stores key, type, size and time of the image, and has no binary column' 'ImageContentType:character varying,ImageKey:character varying,ImageSizeBytes:bigint,ImageUpdatedAt:timestamp with time zone|0' $(if ($columns.Count -ge 1) { [string]$columns[0] } else { 'no answer' })
        }
    } catch { Fail-Block @(12, 13) 'B7' $_ }

    # ---- B8. Authentication and authorization (criterion 14) ----------------------------------------------------------
    try {
        Check @(14) 'no token -> 401' 401 (Invoke-Api GET '/api/orders' $null).Status
        $forged = Get-ForgedTokens $staff
        Check @(14) 'a token with a changed signature -> 401' 401 (Invoke-Api GET '/api/orders' $forged.Signature).Status
        Check @(14) 'a Staff token whose payload claims Admin under the old signature -> 401' 'True 401' "$($forged.PayloadChanged) $((Invoke-Api GET '/api/users' $forged.Payload).Status)"
        Skip @(14) 'an expired token' 'needs the signing key to make one; JwtBearer checks the lifetime (30 s clock skew), see docs/acceptance.md 14'
        $matrix['Staff: create a product (POST /api/products)'] = "$((Invoke-Api POST '/api/products' $staff @{ sku = "$prefix-NO"; name = 'not allowed'; price = 1; lowStockThreshold = 0 }).Status)"
        $matrix['Staff: list users (GET /api/users)'] = "$((Invoke-Api GET '/api/users' $staff).Status)"
        $websiteOrder = $b1Orders['website']
        $matrix['Staff: pay an order (POST /api/orders/{id}/payment)'] = "$((Invoke-ApiProblem POST "/api/orders/$websiteOrder/payment" $staff @{ paymentMethod = 'Cash' } 'en').Status)"
        $paid = Invoke-ApiProblem POST "/api/orders/$websiteOrder/payment" $manager @{ paymentMethod = 'Cash' } 'en'
        $matrix['Manager: pay an order'] = "$($paid.Status)"
        $matrix['Manager: list users (GET /api/users)'] = "$((Invoke-Api GET '/api/users' $manager).Status)"
        $matrix['Admin: list users (GET /api/users)'] = "$((Invoke-Api GET '/api/users?pageSize=1' $admin).Status)"
        $expected = [ordered]@{
            'Admin: create a user (POST /api/users)' = '201'
            'Staff: order intake (POST /api/orders/website)' = '202'
            'Staff: order intake (POST /api/orders/pos)' = '202'
            'Staff: order intake (POST /api/orders/digital-platform)' = '202'
            'Staff: create a product (POST /api/products)' = '403'
            'Staff: list users (GET /api/users)' = '403'
            'Staff: pay an order (POST /api/orders/{id}/payment)' = '403'
            'Staff: acknowledge an alert' = '403'
            'Staff: request an export' = '403'
            'Staff: read the system events (GET /api/system-events)' = '403'
            'Manager: pay an order' = '200'
            'Manager: acknowledge an alert' = '200'
            'Manager: request an export' = '202'
            'Manager: list users (GET /api/users)' = '403'
            'Admin: list users (GET /api/users)' = '200'
        }
        if ($Full) {
            $expected['Staff: retry a failed task (POST /api/processing/tasks/{id}/retry)'] = '403'
            $expected['Manager: retry a failed task'] = '202'
        }
        Write-Host ''
        Write-Host 'Role matrix (status of one representative request per cell):'
        foreach ($cell in $expected.Keys) {
            $actual = if ($matrix.Contains($cell)) { $matrix[$cell] } else { 'not run' }
            Write-Host ("   {0,-66} expected {1}, actual {2}" -f $cell, $expected[$cell], $actual)
            Check @(14) "role matrix - $cell" $expected[$cell] $actual
        }
        Check @(12) 'the paid order''s invoice and e-mail tasks Succeeded' 'Succeeded Succeeded' "$(Get-TaskStatus (Wait-Task ([string]$paid.Body.invoiceTaskId) $manager)) $(Get-TaskStatus (Wait-Task ([string]$paid.Body.emailTaskId) $manager))"
    } catch { Fail-Block @(14) 'B8' $_ }

    # ---- B3 (end). Invariants after everything (Full) --------------------------------------------------------------
    if ($Full) {
        try {
            $deadline = (Get-Date).AddSeconds(120)
            do {
                $open = [int](Invoke-Sql 'select (select count(*) from "ProcessingTasks" where "Status" in (''Pending'', ''InProgress'', ''RetryScheduled'')) + (select count(*) from "OutboxEvents" where "Status" = ''Pending'');')[0]
                if ($open -gt 0) { Start-Sleep -Seconds 2 }
            } while (($open -gt 0) -and ((Get-Date) -lt $deadline))
            Check @(4, 5, 6) 'nothing unfinished, nothing waiting in the outbox' 0 $open
            $invariants = [System.IO.File]::ReadAllText((Join-Path $script:RepoRoot 'scripts/load/invariants.sql'))
            foreach ($row in (Invoke-Sql $invariants | Where-Object { $_ -match '\|' })) {
                $parts = ([string]$row).Split('|')
                $criterion = if ($parts[0] -like 'orders:*') { @(3) } elseif ($parts[0] -like 'stock:*') { @(4, 5) } elseif ($parts[0] -like 'movements:*' -or $parts[0] -like 'outbox:*') { @(6) } else { @(7) }
                Check $criterion "invariant: $($parts[0].Trim())" 0 ([int]$parts[1])
            }
        } catch { Fail-Block @(4, 5, 6) 'invariants' $_ }
    }
}
catch {
    Fail-Block @() 'the run' $_
}
finally {
    if ($socket) { $socket.Dispose() }
    # Mappings deactivated, accounts locked - also after a failed step. Products cannot be deactivated (no such API).
    if ($manager -and ($mappings.Count -gt 0)) {
        $off = @($mappings | ForEach-Object {
                $body = $_.Body
                $sku = if ($body.externalSku) { [string]$body.externalSku } else { $null }
                $product = if ($body.externalProductId) { [string]$body.externalProductId } else { $null }
                (Invoke-ApiProblem PUT "/api/channels/$($_.Channel)/products/$($body.channelProductId)" $manager @{ externalSku = $sku; externalProductId = $product; status = 'Inactive' } 'en').Status
            } | Where-Object { $_ -eq 200 }).Count
        Check @() 'clean-up: the acceptance mappings are deactivated' $mappings.Count $off
    }
    if ($admin) {
        $locked = @(@($managerUser, $staffUser) | Where-Object { $_ -and $_.Body } | ForEach-Object { (Invoke-Api POST "/api/users/$($_.Body.userId)/lock" $admin).Status } | Where-Object { $_ -eq 200 }).Count
        Check @() 'clean-up: the acceptance accounts are locked' 2 $locked
    }
}
Write-CriteriaTable
Complete-Smoke
