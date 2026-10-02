# Smoke check for task 016 (product SKU on processing tasks; the API side of the dashboard and the Sync & Processing
# page; frontend build and tests).
# Needs PostgreSQL and RabbitMQ running (README section 1). No new migration.
# Uses a running API / Workers host or - for the local default only - builds and starts them, and stops what it started.
# The smoke mapping is deactivated at the end; the real channels are not changed.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-016.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

# Runs npm in the frontend folder without showing its output; returns the exit code.
function Invoke-Npm([string]$Arguments) {
    $frontend = Join-Path $script:RepoRoot 'frontend'
    Push-Location $frontend
    try {
        cmd /c "npm $Arguments > NUL 2>&1"
        return $LASTEXITCODE
    } finally {
        Pop-Location
    }
}

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

$admin = $null
$manager = $null
$managerUser = $null
$staffUser = $null
$website = $null
$mapping = $null
$externalSku = $null

try {
    Start-Smoke 'Task 016 - product SKU on tasks, dashboard and processing API, frontend build and tests'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Smoke users: a Manager prepares the data and the mapping, a Staff reads and enters the order.
    $managerEmail = "smoke-016-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    $staffEmail = "smoke-016-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $staff = Get-Token $staffEmail $password
    $password = $null

    # Data: customer, product, 5 units in stock.
    $sku = "SMOKE-016-$n"
    $customer = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 016-$n"; phone = $null; email = $null; address = $null }
    Assert-Step '03 create smoke customer' 201 $customer.Status
    $product = Invoke-Api POST '/api/products' $manager @{ sku = $sku; name = "Smoke product 016-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '04 create smoke product' 201 $product.Status
    $productId = $product.Body.productId
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-016-GRN-$n"; items = @(@{ productId = $productId; stock = 5 }) }
    Assert-Step '05 import 5 units' 200 $import.Status

    $channels = (Invoke-Api GET '/api/channels' $staff).Body
    $website = @($channels | Where-Object { $_.slug -eq 'website' })[0].channelId
    Assert-Step '06 the website channel exists' $true ([bool]$website)

    # Mapping on website: the initial stock sync is a StockSync task of this channel and product.
    $externalSku = "WEB-SMOKE-016-$n"
    $mapping = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = $productId; externalSku = $externalSku } 'en'
    Assert-Step '07 map the product on website' 201 $mapping.Status
    Assert-Step '08 ... with a sync task' $true ([bool]$mapping.Body.syncTaskId)
    Write-Host 'Waiting for the Stock Sync Worker (up to 60 s)...'
    $syncTask = Wait-Task $mapping.Body.syncTaskId $staff 60
    if ($null -eq $syncTask) {
        throw 'The stock-sync task did not finish within 60 s. Is the Workers host running (README section 6)?'
    }
    Assert-Step '09 sync task' 'Succeeded' $syncTask.status
    Assert-Step '10 task detail carries the product SKU' $sku $syncTask.productSku

    $list = (Invoke-Api GET "/api/processing/tasks?taskType=StockSync&channelId=$website&pageSize=100" $staff).Body
    $listed = @($list.items | Where-Object { $_.taskId -eq $mapping.Body.syncTaskId })
    Assert-Step '11 task list (StockSync, website) contains the task' 1 $listed.Count
    $listedSku = if ($listed.Count -ge 1) { $listed[0].productSku } else { $null }
    Assert-Step '12 ... with the product SKU' $sku $listedSku

    # An order on website: its OrderProcessing task has no product.
    $order = @{ externalOrderId = "SMOKE-016-WEB-$n"; customerId = $customer.Body.customerId; items = @(@{ productId = $productId; stock = 2 }) }
    $submitted = Invoke-Api POST '/api/orders/website' $staff $order
    Assert-Step '13 staff submits an order (website, 2 units)' 202 $submitted.Status
    Write-Host 'Waiting for the Order Worker (up to 60 s)...'
    $orderTask = if ($submitted.Body) { Wait-Task $submitted.Body.taskId $staff 60 } else { $null }
    Assert-Step '14 order task' 'Succeeded' (Get-TaskStatus $orderTask)
    Assert-Step '15 ... has the member productSku' $true (Test-Member $orderTask 'productSku')
    $orderTaskSku = if ($orderTask) { $orderTask.productSku } else { 'no task' }
    Assert-Step '16 ... which is null' $true ($null -eq $orderTaskSku)

    # What the dashboard and the processing page ask for, as Staff.
    Assert-Step '17 staff reads the dashboard summary' 200 (Invoke-Api GET '/api/dashboard/summary' $staff).Status
    $staffRetry = Invoke-ApiProblem POST "/api/processing/tasks/$($mapping.Body.syncTaskId)/retry" $staff $null 'en'
    Assert-Step '18 staff cannot retry a task' 403 $staffRetry.Status

    # Frontend: production build and tests.
    if (-not (Test-Path (Join-Path $script:RepoRoot 'frontend\node_modules\@microsoft\signalr'))) {
        Write-Host 'Installing frontend packages (npm ci)...'
        Assert-Step '19 npm ci' 0 (Invoke-Npm 'ci')
    }
    Write-Host 'Building the frontend...'
    Assert-Step '20 npm run build' 0 (Invoke-Npm 'run build')
    Write-Host 'Running the frontend tests...'
    Assert-Step '21 npm test' 0 (Invoke-Npm 'test')
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke mapping is deactivated and the smoke accounts are locked again, also after a failed step.
    if ($manager -and $website -and $mapping -and $mapping.Body -and $mapping.Body.channelProductId) {
        $off = Invoke-ApiProblem PUT "/api/channels/$website/products/$($mapping.Body.channelProductId)" $manager @{ externalSku = $externalSku; status = 'Inactive' } 'en'
        Assert-Step '22 deactivate the smoke mapping' 200 $off.Status
    }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '23 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if ($admin -and $staffUser -and $staffUser.Body) {
        Assert-Step '24 lock the smoke staff' 200 (Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin).Status
    }
}
Complete-Smoke
