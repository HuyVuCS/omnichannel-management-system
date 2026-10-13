# Smoke check for task 026b: error answers a page on the frontend origin can read (A1), the product filter of the
# processing tasks (D) and - on a throwaway stack only - the stale-task drill (C).
#
# Default: the local development API (started and stopped by this script when it is not running; no Workers needed).
# Another environment (a production-like stack through its proxy):
#   SMOKE_BASE_URL          the API origin of the proxy, e.g. http://localhost:18080
#   SMOKE_ENV_FILE          the environment file of that stack (its admin), or SMOKE_ADMIN_EMAIL / SMOKE_ADMIN_PASSWORD
#   SMOKE_FRONTEND_ORIGIN   FRONTEND_ORIGIN of that stack (default: http://localhost:5173, the development frontend)
# The stale-task drill (never on production: the script refuses the project omnichannel-prod):
#   SMOKE_STALE_DRILL=1
#   SMOKE_COMPOSE_PROJECT   the compose project of the throwaway stack; its Workers must run with short settings
#                           (StaleTasks__Interval / StaleTasks__StaleAfter and the retry and publisher waits they
#                           are checked against), its PostgreSQL container is entered to mark one message as sent
#   SMOKE_STALE_WAIT_SECONDS   how long to wait for the sweep (default 240)
#
# What it leaves behind: two smoke products with deactivated mappings and their stock-sync tasks; with the drill one
# customer and one order. The last step uses up the login limit of this machine's address for a minute: wait 65 s
# before the next script that logs in.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-026b.ps1
. (Join-Path $PSScriptRoot '_common.ps1')
. (Join-Path $PSScriptRoot '_hardening.ps1')

$frontendOrigin = if ($env:SMOKE_FRONTEND_ORIGIN) { $env:SMOKE_FRONTEND_ORIGIN.TrimEnd('/') } else { 'http://localhost:5173' }
$throughProxy = $script:ForeignEnvironment
$drill = ($env:SMOKE_STALE_DRILL -eq '1')
$project = [string]$env:SMOKE_COMPOSE_PROJECT
$staleWaitSeconds = if ($env:SMOKE_STALE_WAIT_SECONDS) { [int]$env:SMOKE_STALE_WAIT_SECONDS } else { 240 }
if ($drill -and ((-not $project) -or ($project -eq 'omnichannel-prod') -or ($project -eq 'omnichannel'))) {
    throw 'The stale-task drill needs SMOKE_COMPOSE_PROJECT = a throwaway stack; it never runs on omnichannel-prod or on development.'
}

# One request as a page on $Origin sends it: no redirect followed, no exception on an error status (Status 0 = no HTTP
# answer). $Body with -Chunked is sent without a Content-Length.
function Invoke-FromOrigin([string]$Method, [string]$Path, [string]$Origin, [string]$CorrelationId, [string]$Token, [byte[]]$Body, [switch]$Chunked) {
    $request = [System.Net.HttpWebRequest]::Create("$script:BaseUrl$Path")
    $request.Method = $Method
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.Headers.Add('Origin', $Origin)
    if ($CorrelationId) { $request.Headers.Add('X-Correlation-Id', $CorrelationId) }
    if ($Token) { $request.Headers.Add('Authorization', "Bearer $Token") }
    $response = $null
    try {
        if ($null -ne $Body) {
            $request.ContentType = 'application/json'
            if ($Chunked) { $request.SendChunked = $true } else { $request.ContentLength = $Body.Length }
            $stream = $request.GetRequestStream()
            $stream.Write($Body, 0, $Body.Length)
            $stream.Close()
        }
        $response = $request.GetResponse()
    }
    catch [System.Net.WebException] { $response = $_.Exception.Response }
    catch [System.IO.IOException] { $response = $null }
    if ($null -eq $response) { return [pscustomobject]@{ Status = 0; Headers = @{}; Text = '' } }
    try {
        $headers = @{}
        foreach ($name in $response.Headers.AllKeys) { $headers[$name.ToLowerInvariant()] = [string]$response.Headers[$name] }
        $text = (New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)).ReadToEnd()
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $headers; Text = $text }
    } finally { $response.Close() }
}

# A body the server refuses without reading it: the answer can arrive while this client is still sending, and the
# connection is then closed under it (no HTTP answer, Status 0). That is this client, not the server: asked again.
function Invoke-Refused([string]$Method, [string]$Path, [string]$Origin, [string]$CorrelationId, [string]$Token, [byte[]]$Body) {
    foreach ($try in 1..5) {
        $answer = Invoke-FromOrigin $Method $Path $Origin $CorrelationId $Token $Body
        if ($answer.Status -ne 0) { return $answer }
        Start-Sleep -Milliseconds 300
    }
    return $answer
}

# "status | allowed origin | the page may read X-Correlation-Id | the header is the id that was sent | the body names
# the same id" of an error answer.
function Get-ReadableSummary($Answer, [string]$CorrelationId) {
    $exposed = (((Get-HardeningHeader $Answer 'access-control-expose-headers') -split ',\s*') -contains 'X-Correlation-Id')
    $header = Get-HardeningHeader $Answer 'x-correlation-id'
    $inBody = 'no body'
    if ($Answer.Text) { try { $inBody = [string]($Answer.Text | ConvertFrom-Json).correlationId } catch { $inBody = 'not JSON' } }
    return "$($Answer.Status) | $(Get-HardeningHeader $Answer 'access-control-allow-origin') | exposed $exposed | header $($header -ceq $CorrelationId) | body $($inBody -ceq $header)"
}

function Get-ErrorCode($Answer) { try { return [string]($Answer.Text | ConvertFrom-Json).errorCode } catch { return 'no problem document' } }

# Runs docker without its output and returns the exit code. Docker writes progress to standard error, which Windows
# PowerShell 5.1 turns into an error under ErrorActionPreference Stop.
function Invoke-Docker([string[]]$Arguments, [switch]$Output) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $lines = @(& docker @Arguments 2>$null) } finally { $ErrorActionPreference = $previous }
    if ($Output) { return ($lines -join "`n") }
    return $LASTEXITCODE
}

# The ids of the containers of a service of the drill's compose project.
function Get-ServiceContainers([string]$Service) {
    $ids = Invoke-Docker @('ps', '-a', '-q', '--filter', "label=com.docker.compose.project=$project", '--filter', "label=com.docker.compose.service=$Service") -Output
    return @($ids -split "`n" | Where-Object { $_ })
}

function Wait-TaskStatus([string]$TaskId, [string]$Token, [string]$Status, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $last = 'unknown'
    do {
        $task = (Invoke-Api GET "/api/processing/tasks/$TaskId" $Token).Body
        if ($task) { $last = [string]$task.status }
        if ($last -ceq $Status) { return $task }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $task
}

$mappings = @()
$website = $null
$admin = $null
try {
    Start-Smoke 'Task 026b - readable error answers, product filter of the tasks, stale-task drill'
    $credential = Get-AdminCredential
    $admin = Get-Token $credential.Email $credential.Password
    $n = Get-Random -Minimum 10000 -Maximum 99999
    $readable = "$frontendOrigin | exposed True | header True | body True"

    # ---- A1: error answers from the frontend origin ----------------------------------------------------------------
    $id = "smoke-026b-401-$n"
    $unauthorized = Invoke-FromOrigin GET '/api/auth/me' $frontendOrigin $id $null $null
    Assert-Step '01 a 401 is readable from the frontend origin (CORS, correlation id in header and body)' "401 | $readable" (Get-ReadableSummary $unauthorized $id)

    $tooLarge = New-Object byte[] ($script:BodyLimitBytes + 1)
    for ($i = 0; $i -lt $tooLarge.Length; $i++) { $tooLarge[$i] = 0x20 }
    $tooLarge[0] = 0x7B ; $tooLarge[1] = 0x7D
    $id = "smoke-026b-413-$n"
    $announced = Invoke-Refused POST '/api/inventory/import' $frontendOrigin $id $admin $tooLarge
    # Through a proxy too: its own limit is 8 times the API's (task 026c), so the API is the one that answers.
    Assert-Step '02 a 413 for an announced length is readable from the frontend origin' "413 | $readable" (Get-ReadableSummary $announced $id)
    Assert-Step '03 ... as the problem document request_body_too_large' 'request_body_too_large' (Get-ErrorCode $announced)

    $id = "smoke-026b-413c-$n"
    $chunked = Invoke-FromOrigin POST '/api/inventory/import' $frontendOrigin $id $admin $tooLarge -Chunked
    if ($throughProxy) {
        # Without a Content-Length the proxy cuts the body off itself and answers alone (runbook 15).
        Assert-Step '04 the same body without a length through the proxy -> 413 (answered by the proxy)' 413 $chunked.Status
    } else {
        Assert-Step '04 the same body without a length, cut off by the server, is readable too' "413 | $readable" (Get-ReadableSummary $chunked $id)
    }

    $foreign = Invoke-Refused POST '/api/inventory/import' 'https://not-the-frontend.example' "smoke-026b-other-$n" $admin $tooLarge
    Assert-Step '05 another origin gets the 413 without a CORS header' '413 | none' "$($foreign.Status) | $(Get-HardeningHeader $foreign 'access-control-allow-origin')"

    # ---- D: the tasks of one product -------------------------------------------------------------------------------
    $website = [string]@((Invoke-Api GET '/api/channels' $admin).Body | Where-Object { $_.slug -eq 'website' })[0].channelId
    Assert-Step '06 the website channel exists' $true ($website.Length -gt 0)
    $productIds = @()
    foreach ($i in 1..2) {
        $product = Invoke-Api POST '/api/products' $admin @{ sku = "SMOKE-026B-$n-$i"; name = "Smoke product 026b-$n-$i"; price = 15000; lowStockThreshold = 0 }
        if ($product.Status -eq 201) { $productIds += [string]$product.Body.productId }
    }
    Assert-Step '07 create two smoke products' 2 $productIds.Count
    foreach ($productId in $productIds) {
        $mapping = Invoke-ApiProblem POST "/api/channels/$website/products" $admin @{ productId = $productId; externalSku = "SMOKE-026B-$n-EXT-$($mappings.Count + 1)" } 'en'
        if ($mapping.Status -eq 201) { $mappings += $mapping.Body }
    }
    Assert-Step '08 map them on website (each starts a stock-sync task)' 2 $mappings.Count

    $first = (Invoke-Api GET "/api/processing/tasks?productId=$($productIds[0])" $admin).Body
    $firstIds = @($first.items | ForEach-Object { [string]$_.taskId })
    Assert-Step '09 tasks?productId= finds the task of the first product' $true ($first.totalCount -ge 1)
    Assert-Step '10 ... and only tasks of that product' 0 (@($first.items | Where-Object { [string]$_.productId -cne $productIds[0] }).Count)
    $combined = (Invoke-Api GET "/api/processing/tasks?productId=$($productIds[0])&taskType=StockSync&channelId=$website" $admin).Body
    Assert-Step '11 ... the same tasks together with taskType and channelId' ([int]$first.totalCount) ([int]$combined.totalCount)
    $noOrderTasks = (Invoke-Api GET "/api/processing/tasks?productId=$($productIds[0])&taskType=OrderProcessing" $admin).Body
    Assert-Step '12 ... none when another filter excludes them' 0 ([int]$noOrderTasks.totalCount)
    $second = (Invoke-Api GET "/api/processing/tasks?productId=$($productIds[1])" $admin).Body
    Assert-Step '13 the second product has tasks of its own, none of the first' 0 (@($second.items | Where-Object { $firstIds -contains [string]$_.taskId }).Count)
    $invalid = Invoke-ApiProblem GET '/api/processing/tasks?productId=not-a-guid' $admin $null 'en'
    $invalidKeys = if ($invalid.Body -and $invalid.Body.errors) { (@($invalid.Body.errors.PSObject.Properties.Name) -join ',') } else { 'no errors' }
    Assert-Step '14 productId that is no GUID -> 400 on productId' '400 | productId' "$($invalid.Status) | $invalidKeys"

    # ---- C: the stale-task drill (throwaway stack only) --------------------------------------------------------------
    if ($drill) {
        # @(...): a function that returns one id hands back a string, and its [0] would be a character.
        $workers = @(Get-ServiceContainers 'workers')
        $postgres = @(Get-ServiceContainers 'postgres')
        Assert-Step "15 the stack $project has Workers and a PostgreSQL container" $true (($workers.Count -ge 1) -and ($postgres.Count -eq 1))
        $stopped = 0
        foreach ($container in $workers) { if ((Invoke-Docker @('stop', $container)) -eq 0) { $stopped++ } }
        Assert-Step '16 stop the Workers (nothing publishes, nothing consumes, nothing sweeps)' $workers.Count $stopped

        $customer = Invoke-Api POST '/api/customers' $admin @{ name = "Smoke customer 026b-$n"; phone = $null; email = "smoke-026b-$n@example.com"; address = $null }
        Invoke-Api POST '/api/inventory/import' $admin @{ documentNumber = "SMOKE-026B-$n"; documentSeries = $null; items = @(@{ productId = $productIds[0]; stock = 5 }) } | Out-Null
        $submitted = Invoke-Api POST '/api/orders/website' $admin @{ externalOrderId = "SMOKE-026B-$n"; customerId = $customer.Body.customerId; items = @(@{ productId = $productIds[0]; stock = 2 }) }
        $taskId = [string]$submitted.Body.taskId
        Assert-Step '17 submit an order -> 202 (its task is Pending, its message waits in the outbox)' '202 | Pending' "$($submitted.Status) | $([string](Invoke-Api GET "/api/processing/tasks/$taskId" $admin).Body.status)"

        # "Sent as far as the database knows, and never delivered": what a broker that lost its data leaves behind.
        $sqlFile = Join-Path ([System.IO.Path]::GetTempPath()) "smoke-026b-$n.sql"
        $sql = 'UPDATE "OutboxEvents" SET "Status" = ''Published'', "PublishedAt" = now() WHERE "AggregateId" = ''' + $taskId + ''' AND "EventType" = ''order.created'' AND "Status" = ''Pending'';'
        [System.IO.File]::WriteAllText($sqlFile, $sql, (New-Object System.Text.UTF8Encoding($false)))
        $marked = 'not run'
        if ((Invoke-Docker @('cp', $sqlFile, "$($postgres[0]):/tmp/smoke-026b.sql")) -eq 0) {
            $marked = Invoke-Docker @('exec', $postgres[0], 'psql', '-h', '127.0.0.1', '-U', 'postgres', '-d', 'omnichannel', '-v', 'ON_ERROR_STOP=1', '-At', '-f', '/tmp/smoke-026b.sql') -Output
        }
        Remove-Item $sqlFile -ErrorAction SilentlyContinue
        Assert-Step '18 mark its message as published by hand (the message itself never reaches the broker)' 'UPDATE 1' $marked

        $started = 0
        foreach ($container in $workers) { if ((Invoke-Docker @('start', $container)) -eq 0) { $started++ } }
        Assert-Step '19 start the Workers again' $workers.Count $started
        $failed = Wait-TaskStatus $taskId $admin 'Failed' $staleWaitSeconds
        Assert-Step "20 the task fails by itself within $staleWaitSeconds s" 'Failed' ([string]$failed.status)
        $lastAttempt = @($failed.attempts | Sort-Object attemptNo)[-1]
        Assert-Step '21 ... failed by the sweep with stale_no_progress' 'StaleTaskSweep | stale_no_progress' "$([string]$lastAttempt.workerName) | $([string]$lastAttempt.errorCode)"
        $alerts = (Invoke-Api GET '/api/alerts?type=ProcessingFailed&status=Open&pageSize=100' $admin).Body
        $alert = @($alerts.items | Where-Object { [string]$_.taskId -ceq $taskId })
        Assert-Step '22 ... with an open alert of the task' 'stale_no_progress' ([string]$(if ($alert.Count -eq 1) { $alert[0].errorCode } else { "alerts: $($alert.Count)" }))
        $runs = (Invoke-Api GET '/api/system-events?eventType=processing.stale_tasks_failed&pageSize=100' $admin).Body
        $named = @($runs.items | Where-Object { @($_.details.taskIds) -contains $taskId })
        Assert-Step '23 ... and the event of the run that names the task' 1 $named.Count
        $retry = Invoke-ApiProblem POST "/api/processing/tasks/$taskId/retry" $admin $null 'en'
        Assert-Step '24 retry -> 202, the task waits for a new message' '202 | Pending' "$($retry.Status) | $([string]$retry.Body.status)"
        $done = Wait-TaskStatus $taskId $admin 'Succeeded' 90
        Assert-Step '25 the retried task succeeds' 'Succeeded' ([string]$done.status)
        $attempts = @($done.attempts | Sort-Object attemptNo | ForEach-Object { "$([string]$_.workerName):$([string]$_.status)" }) -join ', '
        Assert-Step '26 ... with one attempt of the Order Worker after the one of the sweep' 'StaleTaskSweep:Failed, OrderWorker:Succeeded' $attempts
        Assert-Step '27 the order is reserved' 'Reserved' ([string](Invoke-Api GET "/api/orders/$([string]$done.orderId)" $admin).Body.status)
    }

    # ---- A1, last: the login limit of this address (one minute) ------------------------------------------------------
    $wrong = [System.Text.Encoding]::UTF8.GetBytes((@{ email = "nobody-026b-$n@example.com"; password = 'Wrong-password-026b' } | ConvertTo-Json))
    $limited = $null
    foreach ($attempt in 1..12) {
        $id = "smoke-026b-429-$n-$attempt"
        $answer = Invoke-FromOrigin POST '/api/auth/login' $frontendOrigin $id $null $wrong
        if ($answer.Status -eq 429) { $limited = Get-ReadableSummary $answer $id ; break }
    }
    Assert-Step '28 a 429 of the login limit is readable from the frontend origin' "429 | $readable" ([string]$limited)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    try {
        if ($mappings.Count -gt 0) {
            $off = @($mappings | ForEach-Object { (Invoke-ApiProblem PUT "/api/channels/$website/products/$($_.channelProductId)" $admin @{ externalSku = [string]$_.externalSku; status = 'Inactive' } 'en').Status } | Where-Object { $_ -eq 200 }).Count
            Assert-Step '29 deactivate the smoke mappings' $mappings.Count $off
        }
    } catch { Assert-Step "clean-up error: $($_.Exception.Message)" 'no error' 'error' }
    Stop-Smoke
}
Complete-Smoke
