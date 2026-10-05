# Smoke check for task 022 (reconciliation: the Workers host compares the central stock with what the simulated channels
# hold and starts a stock sync where they differ; the simulated channels keep the value of every push they accept).
# Needs PostgreSQL and RabbitMQ running (README section 1) and the SimulatedChannelStocks migration applied (README
# section 4). Uses a running API / Workers host or builds and starts them, and stops what it started.
# The Workers host must run in Development: only there the simulator's SKU rules (SIM-LOST-) are on and a reconciliation
# run starts every minute. The script waits for runs of that timer: it takes about six minutes before the frontend checks.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-022.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

# How long a reconciliation run may take to show up: the first one starts 60 s after the Workers host, then one a minute.
$RunWaitSeconds = 150
# How long nothing may happen once the channel holds the central value: two rounds of the timer and a margin.
$QuietSeconds = 150

# Asks for the task once per second until it is Succeeded or Failed; returns the last answer (or $null after the limit).
function Wait-Task([string]$TaskId, [string]$Token, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $task = (Invoke-Api GET "/api/processing/tasks/$TaskId" $Token).Body
        if ($task -and (($task.status -eq 'Succeeded') -or ($task.status -eq 'Failed'))) { return $task }
        Start-Sleep -Seconds 1
    }
    return $null
}

function Get-TaskStatus($Task) { if ($Task) { return [string]$Task.status } else { return 'not finished' } }

# The stock-sync tasks of one channel and product, created since $Since, that a reconciliation run created (their
# correlation id says so). The task list has no product filter: the pages of the channel since $Since are read.
function Get-ReconciliationTasks([string]$ChannelId, [string]$ProductId, [string]$Since, [string]$Token) {
    $found = @()
    for ($page = 1; $page -le 50; $page++) {
        $r = Invoke-Api GET "/api/processing/tasks?taskType=StockSync&channelId=$ChannelId&from=$Since&page=$page&pageSize=100" $Token
        if ($r.Status -ne 200) { throw "The task list answered HTTP $($r.Status)." }
        $found += @(@($r.Body.items) | Where-Object { ([string]$_.productId -ceq $ProductId) -and ([string]$_.correlationId -clike 'reconciliation-*') })
        if (($page * 100) -ge [int]$r.Body.totalCount) { break }
    }
    return $found
}

function Get-Instant { return [uri]::EscapeDataString((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ')) }

$admin = $null
$managerUser = $null
$manager = $null
$pos = $null
$mapping = $null
$sku = $null

try {
    Start-Smoke 'Task 022 - reconciliation of channel stock (simulated channels)'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999
    $since = Get-Instant

    $managerEmail = "smoke-022-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    # A product with 10 units, mapped on pos under an external SKU the simulated channel loses the updates of.
    $sku = "SMOKE-022-$n"
    $product = Invoke-Api POST '/api/products' $manager @{ sku = $sku; name = "Smoke product 022-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '02 create smoke product' 201 $product.Status
    $productId = [string]$product.Body.productId
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-022-GRN-$n"; items = @(@{ productId = $productId; stock = 10 }) }
    Assert-Step '03 import 10 units' 200 $import.Status
    $pos = [string]@((Invoke-Api GET '/api/channels' $manager).Body | Where-Object { $_.slug -eq 'pos' })[0].channelId
    Assert-Step '04 the pos channel exists' $true ([bool]$pos)

    $mapping = Invoke-ApiProblem POST "/api/channels/$pos/products" $manager @{ productId = $productId; externalSku = "SIM-LOST-$n" } 'en'
    Assert-Step '05 map on pos (SIM-LOST-)' 201 $mapping.Status
    Write-Host 'Waiting for the Stock Sync Worker (up to 60 s)...'
    $initial = Wait-Task $mapping.Body.syncTaskId $manager 60
    if ($null -eq $initial) { throw 'The initial stock-sync task did not finish within 60 s. Is the Workers host running (README section 6)?' }
    Assert-Step '06 its initial sync task (accepted by the channel, not stored)' 'Succeeded' (Get-TaskStatus $initial)

    # The channel holds no value for the product: the next reconciliation run finds the drift and starts a sync.
    Write-Host "Waiting for a reconciliation run to find the drift (up to $RunWaitSeconds s)..."
    $found = @()
    $deadline = (Get-Date).AddSeconds($RunWaitSeconds)
    while (((Get-Date) -lt $deadline) -and ($found.Count -eq 0)) {
        Start-Sleep -Seconds 3
        $found = @(Get-ReconciliationTasks $pos $productId $since $manager)
    }
    if ($found.Count -eq 0) {
        throw "No reconciliation task for the product within $RunWaitSeconds s. Is the Workers host a build of task 022, running in Development (SKU rules on, a run every minute - README section 8)? Restart a host of an older build."
    }
    Assert-Step "07 a reconciliation run created a stock-sync task for (pos, product) within $RunWaitSeconds s" $true ($found.Count -ge 1)
    $reconciled = $found[$found.Count - 1]
    $correlationId = [string]$reconciled.correlationId
    Assert-Step '08 ... its correlation id is reconciliation-{run id}' $true ($correlationId -cmatch '^reconciliation-[0-9a-f]{32}$')
    $finished = Wait-Task $reconciled.taskId $manager 60
    Assert-Step '09 ... the task ends' 'Succeeded' (Get-TaskStatus $finished)
    $attempts = @(if ($finished) { $finished.attempts })
    $worker = if ($attempts.Count -ge 1) { [string]$attempts[0].workerName } else { 'none' }
    $messageType = if ($finished -and $finished.message) { [string]$finished.message.eventType } else { 'none' }
    Assert-Step '10 ... pushed by the StockSyncWorker through a directed message' 'StockSyncWorker stock.sync.requested' "$worker $messageType"

    # The run is found again by its correlation id: its tasks on Sync & Processing, its event on Logs / audit.
    $byRun = Invoke-Api GET "/api/processing/tasks?taskType=StockSync&correlationId=$correlationId&pageSize=100" $manager
    Assert-Step '11 the task list filtered by the run finds the task' $true (@(@($byRun.Body.items) | Where-Object { [string]$_.taskId -ceq [string]$reconciled.taskId }).Count -eq 1)
    $events = @()
    $deadline = (Get-Date).AddSeconds(60)
    while (((Get-Date) -lt $deadline) -and ($events.Count -eq 0)) {
        $events = @((Invoke-Api GET "/api/system-events?eventType=reconciliation.completed&correlationId=$correlationId" $manager).Body.items)
        if ($events.Count -eq 0) { Start-Sleep -Seconds 2 }
    }
    Assert-Step '12 one reconciliation.completed event of the run' 1 $events.Count
    $level = if ($events.Count -ge 1) { [string]$events[0].level } else { 'none' }
    Assert-Step '13 ... level' 'Warning' $level
    $created = if ($events.Count -ge 1) { [int]$events[0].details.tasksCreated } else { 0 }
    $drifted = if ($events.Count -ge 1) { [int]$events[0].details.drifted } else { 0 }
    Assert-Step '14 ... it counts the task it created and the drift it found' $true (($created -ge 1) -and ($drifted -ge $created))
    $eventWorker = if ($events.Count -ge 1) { [string]$events[0].workerName } else { 'none' }
    Assert-Step '15 ... written by' 'ReconciliationWorker' $eventWorker

    # The mapping gets an external SKU the channel keeps: its initial sync stores the value, and the runs go quiet.
    $changed = Invoke-ApiProblem PUT "/api/channels/$pos/products/$($mapping.Body.channelProductId)" $manager @{ externalSku = $sku; status = 'Active' } 'en'
    Assert-Step '16 change the external SKU (no SIM-LOST- any more)' 200 $changed.Status
    $stored = if ($changed.Body.syncTaskId) { Wait-Task $changed.Body.syncTaskId $manager 60 } else { $null }
    Assert-Step '17 its initial sync task (stored by the channel now)' 'Succeeded' (Get-TaskStatus $stored)
    $quietFrom = Get-Instant
    Write-Host "Watching for $QuietSeconds s: no reconciliation task may be created for the pair any more..."
    Start-Sleep -Seconds $QuietSeconds
    $later = @(Get-ReconciliationTasks $pos $productId $quietFrom $manager)
    Assert-Step "18 no reconciliation task for the pair during the next $QuietSeconds s" 0 $later.Count

    # Reconciliation only ever read the stock.
    $after = (Invoke-Api GET "/api/products/$productId" $manager).Body
    Assert-Step '19 stock of the product unchanged (total / reserved / available)' '10 / 0 / 10' "$($after.totalStock) / $($after.reservedStock) / $($after.availableStock)"
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # Nothing is left behind that drifts for good, and the smoke account is locked - also after a failed step.
    if ($manager -and $pos -and $mapping -and $mapping.Body -and $mapping.Body.channelProductId) {
        $off = Invoke-ApiProblem PUT "/api/channels/$pos/products/$($mapping.Body.channelProductId)" $manager @{ externalSku = $sku; status = 'Inactive' } 'en'
        Assert-Step '20 deactivate the smoke mapping' 200 $off.Status
    }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '21 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
}
Invoke-FrontendChecks 22
Complete-Smoke
