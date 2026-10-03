# Smoke check for task 018 (alerts page: the API flows behind it; a deactivated mapping resolves its stock-sync alert).
# Needs PostgreSQL and RabbitMQ running (README section 1) and the Alerts migration applied (README section 4).
# Uses a running API / Workers host or - for the local default only - builds and starts them, and stops what it started.
# The Workers host must run in Development: only there the simulator's SKU rule SIM-PERM- is on.
# The smoke mapping ends Inactive and the smoke accounts are locked, also after a failed step.
# The frontend build and tests run last, after the hosts this script started were stopped.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-018.ps1
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

# The alerts of one type about a product as the alerts page asks for them. Without $Statuses the request carries no
# status: the server's default (what is unresolved). Always an array, most recent occurrence first.
function Get-ProductAlerts([string]$Token, [string]$ProductId, [string]$Type, [string[]]$Statuses = @()) {
    $query = "productId=$ProductId&type=$Type&pageSize=100"
    foreach ($status in $Statuses) { $query += "&status=$status" }
    $r = Invoke-Api GET "/api/alerts?$query" $Token
    if ($r.Status -ne 200) { return , @() }
    return , @($r.Body.items)
}

$AllStatuses = @('Open', 'Acknowledged', 'Resolved')

# The status of the most recent alert of the list, or 'none'.
function Get-NewestStatus($Alerts) { if (@($Alerts).Count -ge 1) { return [string]@($Alerts)[0].status } else { return 'none' } }

$admin = $null
$manager = $null
$managerUser = $null
$staffUser = $null
$website = $null
$mapping = $null
$mappingSku = $null
$deactivated = $false

try {
    Start-Smoke 'Task 018 - alerts page flows: low stock, stock-sync failure, deactivated mapping resolves its alert'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Smoke users: a Manager maintains data, a Staff reads (the alerts page is for every role).
    $managerEmail = "smoke-018-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    $staffEmail = "smoke-018-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $staff = Get-Token $staffEmail $password
    $password = $null

    # Low stock: a new product has no stock. The default list (no status in the request) shows it as Open.
    $product = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-018-$n"; name = "Smoke product 018-$n"; price = 15000; lowStockThreshold = 5 }
    Assert-Step '03 create smoke product (threshold 5)' 201 $product.Status
    $productId = $product.Body.productId
    $low = Get-ProductAlerts $staff $productId 'LowStock'
    Assert-Step '04 default list of the product: one LowStock alert' 1 @($low).Count
    Assert-Step '05 ... which is Open' 'Open' (Get-NewestStatus $low)
    $lowNumbers = if (@($low).Count -ge 1) { "$($low[0].availableStock) $($low[0].lowStockThreshold)" } else { 'none' }
    Assert-Step '06 ... with the current stock and the threshold' '0 5' $lowNumbers

    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-018-GRN1-$n"; items = @(@{ productId = $productId; stock = 10 }) }
    Assert-Step '07 import 10 units (above the threshold)' 200 $import.Status
    # (The helper's answer goes into a variable first: wrapped directly in @(), an empty list would count as one item.)
    $low = Get-ProductAlerts $staff $productId 'LowStock'
    Assert-Step '08 the alert left the default list' 0 @($low).Count
    Assert-Step '09 ... and is Resolved' 'Resolved' (Get-NewestStatus (Get-ProductAlerts $staff $productId 'LowStock' $AllStatuses))

    # Stock-sync failure: SIM-PERM- is rejected by the simulated channel.
    $channels = (Invoke-Api GET '/api/channels' $staff).Body
    $website = [string]@($channels | Where-Object { $_.slug -ceq 'website' })[0].channelId
    Assert-Step '10 the website channel exists' $true ($website.Length -gt 0)
    $mappingSku = "SIM-PERM-018-$n"
    $mapping = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = $productId; externalSku = $mappingSku } 'en'
    Assert-Step '11 map the product on website (SIM-PERM-)' 201 $mapping.Status
    $mappingPath = "/api/channels/$website/products/$($mapping.Body.channelProductId)"
    Write-Host 'Waiting for the Stock Sync Worker (up to 60 s)...'
    $failedTask = if ($mapping.Body.syncTaskId) { Wait-Task $mapping.Body.syncTaskId $staff 60 } else { $null }
    if ($null -eq $failedTask) {
        throw 'The sync task did not reach a terminal state within 60 s. Is the Workers host running a build of task 018 (README section 6)?'
    }
    if ($failedTask.status -ceq 'Succeeded') {
        Write-Host 'The SIM-PERM- task succeeded: ChannelSimulation:SkuRules is OFF (Workers host not in Development).' -ForegroundColor Yellow
    }
    Assert-Step '12 its sync task (SkuRules must be on)' 'Failed' (Get-TaskStatus $failedTask)
    $syncAlerts = Get-ProductAlerts $staff $productId 'StockSyncFailed'
    Assert-Step '13 default list: one StockSyncFailed alert' 1 @($syncAlerts).Count
    Assert-Step '14 ... which is Open' 'Open' (Get-NewestStatus $syncAlerts)
    $syncAlert = if (@($syncAlerts).Count -ge 1) { $syncAlerts[0] } else { $null }
    $firstSyncAlertId = if ($syncAlert) { [string]$syncAlert.alertId } else { 'none' }
    Assert-Step '15 ... names the channel' $website $(if ($syncAlert) { [string]$syncAlert.channelId } else { 'none' })
    Assert-Step '16 ... the failed task' ([string]$mapping.Body.syncTaskId) $(if ($syncAlert) { [string]$syncAlert.taskId } else { 'none' })
    Assert-Step '17 ... and its error code' 'channel_rejected' $(if ($syncAlert) { [string]$syncAlert.errorCode } else { 'none' })

    # Part A6: switching the mapping off resolves the alert of its (channel, product) - nothing was synced for that.
    Assert-Step '18 staff cannot change the mapping' 403 (Invoke-ApiProblem PUT $mappingPath $staff @{ externalSku = $mappingSku; status = 'Inactive' } 'en').Status
    Assert-Step '19 ... the alert is still Open' 'Open' ([string](Invoke-Api GET "/api/alerts/$firstSyncAlertId" $staff).Body.status)
    $off = Invoke-ApiProblem PUT $mappingPath $manager @{ externalSku = $mappingSku; status = 'Inactive' } 'en'
    Assert-Step '20 manager sets the mapping Inactive' 200 $off.Status
    $deactivated = ($off.Status -eq 200)
    Assert-Step '21 ... no stock sync was started' $true ($null -eq $off.Body.syncTaskId)
    $afterOff = (Invoke-Api GET "/api/alerts/$firstSyncAlertId" $staff).Body
    Assert-Step '22 ... the StockSyncFailed alert is Resolved' 'Resolved' ([string]$afterOff.status)
    Assert-Step '23 ... with a resolution time' $true ([bool]$afterOff.resolvedAt)
    $syncAlerts = Get-ProductAlerts $staff $productId 'StockSyncFailed'
    Assert-Step '24 ... and left the default list' 0 @($syncAlerts).Count

    # Re-activating starts the initial sync again; with a SKU the channel accepts it succeeds and opens no alert.
    $mappingSku = "WEB-SMOKE-018-$n"
    $on = Invoke-ApiProblem PUT $mappingPath $manager @{ externalSku = $mappingSku; status = 'Active' } 'en'
    Assert-Step '25 re-activate with a normal SKU' 200 $on.Status
    $deactivated = -not ($on.Status -eq 200)
    Assert-Step '26 ... a stock sync was started' $true ([bool]$on.Body.syncTaskId)
    $okTask = if ($on.Body.syncTaskId) { Wait-Task $on.Body.syncTaskId $staff 60 } else { $null }
    Assert-Step '27 the new sync task' 'Succeeded' (Get-TaskStatus $okTask)
    $syncAlerts = Get-ProductAlerts $staff $productId 'StockSyncFailed'
    Assert-Step '28 no unresolved StockSyncFailed alert for the pair' 0 @($syncAlerts).Count
    $allSync = Get-ProductAlerts $staff $productId 'StockSyncFailed' $AllStatuses
    Assert-Step '29 the resolved alert is kept, and it is the only one' 1 @($allSync).Count
    Assert-Step '30 ... still Resolved' 'Resolved' (Get-NewestStatus $allSync)

    $end = Invoke-ApiProblem PUT $mappingPath $manager @{ externalSku = $mappingSku; status = 'Inactive' } 'en'
    Assert-Step '31 set the smoke mapping Inactive again' 200 $end.Status
    $deactivated = ($end.Status -eq 200)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke mapping never stays Active and the smoke accounts are locked again, also after a failed step.
    if ($manager -and $website -and $mapping -and $mapping.Body -and $mapping.Body.channelProductId -and (-not $deactivated)) {
        $cleanup = Invoke-ApiProblem PUT "/api/channels/$website/products/$($mapping.Body.channelProductId)" $manager @{ externalSku = $mappingSku; status = 'Inactive' } 'en'
        Assert-Step '32 deactivate the smoke mapping (clean-up)' 200 $cleanup.Status
    }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '33 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if ($admin -and $staffUser -and $staffUser.Body) {
        Assert-Step '34 lock the smoke staff' 200 (Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin).Status
    }
}
Invoke-FrontendChecks 35
Complete-Smoke
