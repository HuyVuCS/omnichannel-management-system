# Smoke check for task 013 (order entry end to end: intake, idempotency, Order Worker reservation, orders list and
# detail, movements of an order; frontend build and tests).
# Needs PostgreSQL, RabbitMQ and the Workers host running (docs/operations-guide.md sections 1 and 6) and Node.js (see frontend/.nvmrc);
# starts the API if needed. The Workers host is NOT started by this script.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-013.ps1
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

$admin = $null
$managerUser = $null
$staffUser = $null

try {
    Start-Smoke 'Task 013 - order entry, Order Worker, orders list and detail, frontend'
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # A smoke Manager prepares the data: customer, product (price 15000), 5 units in stock.
    $managerEmail = "smoke-013-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    $customer = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 013-$n"; phone = $null; email = $null; address = $null }
    Assert-Step '02 create smoke customer' 201 $customer.Status
    $customerId = $customer.Body.customerId

    $price = 15000
    $product = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-013-$n"; name = "Smoke product 013-$n"; price = $price; lowStockThreshold = 0 }
    Assert-Step '03 create smoke product (price 15000)' 201 $product.Status
    $productId = $product.Body.productId

    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-013-GRN-$n"; items = @(@{ productId = $productId; stock = 5 }) }
    Assert-Step '04 import 5 units' 200 $import.Status

    # A smoke Staff enters the orders: every role may use the order pages.
    $staffEmail = "smoke-013-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '05 create smoke staff' 201 $staffUser.Status
    $staff = Get-Token $staffEmail $password
    $password = $null

    # First order: website, 3 units. Accepted for asynchronous processing.
    $externalId = "SMOKE-013-WEB-$n"
    $order = @{ externalOrderId = $externalId; customerId = $customerId; items = @(@{ productId = $productId; stock = 3 }) }
    $first = Invoke-ApiProblem POST '/api/orders/website' $staff $order 'en'
    Assert-Step '06 submit order (website, 3 units)' 202 $first.Status
    Assert-Step '07 ... not a duplicate' $false $first.Body.duplicate
    $taskId = $first.Body.taskId

    # The same order again: the same task, nothing new.
    $again = Invoke-ApiProblem POST '/api/orders/website' $staff $order 'en'
    Assert-Step '08 same order again' 202 $again.Status
    Assert-Step '09 ... is a duplicate' $true $again.Body.duplicate
    Assert-Step '10 ... of the same task' $taskId $again.Body.taskId

    # The same external order id with other content: rejected.
    $changed = @{ externalOrderId = $externalId; customerId = $customerId; items = @(@{ productId = $productId; stock = 2 }) }
    $conflict = Invoke-ApiProblem POST '/api/orders/website' $staff $changed 'en'
    Assert-Step '11 same id, other quantity' 409 $conflict.Status
    Assert-Step '12 ... errorCode' 'order_conflict' $conflict.Body.errorCode

    # A channel that does not exist.
    $unknown = Invoke-ApiProblem POST "/api/orders/smoke-no-such-channel-$n" $staff $order 'en'
    Assert-Step '13 unknown channel slug' 404 $unknown.Status
    Assert-Step '14 ... errorCode' 'channel_not_found' $unknown.Body.errorCode

    # The Order Worker creates the order and reserves the stock.
    Write-Host 'Waiting for the Order Worker (up to 60 s)...'
    $task = Wait-Task $taskId $staff 60
    if ($null -eq $task) {
        throw 'The first order did not reach a terminal state within 60 s. The Workers host is probably not running (docs/operations-guide.md section 6).'
    }
    Assert-Step '15 task finished within 60 s' 'Succeeded' $task.status
    Assert-Step '16 task has an order id' $true ([bool]$task.orderId)
    $orderId = $task.orderId

    $detail = (Invoke-Api GET "/api/orders/$orderId" $staff).Body
    Assert-Step '17 order status' 'Reserved' $detail.status
    Assert-Step '18 order total computed by the server (price x 3)' ($price * 3) ([int]$detail.totalAmount)
    Assert-Step '19 order has the task with an attempt' $true (($detail.tasks.Count -ge 1) -and ($detail.tasks[0].attempts.Count -ge 1))

    $movements = (Invoke-Api GET "/api/inventory/movements?referenceType=Order&referenceId=$orderId" $staff).Body
    Assert-Step '20 movements of the order: one' 1 $movements.totalCount
    Assert-Step '21 ... a reservation of 3' 'Reserve 3' "$($movements.items[0].type) $($movements.items[0].stock)"

    # Second order: pos, 3 units, but only 2 are left: recorded without a reservation; the task still succeeds.
    $secondId = "SMOKE-013-POS-$n"
    $second = Invoke-ApiProblem POST '/api/orders/pos' $staff @{ externalOrderId = $secondId; customerId = $customerId; items = @(@{ productId = $productId; stock = 3 }) } 'en'
    Assert-Step '22 submit order (pos, 3 units, 2 left)' 202 $second.Status
    $secondTask = Wait-Task $second.Body.taskId $staff 60
    Assert-Step '23 second task' 'Succeeded' $(if ($secondTask) { $secondTask.status } else { 'not finished' })
    $secondOrderId = if ($secondTask) { $secondTask.orderId } else { $null }
    $secondDetail = if ($secondOrderId) { (Invoke-Api GET "/api/orders/$secondOrderId" $staff).Body } else { $null }
    Assert-Step '24 second order status' 'InsufficientStock' $(if ($secondDetail) { $secondDetail.status } else { 'no order' })
    Assert-Step '25 stock: 3 reserved, 2 available' '3 2' "$((Invoke-Api GET "/api/products/$productId" $staff).Body.reservedStock) $((Invoke-Api GET "/api/products/$productId" $staff).Body.availableStock)"

    # The order list: search and filters.
    Assert-Step '26 orders: search by external order id' 1 (Invoke-Api GET "/api/orders?search=$externalId" $staff).Body.totalCount
    $filtered = (Invoke-Api GET "/api/orders?channel=pos&status=InsufficientStock&customerId=$customerId" $staff).Body
    Assert-Step '27 orders: channel + status + customer contains the second order' $true (@($filtered.items | Where-Object { $_.orderId -eq $secondOrderId }).Count -eq 1)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke accounts are locked again, also after a failed step.
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '28 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if ($admin -and $staffUser -and $staffUser.Body) {
        Assert-Step '29 lock the smoke staff' 200 (Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin).Status
    }
}
# Frontend: production build and tests (steps 30-32), after the API this script started is stopped.
Invoke-FrontendChecks 30
Complete-Smoke
