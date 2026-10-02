# Smoke check for task 014 (channel product mapping and stock sync with the simulated channels: initial sync, retry
# after a temporary failure, permanent failure, manual retry, sync after an order).
# Needs PostgreSQL and RabbitMQ running (README section 1) and the StockSync migration applied (README section 4).
# Uses a running API / Workers host or builds and starts them, and stops what it started.
# The Workers host must run in Development: only there the simulator's SKU rules (SIM-TEMP- / SIM-PERM-) are on.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-014.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

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

function Get-TaskStatus($Task) { if ($Task) { return $Task.status } else { return 'not finished' } }

# Stock-sync tasks of one channel for one correlation id, once all of them are finished (or after the limit).
function Wait-SyncTasks([string]$ChannelId, [string]$CorrelationId, [string]$Token, [int]$Expected, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $items = @()
    while ((Get-Date) -lt $deadline) {
        $page = (Invoke-Api GET "/api/processing/tasks?taskType=StockSync&channelId=$ChannelId&correlationId=$CorrelationId" $Token).Body
        $items = @($page.items)
        $open = @($items | Where-Object { ($_.status -ne 'Succeeded') -and ($_.status -ne 'Failed') }).Count
        if (($items.Count -ge $Expected) -and ($open -eq 0)) { return $items }
        Start-Sleep -Seconds 1
    }
    return $items
}

$admin = $null
$managerUser = $null
$staffUser = $null

try {
    Start-Smoke 'Task 014 - channel product mapping and stock sync (simulated channels)'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Smoke users: a Manager maintains mappings, a Staff enters the order.
    $managerEmail = "smoke-014-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    $staffEmail = "smoke-014-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $staff = Get-Token $staffEmail $password
    $password = $null

    # Data: customer, product, 5 units in stock.
    $customer = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 014-$n"; phone = $null; email = $null; address = $null }
    Assert-Step '03 create smoke customer' 201 $customer.Status
    $product = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-014-$n"; name = "Smoke product 014-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '04 create smoke product' 201 $product.Status
    $productId = $product.Body.productId
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-014-GRN-$n"; items = @(@{ productId = $productId; stock = 5 }) }
    Assert-Step '05 import 5 units' 200 $import.Status

    $channels = (Invoke-Api GET '/api/channels' $staff).Body
    $website = @($channels | Where-Object { $_.slug -eq 'website' })[0].channelId
    $pos = @($channels | Where-Object { $_.slug -eq 'pos' })[0].channelId
    $marketplace = @($channels | Where-Object { $_.slug -eq 'digital-platform' })[0].channelId
    Assert-Step '06 the three seeded channels exist' $true ([bool]$website -and [bool]$pos -and [bool]$marketplace)

    # Website: a normal external SKU. The mapping asks for an initial sync, which succeeds at the first attempt.
    $mapWeb = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = $productId; externalSku = "WEB-SMOKE-$n" } 'en'
    Assert-Step '07 map on website' 201 $mapWeb.Status
    Assert-Step '08 ... with a sync task' $true ([bool]$mapWeb.Body.syncTaskId)
    Write-Host 'Waiting for the Stock Sync Worker (up to 60 s)...'
    $webTask = Wait-Task $mapWeb.Body.syncTaskId $staff 60
    if ($null -eq $webTask) {
        throw 'The first stock-sync task did not finish within 60 s. Is the Workers host running a build of task 014 (README section 6)?'
    }
    Assert-Step '09 website sync task' 'Succeeded' $webTask.status
    Assert-Step '10 ... one attempt by the StockSyncWorker' '1 StockSyncWorker' "$($webTask.attempts.Count) $($webTask.attempts[0].workerName)"
    Assert-Step '11 ... task names channel and product' $true (($webTask.channelId -eq $website) -and ($webTask.productId -eq $productId))

    # POS: SIM-TEMP- fails the first attempt with a temporary error; the retry (5 s tier) succeeds.
    $mapPos = Invoke-ApiProblem POST "/api/channels/$pos/products" $manager @{ productId = $productId; externalSku = "SIM-TEMP-$n" } 'en'
    Assert-Step '12 map on pos (SIM-TEMP-)' 201 $mapPos.Status
    Write-Host 'Waiting for the retry of the pos task (5 s delay tier)...'
    $posTask = Wait-Task $mapPos.Body.syncTaskId $staff 60
    Assert-Step '13 pos sync task' 'Succeeded' (Get-TaskStatus $posTask)
    # @( ) around the whole expression: Windows PowerShell unrolls a one-element array that an "if" returns.
    $posAttempts = @(if ($posTask) { $posTask.attempts })
    if ($posTask -and ($posAttempts.Count -eq 1)) {
        Write-Host 'The pos task succeeded at its first attempt: ChannelSimulation:SkuRules is OFF.' -ForegroundColor Yellow
        Write-Host 'Run the Workers host in Development (appsettings.Development.json switches the SKU rules on).' -ForegroundColor Yellow
    }
    Assert-Step '14 ... after one failed attempt (SkuRules must be on)' 2 $posAttempts.Count
    $firstError = if ($posAttempts.Count -ge 1) { $posAttempts[0].errorCode } else { $null }
    Assert-Step '15 ... which failed with' 'channel_unavailable' $firstError

    # Roles and conflicts.
    $staffMap = Invoke-ApiProblem POST "/api/channels/$marketplace/products" $staff @{ productId = $productId; externalSku = "MP-$n" } 'en'
    Assert-Step '16 staff cannot map' 403 $staffMap.Status
    $duplicate = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = $productId; externalSku = "WEB-OTHER-$n" } 'en'
    Assert-Step '17 product mapped twice on website' 409 $duplicate.Status
    Assert-Step '18 ... errorCode' 'channel_product_exists' $duplicate.Body.errorCode
    $listed = (Invoke-Api GET "/api/channels/$website/products?search=SMOKE-014-$n" $staff).Body
    Assert-Step '19 staff lists the website mapping' 1 $listed.totalCount

    # An order on website (2 units): the reservation changes the stock, so every channel that sells the product is synced.
    $correlationId = "smoke-014-$n"
    $order = @{ externalOrderId = "SMOKE-014-WEB-$n"; customerId = $customer.Body.customerId; items = @(@{ productId = $productId; stock = 2 }) }
    $submitted = Invoke-Api POST '/api/orders/website' $staff $order @{ 'X-Correlation-Id' = $correlationId }
    Assert-Step '20 staff submits an order (website, 2 units)' 202 $submitted.Status
    $orderTask = if ($submitted.Body) { Wait-Task $submitted.Body.taskId $staff 60 } else { $null }
    Assert-Step '21 order task' 'Succeeded' (Get-TaskStatus $orderTask)
    Assert-Step '22 available stock after the order' 3 (Invoke-Api GET "/api/products/$productId" $staff).Body.availableStock

    Write-Host 'Waiting for the stock sync of the order (up to 60 s)...'
    $webSync = @(Wait-SyncTasks $website $correlationId $staff 1 60)
    $posSync = @(Wait-SyncTasks $pos $correlationId $staff 1 60)
    $webSyncStatus = if ($webSync.Count -ge 1) { $webSync[0].status } else { 'none' }
    $posSyncStatus = if ($posSync.Count -ge 1) { $posSync[0].status } else { 'none' }
    Assert-Step '23 order: one sync task on website' "1 Succeeded" "$($webSync.Count) $webSyncStatus"
    Assert-Step '24 order: one sync task on pos' "1 Succeeded" "$($posSync.Count) $posSyncStatus"
    $mpSync = (Invoke-Api GET "/api/processing/tasks?taskType=StockSync&channelId=$marketplace&correlationId=$correlationId" $staff).Body
    Assert-Step '25 order: none on digital-platform (not mapped)' 0 $mpSync.totalCount

    # Digital platform: SIM-PERM- is rejected by the channel: the task fails for good, without a retry.
    $mapMp = Invoke-ApiProblem POST "/api/channels/$marketplace/products" $manager @{ productId = $productId; externalSku = "SIM-PERM-$n" } 'en'
    Assert-Step '26 map on digital-platform (SIM-PERM-)' 201 $mapMp.Status
    $mpTask = Wait-Task $mapMp.Body.syncTaskId $staff 60
    if ($mpTask -and ($mpTask.status -eq 'Succeeded')) {
        Write-Host 'The SIM-PERM- task succeeded: ChannelSimulation:SkuRules is OFF (Workers host not in Development).' -ForegroundColor Yellow
    }
    Assert-Step '27 digital-platform sync task (SkuRules must be on)' 'Failed' (Get-TaskStatus $mpTask)
    $mpAttempts = @(if ($mpTask) { $mpTask.attempts })
    $mpError = if ($mpAttempts.Count -ge 1) { $mpAttempts[0].errorCode } else { $null }
    Assert-Step '28 ... one attempt, rejected by the channel' '1 channel_rejected' "$($mpAttempts.Count) $mpError"

    # The mapping is corrected: a new sync task, which succeeds.
    $fixed = Invoke-ApiProblem PUT "/api/channels/$marketplace/products/$($mapMp.Body.channelProductId)" $manager @{ externalSku = "MP-SMOKE-$n"; status = 'Active' } 'en'
    Assert-Step '29 correct the external SKU' 200 $fixed.Status
    Assert-Step '30 ... requests a new sync task' $true ([bool]$fixed.Body.syncTaskId -and ($fixed.Body.syncTaskId -ne $mapMp.Body.syncTaskId))
    $fixedTask = if ($fixed.Body.syncTaskId) { Wait-Task $fixed.Body.syncTaskId $staff 60 } else { $null }
    Assert-Step '31 ... which succeeds' 'Succeeded' (Get-TaskStatus $fixedTask)

    # Manual retry of the failed task: Staff may not, a Manager may; it now succeeds with a second attempt.
    $staffRetry = Invoke-ApiProblem POST "/api/processing/tasks/$($mapMp.Body.syncTaskId)/retry" $staff $null 'en'
    Assert-Step '32 staff cannot retry' 403 $staffRetry.Status
    $retry = Invoke-ApiProblem POST "/api/processing/tasks/$($mapMp.Body.syncTaskId)/retry" $manager $null 'en'
    Assert-Step '33 manager retries the failed task' 202 $retry.Status
    $retried = Wait-Task $mapMp.Body.syncTaskId $staff 60
    Assert-Step '34 retried task' 'Succeeded' (Get-TaskStatus $retried)
    $retriedAttempts = if ($retried) { @($retried.attempts).Count } else { 0 }
    Assert-Step '35 ... with a second attempt' 2 $retriedAttempts
    $retriedEvent = if ($retried -and $retried.message) { $retried.message.eventType } else { $null }
    Assert-Step '36 ... sent as a directed message' 'stock.sync.requested' $retriedEvent
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke accounts are locked again, also after a failed step.
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '37 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if ($admin -and $staffUser -and $staffUser.Body) {
        Assert-Step '38 lock the smoke staff' 200 (Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin).Status
    }
}
Complete-Smoke
