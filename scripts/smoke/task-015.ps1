# Smoke check for task 015 (realtime hub: authentication of negotiate; dashboard summary before and after an order).
# Needs PostgreSQL and RabbitMQ running (README section 1). No new migration.
# Uses a running API / Workers host or builds and starts them, and stops what it started.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-015.ps1
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

function Get-ChannelOrders($Summary, [string]$Slug) { return @($Summary.orders.byChannel | Where-Object { $_.slug -eq $Slug })[0] }
function Get-Processing($Summary, [string]$TaskType) { return @($Summary.processing | Where-Object { $_.taskType -eq $TaskType })[0] }

$admin = $null
$managerUser = $null
$staffUser = $null
$hub = '/api/hubs/notifications/negotiate?negotiateVersion=1'

try {
    Start-Smoke 'Task 015 - realtime hub and dashboard summary'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Smoke users: a Manager prepares the data (stock import), a Staff uses the hub, the summary and the order entry.
    $managerEmail = "smoke-015-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    $staffEmail = "smoke-015-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $staff = Get-Token $staffEmail $password
    $password = $null

    # The hub: negotiate is refused without a token; the token is accepted as header and, for the hub only, in the query.
    Assert-Step '03 hub negotiate without a token' 401 (Invoke-Api POST $hub $null).Status
    Assert-Step '04 hub negotiate with the token in the header' 200 (Invoke-Api POST $hub $staff).Status
    $queryNegotiate = Invoke-Api POST ($hub + '&access_token=' + $staff) $null
    Assert-Step '05 hub negotiate with the token in the query string' 200 $queryNegotiate.Status
    Assert-Step '06 ... answers with a connection' $true ([bool]$queryNegotiate.Body.connectionToken)
    Assert-Step '07 another path ignores a token in the query string' 401 (Invoke-Api GET ('/api/dashboard/summary?access_token=' + $staff) $null).Status

    # Summary before.
    $before = Invoke-Api GET '/api/dashboard/summary' $staff
    Assert-Step '08 staff reads the summary' 200 $before.Status
    Assert-Step '09 ... default range is 24 hours' 24 ([int]([datetimeoffset]$before.Body.to - [datetimeoffset]$before.Body.from).TotalHours)
    Assert-Step '10 ... lists the three stock-sync channels' 3 @($before.Body.stockSync).Count
    $webBefore = Get-ChannelOrders $before.Body 'website'
    $orderTasksBefore = Get-Processing $before.Body 'OrderProcessing'

    # One order on website: product, 5 units in stock, 2 ordered.
    $customer = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 015-$n"; phone = $null; email = $null; address = $null }
    Assert-Step '11 create smoke customer' 201 $customer.Status
    $product = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-015-$n"; name = "Smoke product 015-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '12 create smoke product' 201 $product.Status
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-015-GRN-$n"; items = @(@{ productId = $product.Body.productId; stock = 5 }) }
    Assert-Step '13 import 5 units' 200 $import.Status
    $order = @{ externalOrderId = "SMOKE-015-WEB-$n"; customerId = $customer.Body.customerId; items = @(@{ productId = $product.Body.productId; stock = 2 }) }
    $submitted = Invoke-Api POST '/api/orders/website' $staff $order
    Assert-Step '14 staff submits an order (website, 2 units)' 202 $submitted.Status
    Write-Host 'Waiting for the Order Worker (up to 60 s)...'
    $task = if ($submitted.Body) { Wait-Task $submitted.Body.taskId $staff 60 } else { $null }
    if ($null -eq $task) {
        throw 'The order did not reach a terminal state within 60 s. Is the Workers host running (README section 6)?'
    }
    Assert-Step '15 order task' 'Succeeded' $task.status

    # Summary after: the order is counted, the task is counted as succeeded, nothing failed in the outbox.
    $after = Invoke-Api GET '/api/dashboard/summary' $staff
    Assert-Step '16 summary after the order' 200 $after.Status
    $webAfter = Get-ChannelOrders $after.Body 'website'
    $orderTasksAfter = Get-Processing $after.Body 'OrderProcessing'
    Assert-Step '17 orders.total grew by 1' ($before.Body.orders.total + 1) $after.Body.orders.total
    Assert-Step '18 website: total grew by 1' ($webBefore.total + 1) $webAfter.total
    Assert-Step '19 website: reserved grew by 1' ($webBefore.reserved + 1) $webAfter.reserved
    Assert-Step '20 sales amount grew by 2 x 15000' ([decimal]$before.Body.orders.salesAmount + 30000) ([decimal]$after.Body.orders.salesAmount)
    Assert-Step '21 OrderProcessing succeeded grew' $true ($orderTasksAfter.succeeded -ge ($orderTasksBefore.succeeded + 1))
    Assert-Step '22 outbox.failed unchanged' $before.Body.outbox.failed $after.Body.outbox.failed
    Assert-Step '23 products grew by 1' ($before.Body.inventory.products + 1) $after.Body.inventory.products

    # An invalid range is a 400 on its field.
    $invalid = Invoke-ApiProblem GET '/api/dashboard/summary?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z' $staff $null 'en'
    Assert-Step '24 from after to' 400 $invalid.Status
    Assert-Step '25 ... reported on "to"' $true ([bool]$invalid.Body.errors.to)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke accounts are locked again, also after a failed step.
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '26 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if ($admin -and $staffUser -and $staffUser.Body) {
        Assert-Step '27 lock the smoke staff' 200 (Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin).Status
    }
}
Complete-Smoke
