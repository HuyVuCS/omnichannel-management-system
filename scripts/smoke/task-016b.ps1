# Smoke check for task 016b (stockSyncEnabled on channels; the mapping API as the channel detail page uses it: create,
# change, deactivate, search; frontend build and tests).
# Needs PostgreSQL and RabbitMQ running (docs/operations-guide.md section 1). No new migration.
# Uses a running API / Workers host or - for the local default only - builds and starts them, and stops what it started.
# The smoke mapping ends Inactive, also after a failed step; the mappings of real products are never touched.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-016b.ps1
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

# True when the JSON object has the member at all (a member with the value null counts).
function Test-Member($Object, [string]$Name) { return ($null -ne $Object) -and ($Object.PSObject.Properties.Name -contains $Name) }

# The stockSyncEnabled value of the channel with this slug as text: 'True', 'False', or 'missing' when the channel or
# the member is not there. (Text on purpose: PowerShell treats every non-empty string as equal to $true.)
function Get-StockSync($Channels, [string]$Slug) {
    $channel = @($Channels | Where-Object { $_.slug -eq $Slug })[0]
    if (-not (Test-Member $channel 'stockSyncEnabled')) { return 'missing' }
    return "$($channel.stockSyncEnabled)"
}

$admin = $null
$manager = $null
$managerUser = $null
$staffUser = $null
$website = $null
$mapping = $null
$deactivated = $false

try {
    Start-Smoke 'Task 016b - stockSyncEnabled, channel product mappings, frontend build and tests'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Smoke users: a Manager maintains the mapping, a Staff reads.
    $managerEmail = "smoke-016b-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    $staffEmail = "smoke-016b-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $staff = Get-Token $staffEmail $password
    $password = $null

    # The three seeded channels are synced, as the list and a single channel say it.
    $channels = (Invoke-Api GET '/api/channels' $staff).Body
    Assert-Step '03 website has stockSyncEnabled' 'True' (Get-StockSync $channels 'website')
    Assert-Step '04 pos has stockSyncEnabled' 'True' (Get-StockSync $channels 'pos')
    Assert-Step '05 digital-platform has stockSyncEnabled' 'True' (Get-StockSync $channels 'digital-platform')
    $website = @($channels | Where-Object { $_.slug -eq 'website' })[0].channelId
    $single = Invoke-Api GET "/api/channels/$website" $staff
    Assert-Step '06 staff reads the website channel' 200 $single.Status
    Assert-Step '07 ... with stockSyncEnabled' 'True' (Get-StockSync @($single.Body) 'website')

    # Data: product, 5 units in stock.
    $sku = "SMOKE-016B-$n"
    $product = Invoke-Api POST '/api/products' $manager @{ sku = $sku; name = "Smoke product 016b-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '08 create smoke product' 201 $product.Status
    $productId = $product.Body.productId
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-016B-GRN-$n"; items = @(@{ productId = $productId; stock = 5 }) }
    Assert-Step '09 import 5 units' 200 $import.Status

    # Staff may not map; the Manager maps the product on website: a sync task, which succeeds.
    $staffMap = Invoke-ApiProblem POST "/api/channels/$website/products" $staff @{ productId = $productId; externalSku = "WEB-SMOKE-016B-$n" } 'en'
    Assert-Step '10 staff cannot map a product' 403 $staffMap.Status
    $mapping = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = $productId; externalSku = "WEB-SMOKE-016B-$n" } 'en'
    Assert-Step '11 manager maps the product on website' 201 $mapping.Status
    Assert-Step '12 ... with a sync task' $true ([bool]$mapping.Body.syncTaskId)
    $mappingPath = "/api/channels/$website/products/$($mapping.Body.channelProductId)"
    Write-Host 'Waiting for the Stock Sync Worker (up to 60 s)...'
    $firstTask = Wait-Task $mapping.Body.syncTaskId $staff 60
    if ($null -eq $firstTask) {
        throw 'The stock-sync task did not finish within 60 s. Is the Workers host running (docs/operations-guide.md section 6)?'
    }
    Assert-Step '13 sync task' 'Succeeded' $firstTask.status

    # A changed external SKU has to reach the channel: a new sync task.
    $changed = Invoke-ApiProblem PUT $mappingPath $manager @{ externalSku = "WEB-SMOKE-016B-$n-B"; status = 'Active' } 'en'
    Assert-Step '14 manager changes the external SKU' 200 $changed.Status
    Assert-Step '15 ... which starts a new sync task' $true ([bool]$changed.Body.syncTaskId -and ($changed.Body.syncTaskId -ne $mapping.Body.syncTaskId))
    $secondTask = if ($changed.Body.syncTaskId) { Wait-Task $changed.Body.syncTaskId $staff 60 } else { $null }
    Assert-Step '16 second sync task' 'Succeeded' (Get-TaskStatus $secondTask)

    # Deactivating sends nothing to the channel: no task.
    $off = Invoke-ApiProblem PUT $mappingPath $manager @{ externalSku = "WEB-SMOKE-016B-$n-B"; status = 'Inactive' } 'en'
    Assert-Step '17 manager deactivates the mapping' 200 $off.Status
    $deactivated = ($off.Status -eq 200)
    Assert-Step '18 ... answer has the member syncTaskId' $true (Test-Member $off.Body 'syncTaskId')
    Assert-Step '19 ... which is null' $true ($null -eq $off.Body.syncTaskId)

    # What the mapping list of the page shows for a search by the SKU.
    $listed = (Invoke-Api GET "/api/channels/$website/products?search=$sku" $staff).Body
    Assert-Step '20 staff finds the mapping by its SKU' 1 $listed.totalCount
    $listedStatus = if (@($listed.items).Count -ge 1) { @($listed.items)[0].status } else { 'none' }
    Assert-Step '21 ... and it is Inactive' 'Inactive' $listedStatus
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke mapping never stays Active and the smoke accounts are locked again, also after a failed step.
    if ($manager -and $website -and $mapping -and $mapping.Body -and $mapping.Body.channelProductId -and (-not $deactivated)) {
        $cleanup = Invoke-ApiProblem PUT "/api/channels/$website/products/$($mapping.Body.channelProductId)" $manager @{ externalSku = "WEB-SMOKE-016B-$n"; status = 'Inactive' } 'en'
        Assert-Step '22 deactivate the smoke mapping (clean-up)' 200 $cleanup.Status
    }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '23 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if ($admin -and $staffUser -and $staffUser.Body) {
        Assert-Step '24 lock the smoke staff' 200 (Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin).Status
    }
}
# Frontend: production build and tests (steps 25-27), after the hosts this script started are stopped.
Invoke-FrontendChecks 25 @('@microsoft\signalr')
Complete-Smoke
