# Smoke check for task 024b (payment on the order detail: the API the page uses, the invoice number of the simulated
# provider, the answer to a payment method the system does not know).
# Needs PostgreSQL and RabbitMQ running (docs/operations-guide.md section 1) and migration PaymentAndInvoices applied (docs/operations-guide.md section 4).
# Uses a running API / Workers host or - for the local default only - builds and starts them, and stops what it started.
# What it leaves behind: one paid smoke order with its invoice and one unpaid smoke order; the smoke account is locked,
# also after a failed step.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-024b.ps1
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

# Submits an order of $Quantity units on website and waits for the Order Worker; returns the order id (or $null).
function New-SmokeOrder([string]$ExternalOrderId, [string]$CustomerId, [string]$ProductId, [int]$Quantity, [string]$Token) {
    $submitted = Invoke-Api POST '/api/orders/website' $Token @{ externalOrderId = $ExternalOrderId; customerId = $CustomerId; items = @(@{ productId = $ProductId; stock = $Quantity }) }
    if ($submitted.Status -ne 202) { return $null }
    $task = Wait-Task $submitted.Body.taskId $Token 60
    if ($task -and $task.orderId) { return [string]$task.orderId } else { return $null }
}

$admin = $null
$managerUser = $null

try {
    Start-Smoke 'Task 024b - payment on the order detail: the API behind the page'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    $managerEmail = "smoke-024b-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    # One product with 10 units and a customer with an e-mail address.
    $sku = "SMOKE-024B-$n"
    $product = Invoke-Api POST '/api/products' $manager @{ sku = $sku; name = "Smoke product 024b-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '02 create smoke product' 201 $product.Status
    $productId = [string]$product.Body.productId
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-024B-GRN-$n"; items = @(@{ productId = $productId; stock = 10 }) }
    Assert-Step '03 import 10 units' 200 $import.Status
    $customer = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 024b-$n"; phone = $null; email = "smoke-024b-$n@example.com"; address = $null }
    Assert-Step '04 create smoke customer with an e-mail address' 201 $customer.Status
    $customerId = [string]$customer.Body.customerId

    # 1. The order the page starts from: Reserved and unpaid, in the detail and in the list.
    $orderId = New-SmokeOrder "SMOKE-024B-A-$n" $customerId $productId 2 $manager
    Assert-Step '05 an order, processed by the Order Worker (is the Workers host running?)' $true ($null -ne $orderId)
    if ($null -eq $orderId) { throw 'The smoke order was not processed: is the Workers host running?' }
    $before = (Invoke-Api GET "/api/orders/$orderId" $manager).Body
    Assert-Step '06 ... Reserved, Unpaid, without an invoice' 'Reserved Unpaid True' "$($before.status) $($before.payment.status) $($null -eq $before.invoice)"
    $listedBefore = @((Invoke-Api GET "/api/orders?search=SMOKE-024B-A-$n" $manager).Body.items)[0]
    Assert-Step '07 the order list shows it as Unpaid' 'Unpaid' ([string]$listedBefore.paymentStatus)

    # 2. "Mark as paid": the body the dialog sends, the answer it reads.
    $reference = "SMOKE-024B-REF-$n"
    $paid = Invoke-ApiProblem POST "/api/orders/$orderId/payment" $manager @{ paymentMethod = 'BankTransfer'; paymentReference = $reference } 'en'
    Assert-Step '08 the manager pays the order (BankTransfer, with a reference) -> 200' 200 $paid.Status
    Assert-Step '09 ... the answer names an invoice task and an e-mail task' $true (($null -ne $paid.Body.invoiceTaskId) -and ($null -ne $paid.Body.emailTaskId))
    $invoiceTask = Wait-Task ([string]$paid.Body.invoiceTaskId) $manager $TaskWaitSeconds
    Assert-Step "10 the invoice task succeeds within $TaskWaitSeconds s (Invoice Worker)" 'Succeeded' (Get-TaskStatus $invoiceTask)
    $emailTask = Wait-Task ([string]$paid.Body.emailTaskId) $manager $TaskWaitSeconds
    Assert-Step '11 the e-mail task succeeds (Notification Worker)' 'Succeeded' (Get-TaskStatus $emailTask)

    # 3. What the payment card shows.
    $detail = (Invoke-Api GET "/api/orders/$orderId" $manager).Body
    Assert-Step '12 the detail: Paid by BankTransfer, recorded by the smoke manager, with the reference' "Paid BankTransfer $managerEmail $reference" "$($detail.payment.status) $($detail.payment.method) $($detail.payment.paidByEmail) $($detail.payment.reference)"
    Assert-Step '13 ... the invoice number is SIM-{yyyyMM}-{16 hex digits}' $true ([string]$detail.invoice.number -cmatch '^SIM-\d{6}-[0-9A-F]{16}$')
    Assert-Step '14 ... the order has an Invoice task and an Email task' 'Email Invoice' ((@($detail.tasks | Where-Object { $_.taskType -cne 'OrderProcessing' } | ForEach-Object { [string]$_.taskType } | Sort-Object)) -join ' ')
    Assert-Step '15 ... and is still Reserved' 'Reserved' ([string]$detail.status)
    $listed = @((Invoke-Api GET "/api/orders?search=SMOKE-024B-A-$n" $manager).Body.items)[0]
    Assert-Step '16 the order list shows it as Paid' 'Paid' ([string]$listed.paymentStatus)

    # 4. A payment method the system does not know: refused by the JSON binder, under its JSON path (task 024b A1).
    $otherId = New-SmokeOrder "SMOKE-024B-B-$n" $customerId $productId 1 $manager
    Assert-Step '17 a second Reserved order' $true ($null -ne $otherId)
    $cheque = Invoke-ApiProblem POST "/api/orders/$otherId/payment" $manager @{ paymentMethod = 'Cheque' } 'en'
    Assert-Step '18 paying it with paymentMethod "Cheque" -> 400' 400 $cheque.Status
    Assert-Step '19 ... validation_failed' 'validation_failed' ([string]$cheque.Body.errorCode)
    $keys = @()
    if ($cheque.Body -and $cheque.Body.errors) { $keys = @($cheque.Body.errors.PSObject.Properties | ForEach-Object { $_.Name } | Sort-Object) }
    Assert-Step '20 ... with the error keys $.paymentMethod and request' '$.paymentMethod request' ($keys -join ' ')
    Assert-Step '21 ... and the order stays Unpaid' 'Unpaid' ([string](Invoke-Api GET "/api/orders/$otherId" $manager).Body.payment.status)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke account is locked, also after a failed step.
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '22 lock smoke account' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
}
Invoke-FrontendChecks 23
Complete-Smoke
