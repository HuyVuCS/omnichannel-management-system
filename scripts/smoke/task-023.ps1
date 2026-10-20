# Smoke check for task 023 (webhooks: POST /api/webhooks/{slug} - signed, normalised through the channel's mappings,
# accepted by the order intake; and one external SKU per channel among mappings without an external product id).
# Needs PostgreSQL and RabbitMQ running (docs/operations-guide.md section 1), the migrations ChannelProductExternalSku and
# ChannelCustomers applied (docs/operations-guide.md section 4) and the webhook secrets (scripts\setup-webhook-secrets.ps1, README
# section 9). Uses a running API / Workers host or builds and starts them, and stops what it started. An API that was
# started before the secrets were set must be restarted first.
# Another environment (SMOKE_BASE_URL, see _common.ps1) has a webhook secret of its own: name a .env-style file that
# holds its WEBHOOK_SECRET_WEBSITE in SMOKE_WEBHOOK_ENV_FILE.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-023.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

$SendWebhook = Join-Path $script:RepoRoot 'scripts\send-webhook.ps1'
$WebhookEnvFile = if ($env:SMOKE_WEBHOOK_ENV_FILE) { $env:SMOKE_WEBHOOK_ENV_FILE } else { Join-Path $script:RepoRoot 'backend\src\Omnichannel.Api\.env' }
$WorkFolder = Join-Path ([System.IO.Path]::GetTempPath()) ('omnichannel-smoke-023-' + [guid]::NewGuid().ToString('N'))

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

# Writes $Json to a file of the work folder exactly as given (UTF-8, no BOM): the bytes of the file are what is signed.
function New-BodyFile([string]$Name, [string]$Json) {
    $path = Join-Path $WorkFolder $Name
    [System.IO.File]::WriteAllText($path, $Json, (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

# Signs and sends a webhook to the website channel with the helper script; returns { Status; Text; Body }.
function Send-Webhook([string]$BodyFile, [string]$Timestamp, [string]$SignatureOf) {
    $arguments = @{ Channel = 'website'; BodyFile = $BodyFile; BaseUrl = $script:BaseUrl; EnvFile = $WebhookEnvFile; PassThru = $true }
    if ($Timestamp) { $arguments.Timestamp = $Timestamp }
    if ($SignatureOf) { $arguments.SignatureOf = $SignatureOf }
    return (& $SendWebhook @arguments)
}

function Get-ErrorCode($Answer) { if ($Answer -and $Answer.Body) { return [string]$Answer.Body.errorCode } else { return 'none' } }

$admin = $null
$managerUser = $null
$manager = $null
$website = $null
$mappings = @()

try {
    Start-Smoke 'Task 023 - webhooks (signed, normalised through channel mappings) and one external SKU per channel'
    Start-SmokeWorkers
    [void](New-Item -ItemType Directory -Path $WorkFolder)

    # The secret the helper signs with must exist: without it nothing below can work.
    $envValues = Read-DotEnv $WebhookEnvFile
    $hasSecret = [bool]$envValues['WEBHOOK_SECRET_WEBSITE']
    $envValues = $null
    Assert-Step '01 WEBHOOK_SECRET_WEBSITE is in the .env (scripts\setup-webhook-secrets.ps1)' $true $hasSecret
    if (-not $hasSecret) { throw 'No webhook secret: run scripts\setup-webhook-secrets.ps1, restart the API, and run this script again.' }

    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    $managerEmail = "smoke-023-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '02 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    # A product with 10 units, mapped on website under an external product id.
    $sku = "SMOKE-023-$n"
    $externalProductId = "WEB-023-$n"
    $product = Invoke-Api POST '/api/products' $manager @{ sku = $sku; name = "Smoke product 023-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '03 create smoke product' 201 $product.Status
    $productId = [string]$product.Body.productId
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-023-GRN-$n"; items = @(@{ productId = $productId; stock = 10 }) }
    Assert-Step '04 import 10 units' 200 $import.Status
    $website = [string]@((Invoke-Api GET '/api/channels' $manager).Body | Where-Object { $_.slug -eq 'website' })[0].channelId
    Assert-Step '05 the website channel exists' $true ([bool]$website)
    $mapping = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = $productId; externalProductId = $externalProductId } 'en'
    Assert-Step '06 map on website by external product id' 201 $mapping.Status
    if ($mapping.Status -eq 201) { $mappings += $mapping.Body }

    # 1. A signed webhook of a new external customer, 2 units.
    $orderId = "SMOKE-023-ORDER-$n"
    $customerName = "Smoke Webhook Customer 023-$n"
    $json = '{"externalOrderId":"' + $orderId + '","customer":{"externalCustomerId":"SMOKE-023-C-' + $n + '","name":"' + $customerName + '","phone":"0901 234 567"},"items":[{"externalProductId":"' + $externalProductId + '","quantity":2}]}'
    $orderFile = New-BodyFile 'order.json' $json
    $sent = Send-Webhook $orderFile
    Assert-Step '07 signed webhook is accepted' 202 $sent.Status
    Assert-Step '08 ... as a new order (duplicate false)' $false ([bool]$sent.Body.duplicate)
    $taskId = [string]$sent.Body.taskId
    Write-Host 'Waiting for the Order Worker (up to 60 s)...'
    $task = Wait-Task $taskId $manager 60
    if ($null -eq $task) { throw 'The order task did not finish within 60 s. Is the Workers host running (docs/operations-guide.md section 6)?' }
    Assert-Step '09 the task of the webhook order' 'Succeeded' (Get-TaskStatus $task)
    $order = (Invoke-Api GET "/api/orders/$($task.orderId)" $manager).Body
    Assert-Step '10 the order is Reserved under the channel order id' "Reserved $orderId" "$($order.status) $($order.externalOrderId)"
    $lines = @($order.items)
    $line = if ($lines.Count -eq 1) { "$($lines[0].sku) x $($lines[0].stock)" } else { "$($lines.Count) lines" }
    Assert-Step '11 ... with the mapped product, 2 units' "$sku x 2" $line
    Assert-Step '12 ... for the customer the webhook named' $customerName ([string]$order.customer.name)
    Assert-Step '13 ... on the website channel' 'website' ([string]$order.channel.slug)
    $customers = Invoke-Api GET "/api/customers?search=$([uri]::EscapeDataString($customerName))" $manager
    Assert-Step '14 the customer exists once' 1 ([int]$customers.Body.totalCount)

    # 2. The same body again, with a new timestamp: the same order.
    $again = Send-Webhook $orderFile
    Assert-Step '15 the same webhook again is accepted' 202 $again.Status
    Assert-Step '16 ... as a duplicate' $true ([bool]$again.Body.duplicate)
    Assert-Step '17 ... of the same task' $taskId ([string]$again.Body.taskId)
    $customers = Invoke-Api GET "/api/customers?search=$([uri]::EscapeDataString($customerName))" $manager
    Assert-Step '18 ... and the customer still exists once' 1 ([int]$customers.Body.totalCount)

    # 3. What is refused: a body changed after signing, an old timestamp, a product the channel does not sell.
    $tamperedFile = New-BodyFile 'tampered.json' $json.Replace('"quantity":2', '"quantity":9')
    $tampered = Send-Webhook $tamperedFile $null $orderFile
    Assert-Step '19 a body changed after signing is refused' 401 $tampered.Status
    Assert-Step '20 ... with' 'webhook_signature_invalid' (Get-ErrorCode $tampered)
    $old = Send-Webhook $orderFile ([string]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 600))
    Assert-Step '21 a timestamp of ten minutes ago is refused' 401 $old.Status
    Assert-Step '22 ... with' 'webhook_timestamp_invalid' (Get-ErrorCode $old)
    $unknownFile = New-BodyFile 'unknown.json' ('{"externalOrderId":"SMOKE-023-UNKNOWN-' + $n + '","customer":{"externalCustomerId":"SMOKE-023-C-' + $n + '","name":"' + $customerName + '"},"items":[{"externalProductId":"NOBODY-SELLS-' + $n + '","quantity":1}]}')
    $unknown = Send-Webhook $unknownFile
    Assert-Step '23 an external product id nobody maps is refused' 400 $unknown.Status
    $keys = if ($unknown.Body -and $unknown.Body.errors) { @($unknown.Body.errors.PSObject.Properties | ForEach-Object { $_.Name }) -join ',' } else { 'none' }
    Assert-Step '24 ... on the line' 'items[0].externalProductId' $keys
    $after = (Invoke-Api GET "/api/products/$productId" $manager).Body
    Assert-Step '25 stock after one order of 2 (total / reserved / available)' '10 / 2 / 8' "$($after.totalStock) / $($after.reservedStock) / $($after.availableStock)"

    # 4. One external SKU per channel among the mappings without an external product id (task 023 A3).
    $skuOnly = "SMOKE-023-SKU-$n"
    $second = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-023B-$n"; name = "Smoke product 023B-$n"; price = 1000; lowStockThreshold = 0 }
    $third = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-023C-$n"; name = "Smoke product 023C-$n"; price = 1000; lowStockThreshold = 0 }
    Assert-Step '26 create two more smoke products' '201 201' "$($second.Status) $($third.Status)"
    $bySku = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = [string]$second.Body.productId; externalSku = $skuOnly } 'en'
    Assert-Step '27 map one on website by external SKU only' 201 $bySku.Status
    if ($bySku.Status -eq 201) { $mappings += $bySku.Body }
    $taken = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = [string]$third.Body.productId; externalSku = $skuOnly } 'en'
    if ($taken.Status -eq 201) { $mappings += $taken.Body }
    Assert-Step '28 the same external SKU for the other product is refused' 409 $taken.Status
    Assert-Step '29 ... with' 'external_sku_taken' (Get-ErrorCode $taken)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # Nothing is left Active, and the smoke account is locked - also after a failed step.
    $step = 30
    foreach ($m in $mappings) {
        if ($manager -and $website -and $m -and $m.channelProductId) {
            $off = Invoke-ApiProblem PUT "/api/channels/$website/products/$($m.channelProductId)" $manager @{ externalProductId = $m.externalProductId; externalSku = $m.externalSku; status = 'Inactive' } 'en'
            Assert-Step ('{0:00} deactivate a smoke mapping' -f $step) 200 $off.Status
            $step++
        }
    }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step ('{0:00} lock the smoke manager' -f $step) 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if (Test-Path -LiteralPath $WorkFolder) { Remove-Item -LiteralPath $WorkFolder -Recurse -Force }
}
Invoke-FrontendChecks 33
Complete-Smoke
