# Smoke check for task 024 (payment step; invoice and payment confirmation as two independent background tasks).
# Needs PostgreSQL and RabbitMQ running (README section 1), migration PaymentAndInvoices applied (README section 4) and
# the Workers host in Development (the rules of the simulated e-mail sender: a recipient at sim-reject.test is refused).
# Uses a running API / Workers host or - for the local default only - builds and starts them, and stops what it started.
# What it leaves behind: three paid smoke orders with their invoices. The e-mail task that must fail is retried at the
# end with a corrected address, so no alert stays open; the smoke accounts are locked, also after a failed step.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-024.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

$TaskWaitSeconds = 90

# Asks for a processing task once per second until it is Succeeded or Failed; returns the last answer (or $null after the limit).
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

function Get-ErrorCode($Answer) { if ($Answer.Body -and $Answer.Body.errorCode) { return [string]$Answer.Body.errorCode } else { return 'none' } }

# Submits an order of $Quantity units on website and waits for the Order Worker; returns the order id (or $null).
function New-SmokeOrder([string]$ExternalOrderId, [string]$CustomerId, [string]$ProductId, [int]$Quantity, [string]$Token) {
    $submitted = Invoke-Api POST '/api/orders/website' $Token @{ externalOrderId = $ExternalOrderId; customerId = $CustomerId; items = @(@{ productId = $ProductId; stock = $Quantity }) }
    if ($submitted.Status -ne 202) { return $null }
    $task = Wait-Task $submitted.Body.taskId $Token 60
    if ($task -and $task.orderId) { return [string]$task.orderId } else { return $null }
}

# The stock of the smoke product as "total reserved available".
function Get-Stock([string]$Sku, [string]$Token) {
    $row = @((Invoke-Api GET "/api/inventory?search=$Sku" $Token).Body.items)[0]
    if ($row) { return "$($row.totalStock) $($row.reservedStock) $($row.availableStock)" } else { return 'not found' }
}

# The unresolved ProcessingFailed alert of a task: its status, or 'none'.
function Get-TaskAlertStatus([string]$TaskId, [string]$Token, [string]$Statuses) {
    $alerts = @((Invoke-Api GET "/api/alerts?type=ProcessingFailed&pageSize=100&$Statuses" $Token).Body.items)
    $own = @($alerts | Where-Object { [string]$_.taskId -ceq $TaskId })
    if ($own.Count -gt 0) { return [string]$own[0].status } else { return 'none' }
}

$admin = $null
$users = New-Object System.Collections.Generic.List[object]

try {
    Start-Smoke 'Task 024 - payment, invoice and payment confirmation as independent tasks'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Smoke accounts: a Manager records payments, a Staff may not.
    $managerEmail = "smoke-024-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $users.Add($managerUser)
    $manager = Get-Token $managerEmail $password

    $staffEmail = "smoke-024-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $users.Add($staffUser)
    $staff = Get-Token $staffEmail $password
    $password = $null

    # One product with 10 units, and three customers: with an address, without one, with one the simulated mail server refuses.
    $sku = "SMOKE-024-$n"
    $product = Invoke-Api POST '/api/products' $manager @{ sku = $sku; name = "Smoke product 024-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '03 create smoke product' 201 $product.Status
    $productId = [string]$product.Body.productId
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-024-GRN-$n"; items = @(@{ productId = $productId; stock = 10 }) }
    Assert-Step '04 import 10 units' 200 $import.Status

    $customerA = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 024A-$n"; phone = $null; email = "smoke-024-a-$n@example.com"; address = $null }
    $customerB = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 024B-$n"; phone = $null; email = $null; address = $null }
    $customerC = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 024C-$n"; phone = $null; email = "smoke-024-c-$n@sim-reject.test"; address = $null }
    Assert-Step '05 create three smoke customers (A with an address, B without, C at sim-reject.test)' '201 201 201' "$($customerA.Status) $($customerB.Status) $($customerC.Status)"

    $orderA = New-SmokeOrder "SMOKE-024A-$n" ([string]$customerA.Body.customerId) $productId 2 $staff
    $orderB = New-SmokeOrder "SMOKE-024B-$n" ([string]$customerB.Body.customerId) $productId 2 $staff
    $orderC = New-SmokeOrder "SMOKE-024C-$n" ([string]$customerC.Body.customerId) $productId 2 $staff
    Assert-Step '06 one order each, processed by the Order Worker (is the Workers host running?)' $true (($null -ne $orderA) -and ($null -ne $orderB) -and ($null -ne $orderC))
    if (($null -eq $orderA) -or ($null -eq $orderB) -or ($null -eq $orderC)) { throw 'The smoke orders were not processed: is the Workers host running?' }
    $statuses = @($orderA, $orderB, $orderC | ForEach-Object { [string](Invoke-Api GET "/api/orders/$_" $staff).Body.status })
    Assert-Step '07 ... all three Reserved' 'Reserved Reserved Reserved' ($statuses -join ' ')
    Assert-Step '08 ... and unpaid, without an invoice' 'Unpaid True' "$((Invoke-Api GET "/api/orders/$orderA" $staff).Body.payment.status) $($null -eq (Invoke-Api GET "/api/orders/$orderA" $staff).Body.invoice)"
    $stockBefore = Get-Stock $sku $staff
    Assert-Step '09 stock: 10 total, 6 reserved, 4 available' '10 6 4' $stockBefore

    # 1. Order A: Staff may not pay; the Manager pays; invoice and confirmation follow as two tasks.
    $reference = "SMOKE-REF-$n"
    Assert-Step '10 staff pays order A -> 403' 403 (Invoke-Api POST "/api/orders/$orderA/payment" $staff @{ paymentMethod = 'Cash' }).Status
    $paidA = Invoke-ApiProblem POST "/api/orders/$orderA/payment" $manager @{ paymentMethod = 'Cash'; paymentReference = $reference } 'en'
    Assert-Step '11 the manager pays order A (Cash) -> 200' 200 $paidA.Status
    Assert-Step '12 ... the answer names an invoice task and an e-mail task' $true (($null -ne $paidA.Body.invoiceTaskId) -and ($null -ne $paidA.Body.emailTaskId))
    $invoiceTaskA = Wait-Task ([string]$paidA.Body.invoiceTaskId) $manager $TaskWaitSeconds
    $emailTaskA = Wait-Task ([string]$paidA.Body.emailTaskId) $manager $TaskWaitSeconds
    Assert-Step "13 the invoice task succeeds within $TaskWaitSeconds s (Invoice Worker)" 'Succeeded Invoice InvoiceWorker' "$(Get-TaskStatus $invoiceTaskA) $($invoiceTaskA.taskType) $(@($invoiceTaskA.attempts)[0].workerName)"
    Assert-Step '14 the e-mail task succeeds (Notification Worker)' 'Succeeded Email NotificationWorker' "$(Get-TaskStatus $emailTaskA) $($emailTaskA.taskType) $(@($emailTaskA.attempts)[0].workerName)"
    $detailA = (Invoke-Api GET "/api/orders/$orderA" $staff).Body
    Assert-Step '15 order A: payment Paid, by Cash, recorded by the smoke manager' "Paid Cash $managerEmail" "$($detailA.payment.status) $($detailA.payment.method) $($detailA.payment.paidByEmail)"
    Assert-Step '16 ... with the reference that was entered' $reference ([string]$detailA.payment.reference)
    Assert-Step '17 ... an invoice whose number starts with SIM-' $true ([string]$detailA.invoice.number -clike 'SIM-*')
    Assert-Step '18 ... and the order is still Reserved' 'Reserved' ([string]$detailA.status)
    Assert-Step '19 the stock is what it was' $stockBefore (Get-Stock $sku $staff)
    $listed = @((Invoke-Api GET "/api/orders?search=SMOKE-024A-$n" $staff).Body.items)[0]
    Assert-Step '20 the order list shows it as Paid and Reserved' 'Paid Reserved' "$($listed.paymentStatus) $($listed.status)"

    # 2. An order is paid once.
    $again = Invoke-ApiProblem POST "/api/orders/$orderA/payment" $manager @{ paymentMethod = 'Card' } 'en'
    Assert-Step '21 paying order A again -> 409' 409 $again.Status
    Assert-Step '22 ... with' 'order_already_paid' (Get-ErrorCode $again)
    Assert-Step '23 ... and the first payment stays (Cash)' 'Cash' ([string](Invoke-Api GET "/api/orders/$orderA" $staff).Body.payment.method)

    # 3. Order B: no address, no e-mail task; the invoice is issued all the same.
    $paidB = Invoke-ApiProblem POST "/api/orders/$orderB/payment" $manager @{ paymentMethod = 'BankTransfer' } 'en'
    Assert-Step '24 the manager pays order B (customer without an address) -> 200' 200 $paidB.Status
    Assert-Step '25 ... no e-mail task' $null $paidB.Body.emailTaskId
    Assert-Step '26 ... its invoice task succeeds' 'Succeeded' (Get-TaskStatus (Wait-Task ([string]$paidB.Body.invoiceTaskId) $manager $TaskWaitSeconds))
    Assert-Step '27 ... the order has one task of the payment: the invoice' 'Invoice' ((@((Invoke-Api GET "/api/orders/$orderB" $staff).Body.tasks | Where-Object { $_.taskType -cne 'OrderProcessing' } | ForEach-Object { [string]$_.taskType })) -join ' ')

    # 4. Order C: the e-mail is refused - its own task fails; the invoice of the same order succeeds; the order is untouched.
    $paidC = Invoke-ApiProblem POST "/api/orders/$orderC/payment" $manager @{ paymentMethod = 'EWallet' } 'en'
    Assert-Step '28 the manager pays order C (address at sim-reject.test) -> 200' 200 $paidC.Status
    $emailTaskIdC = [string]$paidC.Body.emailTaskId
    $emailTaskC = Wait-Task $emailTaskIdC $manager $TaskWaitSeconds
    Assert-Step '29 its e-mail task ends Failed' 'Failed' (Get-TaskStatus $emailTaskC)
    Assert-Step '30 ... after one attempt with email_rejected (no retry)' '1 email_rejected' "$(@($emailTaskC.attempts).Count) $(@($emailTaskC.attempts)[0].errorCode)"
    Assert-Step '31 ... with an open ProcessingFailed alert' 'Open' (Get-TaskAlertStatus $emailTaskIdC $staff 'status=Open')
    Assert-Step '32 the invoice task of the same order succeeds' 'Succeeded' (Get-TaskStatus (Wait-Task ([string]$paidC.Body.invoiceTaskId) $manager $TaskWaitSeconds))
    $detailC = (Invoke-Api GET "/api/orders/$orderC" $staff).Body
    Assert-Step '33 order C: Reserved, Paid, with its invoice - one failed e-mail fails nothing else' 'Reserved Paid True' "$($detailC.status) $($detailC.payment.status) $([string]$detailC.invoice.number -clike 'SIM-*')"

    # 5. An order without reserved stock cannot be paid.
    $rejected = New-SmokeOrder "SMOKE-024X-$n" ([string]$customerA.Body.customerId) $productId 100 $staff
    Assert-Step '34 an order of 100 units is stored as InsufficientStock' 'InsufficientStock' ([string](Invoke-Api GET "/api/orders/$rejected" $staff).Body.status)
    $notPayable = Invoke-ApiProblem POST "/api/orders/$rejected/payment" $manager @{ paymentMethod = 'Cash' } 'en'
    Assert-Step '35 paying it -> 409' 409 $notPayable.Status
    Assert-Step '36 ... with' 'order_not_payable' (Get-ErrorCode $notPayable)
    Assert-Step '37 ... and it stays unpaid' 'Unpaid' ([string](Invoke-Api GET "/api/orders/$rejected" $staff).Body.payment.status)
    Assert-Step '38 after all payments the stock is still what it was' $stockBefore (Get-Stock $sku $staff)

    # 6. Every payment is audited: who paid which order how - and never the reference.
    $paidEvents = @($orderA, $orderB, $orderC | ForEach-Object { @((Invoke-Api GET "/api/system-events?eventType=order.paid&orderId=$_" $manager).Body.items) })
    Assert-Step '39 order.paid in the system events: one for each of A, B, C' 3 $paidEvents.Count
    Assert-Step '40 ... with the payment methods' 'Cash BankTransfer EWallet' ((@($paidEvents | ForEach-Object { [string]$_.details.paymentMethod })) -join ' ')
    Assert-Step '41 ... each by the smoke manager' $true (@($paidEvents | Where-Object { [string]$_.actorEmail -cne $managerEmail }).Count -eq 0)
    $eventA = (Invoke-WebRequest -Method GET -Uri "$script:BaseUrl/api/system-events?eventType=order.paid&orderId=$orderA" -Headers @{ Authorization = "Bearer $manager" } -UseBasicParsing).Content
    Assert-Step '42 ... and the reference of the payment is not in the event' $false ($eventA -clike "*$reference*")
    Assert-Step '43 staff cannot read the events' 403 (Invoke-Api GET '/api/system-events?eventType=order.paid' $staff).Status

    # The recovery path of the failed e-mail (and the clean-up of its alert): correct the address, retry the task.
    $fixed = Invoke-Api PUT "/api/customers/$($customerC.Body.customerId)" $manager @{ name = "Smoke customer 024C-$n"; phone = $null; email = "smoke-024-c-$n@example.com"; address = $null }
    Assert-Step '44 the address of customer C is corrected' 200 $fixed.Status
    Assert-Step '45 the manager retries the failed e-mail task -> 202' 202 (Invoke-Api POST "/api/processing/tasks/$emailTaskIdC/retry" $manager).Status
    Start-Sleep -Seconds 1
    $retried = Wait-Task $emailTaskIdC $manager $TaskWaitSeconds
    Assert-Step '46 ... it succeeds with its second attempt' 'Succeeded 2' "$(Get-TaskStatus $retried) $(@($retried.attempts).Count)"
    Assert-Step '47 ... and its alert is resolved' 'Resolved' (Get-TaskAlertStatus $emailTaskIdC $staff 'status=Resolved')

    # Clean-up of the insufficient-stock alert of step 34: the next import of the product resolves it.
    Assert-Step '48 an import of 1 unit (resolves the insufficient-stock alert of the rejected order)' 200 (Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-024-GRN2-$n"; items = @(@{ productId = $productId; stock = 1 }) }).Status
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke accounts are locked, also after a failed step.
    $step = 49
    foreach ($user in $users) {
        if ($admin -and $user -and $user.Body) {
            Assert-Step ('{0:00} lock smoke account {1}' -f $step, ($step - 48)) 200 (Invoke-Api POST "/api/users/$($user.Body.userId)/lock" $admin).Status
            $step++
        }
    }
}
Invoke-FrontendChecks 51
Complete-Smoke
