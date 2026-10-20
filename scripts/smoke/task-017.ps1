# Smoke check for task 017 (alerts: low stock, insufficient stock, stock-sync failure; acknowledge; dashboard summary).
# Needs PostgreSQL and RabbitMQ running (docs/operations-guide.md section 1) and the Alerts migration applied (docs/operations-guide.md section 4).
# Uses a running API / Workers host or - for the local default only - builds and starts them, and stops what it started.
# The Workers host must run in Development: only there the simulator's SKU rule SIM-PERM- is on.
# The smoke mapping ends Inactive and the smoke accounts are locked, also after a failed step.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-017.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

# Asks for the task once per second until it is Succeeded or Failed; returns the last answer (or $null after the limit).
function Wait-Task([string]$TaskId, [string]$Token, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $task = (Invoke-Api GET "/api/processing/tasks/$TaskId" $Token).Body
        if ($task -and (($task.status -ceq 'Succeeded') -or ($task.status -ceq 'Failed'))) { return $task }
        Start-Sleep -Seconds 1
    }
    return $null
}

function Get-TaskStatus($Task) { if ($Task) { return [string]$Task.status } else { return 'not finished' } }

# The alerts of one type about a product, resolved ones included, most recent occurrence first. Always an array.
function Get-ProductAlerts([string]$Token, [string]$ProductId, [string]$Type) {
    $r = Invoke-Api GET "/api/alerts?productId=$ProductId&type=$Type&status=Open&status=Acknowledged&status=Resolved&pageSize=100" $Token
    if ($r.Status -ne 200) { return , @() }
    return , @($r.Body.items)
}

# The status of the most recent alert of the list, or 'none'.
function Get-NewestStatus($Alerts) { if (@($Alerts).Count -ge 1) { return [string]@($Alerts)[0].status } else { return 'none' } }

# How many alerts with this status exist (the count of the list, not of one page); -1 when the list cannot be read.
function Get-AlertCount([string]$Token, [string]$Status) {
    $r = Invoke-Api GET "/api/alerts?status=$Status&pageSize=1" $Token
    if ($r.Status -ne 200) { return -1 }
    return [int]$r.Body.totalCount
}

$admin = $null
$manager = $null
$managerUser = $null
$staffUser = $null
$website = $null
$mapping = $null
$deactivated = $false

try {
    Start-Smoke 'Task 017 - alerts: low stock, insufficient stock, stock-sync failure, acknowledge, summary'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Smoke users: a Manager maintains data and acknowledges, a Staff enters orders and reads.
    $managerEmail = "smoke-017-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    $staffEmail = "smoke-017-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $staff = Get-Token $staffEmail $password
    $password = $null

    $customer = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 017-$n"; phone = $null; email = $null; address = $null }
    Assert-Step '03 create smoke customer' 201 $customer.Status

    # Low stock: a new product has no stock, so it is low from the start.
    $product = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-017-$n"; name = "Smoke product 017-$n"; price = 15000; lowStockThreshold = 5 }
    Assert-Step '04 create smoke product (threshold 5)' 201 $product.Status
    $productId = $product.Body.productId
    $low = Get-ProductAlerts $staff $productId 'LowStock'
    Assert-Step '05 new product: one LowStock alert' 1 @($low).Count
    Assert-Step '06 ... which is Open' 'Open' (Get-NewestStatus $low)
    $lowNumbers = if (@($low).Count -ge 1) { "$($low[0].availableStock) $($low[0].lowStockThreshold)" } else { 'none' }
    Assert-Step '07 ... with the current stock and the threshold' '0 5' $lowNumbers
    $firstLowId = if (@($low).Count -ge 1) { [string]$low[0].alertId } else { 'none' }

    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-017-GRN1-$n"; items = @(@{ productId = $productId; stock = 10 }) }
    Assert-Step '08 import 10 units (above the threshold)' 200 $import.Status
    Assert-Step '09 the LowStock alert is Resolved' 'Resolved' (Get-NewestStatus (Get-ProductAlerts $staff $productId 'LowStock'))

    # An order of 6 leaves 4 (<= 5): low again, as a new alert.
    $order = @{ externalOrderId = "SMOKE-017-WEB1-$n"; customerId = $customer.Body.customerId; items = @(@{ productId = $productId; stock = 6 }) }
    $submitted = Invoke-Api POST '/api/orders/website' $staff $order
    Assert-Step '10 staff submits an order (website, 6 units)' 202 $submitted.Status
    Write-Host 'Waiting for the Order Worker (up to 60 s)...'
    $orderTask = if ($submitted.Body) { Wait-Task $submitted.Body.taskId $staff 60 } else { $null }
    if ($null -eq $orderTask) {
        throw 'The order did not reach a terminal state within 60 s. Is the Workers host running a build of task 017 (docs/operations-guide.md section 6)?'
    }
    Assert-Step '11 order task' 'Succeeded' (Get-TaskStatus $orderTask)
    $low = Get-ProductAlerts $staff $productId 'LowStock'
    Assert-Step '12 the reservation opened a second LowStock alert' 2 @($low).Count
    Assert-Step '13 ... which is Open' 'Open' (Get-NewestStatus $low)
    $lowId = if (@($low).Count -ge 1) { [string]$low[0].alertId } else { 'none' }
    Assert-Step '14 ... and is not the first alert again' $true ($lowId -cne $firstLowId)

    # Acknowledge: Manager or Admin only; it does not resolve.
    Assert-Step '15 staff cannot acknowledge' 403 (Invoke-ApiProblem POST "/api/alerts/$lowId/acknowledge" $staff $null 'en').Status
    Assert-Step '16 ... the alert is still Open' 'Open' ([string](Invoke-Api GET "/api/alerts/$lowId" $staff).Body.status)
    $ack = Invoke-ApiProblem POST "/api/alerts/$lowId/acknowledge" $manager $null 'en'
    Assert-Step '17 manager acknowledges' 200 $ack.Status
    Assert-Step '18 ... the alert is Acknowledged' 'Acknowledged' ([string]$ack.Body.status)
    Assert-Step '19 ... by the smoke manager' $managerEmail ([string]$ack.Body.acknowledgedByEmail)
    $again = Invoke-ApiProblem POST "/api/alerts/$lowId/acknowledge" $manager $null 'en'
    Assert-Step '20 acknowledge again' 200 $again.Status
    Assert-Step '21 ... changes nothing (same acknowledgedAt)' $true ("$($again.Body.acknowledgedAt)" -ceq "$($ack.Body.acknowledgedAt)")

    # Dashboard summary: the same numbers as the alert list.
    $summary = (Invoke-Api GET '/api/dashboard/summary' $staff).Body
    $summaryOpen = if ($summary -and $summary.alerts) { [int]$summary.alerts.open } else { -2 }
    $summaryAcknowledged = if ($summary -and $summary.alerts) { [int]$summary.alerts.acknowledged } else { -2 }
    Assert-Step '22 summary: alerts.acknowledged = acknowledged alerts in the list' (Get-AlertCount $staff 'Acknowledged') $summaryAcknowledged
    Assert-Step '23 ... and counts the smoke alert' $true ($summaryAcknowledged -ge 1)
    Assert-Step '24 summary: alerts.open = open alerts in the list' (Get-AlertCount $staff 'Open') $summaryOpen
    $summaryTypes = if ($summary -and $summary.alerts) { (@($summary.alerts.byType | ForEach-Object { $_.type }) -join ',') } else { 'none' }
    Assert-Step '25 summary: every alert type listed' 'LowStock,InsufficientStock,StockSyncFailed,ProcessingFailed' $summaryTypes

    # Stock arrives (4 + 10 = 14 > 5): the system resolves the acknowledged alert; a resolved alert cannot be acknowledged.
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-017-GRN2-$n"; items = @(@{ productId = $productId; stock = 10 }) }
    Assert-Step '26 import 10 more units' 200 $import.Status
    Assert-Step '27 the acknowledged alert is Resolved' 'Resolved' ([string](Invoke-Api GET "/api/alerts/$lowId" $staff).Body.status)
    $late = Invoke-ApiProblem POST "/api/alerts/$lowId/acknowledge" $manager $null 'en'
    Assert-Step '28 acknowledge a resolved alert' 409 $late.Status
    Assert-Step '29 ... errorCode' 'alert_resolved' ([string]$late.Body.errorCode)

    # Insufficient stock: an order of 100 (14 available) is recorded without a reservation.
    $bigOrder = @{ externalOrderId = "SMOKE-017-WEB2-$n"; customerId = $customer.Body.customerId; items = @(@{ productId = $productId; stock = 100 }) }
    $submittedBig = Invoke-Api POST '/api/orders/website' $staff $bigOrder
    Assert-Step '30 staff submits an order of 100 units' 202 $submittedBig.Status
    $bigTask = if ($submittedBig.Body) { Wait-Task $submittedBig.Body.taskId $staff 60 } else { $null }
    Assert-Step '31 order task' 'Succeeded' (Get-TaskStatus $bigTask)
    $bigOrderId = if ($bigTask) { [string]$bigTask.orderId } else { 'no order' }
    $bigOrderStatus = if ($bigTask -and $bigTask.orderId) { [string](Invoke-Api GET "/api/orders/$bigOrderId" $staff).Body.status } else { 'no order' }
    Assert-Step '32 ... order recorded as InsufficientStock' 'InsufficientStock' $bigOrderStatus
    $insufficient = Get-ProductAlerts $staff $productId 'InsufficientStock'
    Assert-Step '33 InsufficientStock alert' 'Open' (Get-NewestStatus $insufficient)
    $insufficientOrder = if (@($insufficient).Count -ge 1) { [string]$insufficient[0].orderId } else { 'none' }
    Assert-Step '34 ... names the order' $bigOrderId $insufficientOrder
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-017-GRN3-$n"; items = @(@{ productId = $productId; stock = 1 }) }
    Assert-Step '35 import of the product' 200 $import.Status
    Assert-Step '36 ... resolves the InsufficientStock alert' 'Resolved' (Get-NewestStatus (Get-ProductAlerts $staff $productId 'InsufficientStock'))

    # Stock-sync failure: SIM-PERM- is rejected by the simulated channel; a corrected mapping syncs and ends the alert.
    $channels = (Invoke-Api GET '/api/channels' $staff).Body
    $website = [string]@($channels | Where-Object { $_.slug -ceq 'website' })[0].channelId
    Assert-Step '37 the website channel exists' $true ($website.Length -gt 0)
    $mapping = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = $productId; externalSku = "SIM-PERM-017-$n" } 'en'
    Assert-Step '38 map the product on website (SIM-PERM-)' 201 $mapping.Status
    $mappingPath = "/api/channels/$website/products/$($mapping.Body.channelProductId)"
    Write-Host 'Waiting for the Stock Sync Worker (up to 60 s)...'
    $failedTask = if ($mapping.Body.syncTaskId) { Wait-Task $mapping.Body.syncTaskId $staff 60 } else { $null }
    if ($failedTask -and ($failedTask.status -ceq 'Succeeded')) {
        Write-Host 'The SIM-PERM- task succeeded: ChannelSimulation:SkuRules is OFF (Workers host not in Development).' -ForegroundColor Yellow
    }
    Assert-Step '39 its sync task (SkuRules must be on)' 'Failed' (Get-TaskStatus $failedTask)
    $syncAlerts = Get-ProductAlerts $staff $productId 'StockSyncFailed'
    Assert-Step '40 StockSyncFailed alert' 'Open' (Get-NewestStatus $syncAlerts)
    $syncAlert = if (@($syncAlerts).Count -ge 1) { $syncAlerts[0] } else { $null }
    Assert-Step '41 ... names the channel' $website $(if ($syncAlert) { [string]$syncAlert.channelId } else { 'none' })
    Assert-Step '42 ... the failed task' ([string]$mapping.Body.syncTaskId) $(if ($syncAlert) { [string]$syncAlert.taskId } else { 'none' })
    Assert-Step '43 ... and its error code' 'channel_rejected' $(if ($syncAlert) { [string]$syncAlert.errorCode } else { 'none' })

    $fixed = Invoke-ApiProblem PUT $mappingPath $manager @{ externalSku = "WEB-SMOKE-017-$n"; status = 'Active' } 'en'
    Assert-Step '44 correct the external SKU' 200 $fixed.Status
    $fixedTask = if ($fixed.Body.syncTaskId) { Wait-Task $fixed.Body.syncTaskId $staff 60 } else { $null }
    Assert-Step '45 the new sync task' 'Succeeded' (Get-TaskStatus $fixedTask)
    Assert-Step '46 ... resolves the StockSyncFailed alert' 'Resolved' (Get-NewestStatus (Get-ProductAlerts $staff $productId 'StockSyncFailed'))

    # The default list shows what needs attention: nothing of this product any more. Resolved rows are kept: two low
    # stock, one insufficient stock, one stock sync.
    $default = Invoke-Api GET "/api/alerts?productId=$productId" $staff
    Assert-Step '47 default list of the product: resolved alerts hidden' 0 ([int]$default.Body.totalCount)
    $resolved = Invoke-Api GET "/api/alerts?productId=$productId&status=Resolved" $staff
    Assert-Step '48 ... and listed when asked for' 4 ([int]$resolved.Body.totalCount)

    $unknown = Invoke-ApiProblem GET '/api/alerts/00000000-0000-0000-0000-000000000000' $staff $null 'en'
    Assert-Step '49 unknown alert' 404 $unknown.Status
    Assert-Step '50 ... errorCode' 'alert_not_found' ([string]$unknown.Body.errorCode)

    $off = Invoke-ApiProblem PUT $mappingPath $manager @{ externalSku = "WEB-SMOKE-017-$n"; status = 'Inactive' } 'en'
    Assert-Step '51 deactivate the smoke mapping' 200 $off.Status
    $deactivated = ($off.Status -eq 200)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke mapping never stays Active and the smoke accounts are locked again, also after a failed step.
    if ($manager -and $website -and $mapping -and $mapping.Body -and $mapping.Body.channelProductId -and (-not $deactivated)) {
        $cleanup = Invoke-ApiProblem PUT "/api/channels/$website/products/$($mapping.Body.channelProductId)" $manager @{ externalSku = "WEB-SMOKE-017-$n"; status = 'Inactive' } 'en'
        Assert-Step '52 deactivate the smoke mapping (clean-up)' 200 $cleanup.Status
    }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '53 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if ($admin -and $staffUser -and $staffUser.Body) {
        Assert-Step '54 lock the smoke staff' 200 (Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin).Status
    }
}
Complete-Smoke
