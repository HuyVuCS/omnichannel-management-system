# Smoke check for task 018b (Logs/Audit: GET /api/system-events - roles, allowed details, trace by correlation id).
# Needs PostgreSQL and RabbitMQ running (README section 1) and every migration applied (README section 4).
# Uses a running API / Workers host or - for the local default only - builds and starts them, and stops what it started.
# The Workers host must run in Development: only there the simulator's SKU rule SIM-PERM- is on.
# The smoke mapping ends Inactive and the smoke accounts are locked, also after a failed step.
# The frontend build and tests run last, after the hosts this script started were stopped.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-018b.ps1
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

# Every answer of GET /api/system-events this script received, as text: the last check searches all of them.
$script:EventAnswers = New-Object System.Collections.Generic.List[string]

# One page of GET /api/system-events as the Logs/Audit page asks for it: Status, Items (always an array, newest first)
# and Total. Any other status than 200 has no items.
function Get-Events([string]$Token, [string]$Query) {
    $headers = @{}
    if ($Token) { $headers.Authorization = "Bearer $Token" }
    try {
        $r = Invoke-WebRequest -Method GET -Uri "$script:BaseUrl/api/system-events?$Query" -UseBasicParsing -Headers $headers
        $script:EventAnswers.Add([string]$r.Content)
        $body = $r.Content | ConvertFrom-Json
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Items = @($body.items); Total = $body.totalCount }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Items = @(); Total = $null }
    }
}

# The first item of an answer, or $null.
function Get-FirstEvent($Answer) { if (@($Answer.Items).Count -ge 1) { return @($Answer.Items)[0] } else { return $null } }

# A property of an event's details as text, or 'none' when the event or the field is missing.
function Get-Detail($Item, [string]$Name) {
    if ($Item -and $Item.details -and ($null -ne $Item.details.$Name)) { return [string]$Item.details.$Name } else { return 'none' }
}

# A property of an event as text, or 'none'.
function Get-Field($Item, [string]$Name) {
    if ($Item -and ($null -ne $Item.$Name)) { return [string]$Item.$Name } else { return 'none' }
}

$admin = $null
$manager = $null
$managerUser = $null
$staffUser = $null
$website = $null
$mapping = $null
$mappingSku = $null
$deactivated = $false

try {
    Start-Smoke 'Task 018b - Logs/Audit: roles, allowed details, trace by correlation id'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $adminEmail = [string]$cred.Email
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Smoke users, created by the Admin: the Manager reads the log, the Staff must not.
    $managerEmail = "smoke-018b-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    $staffEmail = "smoke-018b-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $staff = Get-Token $staffEmail $password
    $password = $null

    Assert-Step '03 staff cannot read the events' 403 (Get-Events $staff 'pageSize=1').Status
    Assert-Step '04 ... nor an anonymous caller' 401 (Get-Events $null 'pageSize=1').Status

    # A stock import of the Manager: inventory.imported, found by the correlation id of the request.
    $product = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-018B-$n"; name = "Smoke product 018b-$n"; price = 15000; lowStockThreshold = 5 }
    Assert-Step '05 create smoke product' 201 $product.Status
    $productId = $product.Body.productId
    $documentNumber = "SMOKE-018B-GRN-$n"
    $importCorrelation = "smoke-018b-import-$n"
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = $documentNumber; items = @(@{ productId = $productId; stock = 10 }) } @{ 'X-Correlation-Id' = $importCorrelation }
    Assert-Step '06 manager imports 10 units' 200 $import.Status
    $imported = Get-Events $manager "correlationId=$importCorrelation&eventType=inventory.imported"
    Assert-Step '07 manager sees one inventory.imported event of that request' 1 @($imported.Items).Count
    $importedEvent = Get-FirstEvent $imported
    Assert-Step '08 ... details.referenceId is the document' $documentNumber (Get-Detail $importedEvent 'referenceId')
    Assert-Step '09 ... with the number of lines and units' '1 10' "$(Get-Detail $importedEvent 'itemCount') $(Get-Detail $importedEvent 'totalQuantity')"

    # A stock sync that fails for good (SIM-PERM- is rejected by the simulated channel): processing.failed.
    $channels = (Invoke-Api GET '/api/channels' $manager).Body
    $website = [string]@($channels | Where-Object { $_.slug -ceq 'website' })[0].channelId
    Assert-Step '10 the website channel exists' $true ($website.Length -gt 0)
    $mappingSku = "SIM-PERM-018B-$n"
    $mapping = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = $productId; externalSku = $mappingSku } 'en'
    Assert-Step '11 map the product on website (SIM-PERM-)' 201 $mapping.Status
    $mappingPath = "/api/channels/$website/products/$($mapping.Body.channelProductId)"
    $syncTaskId = [string]$mapping.Body.syncTaskId
    Write-Host 'Waiting for the Stock Sync Worker (up to 60 s)...'
    $failedTask = if ($syncTaskId) { Wait-Task $syncTaskId $manager 60 } else { $null }
    if ($null -eq $failedTask) {
        throw 'The sync task did not reach a terminal state within 60 s. Is the Workers host running a build of task 018b (README section 6)?'
    }
    if ($failedTask.status -ceq 'Succeeded') {
        Write-Host 'The SIM-PERM- task succeeded: ChannelSimulation:SkuRules is OFF (Workers host not in Development).' -ForegroundColor Yellow
    }
    Assert-Step '12 its sync task (SkuRules must be on)' 'Failed' (Get-TaskStatus $failedTask)
    $failed = Get-Events $manager "taskId=$syncTaskId&eventType=processing.failed"
    Assert-Step '13 one processing.failed event of the task' 1 @($failed.Items).Count
    $failedEvent = Get-FirstEvent $failed
    Assert-Step '14 ... level' 'Error' (Get-Field $failedEvent 'level')
    Assert-Step '15 ... details.errorCode' 'channel_rejected' (Get-Detail $failedEvent 'errorCode')
    Assert-Step '16 ... the task id (column and details)' "$syncTaskId $syncTaskId" "$(Get-Field $failedEvent 'taskId') $(Get-Detail $failedEvent 'taskId')"
    $traceId = Get-Field $failedEvent 'correlationId'
    Assert-Step '17 ... it has a correlation id' $true ($traceId -cne 'none')
    $trace = Get-Events $manager "correlationId=$traceId&pageSize=100"
    $failedLogId = Get-Field $failedEvent 'logId'
    Assert-Step '18 found again by its correlation id' 1 @($trace.Items | Where-Object { ([string]$_.logId -ceq $failedLogId) -and ($_.eventType -ceq 'processing.failed') }).Count

    # The Manager acknowledges the alert of that failure: alert.acknowledged names the Manager.
    $alerts = Invoke-Api GET "/api/alerts?productId=$productId&type=StockSyncFailed" $manager
    $alertId = if ($alerts.Body -and (@($alerts.Body.items).Count -ge 1)) { [string]@($alerts.Body.items)[0].alertId } else { 'none' }
    $ackCorrelation = "smoke-018b-ack-$n"
    $ack = Invoke-Api POST "/api/alerts/$alertId/acknowledge" $manager $null @{ 'X-Correlation-Id' = $ackCorrelation }
    Assert-Step '19 manager acknowledges the stock-sync alert' 200 $ack.Status
    $acknowledged = Get-Events $manager "correlationId=$ackCorrelation"
    $acknowledgedEvent = Get-FirstEvent $acknowledged
    Assert-Step '20 one event of that request: alert.acknowledged' '1 alert.acknowledged' "$(@($acknowledged.Items).Count) $(Get-Field $acknowledgedEvent 'eventType')"
    Assert-Step '21 ... actorEmail is the smoke manager' $managerEmail (Get-Field $acknowledgedEvent 'actorEmail')
    Assert-Step '22 ... details.alertId is the alert' $alertId (Get-Detail $acknowledgedEvent 'alertId')

    # User management is the Admin's: user.locked is listed for the Admin and does not exist for the Manager.
    $lockCorrelation = "smoke-018b-lock-$n"
    $lock = Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin $null @{ 'X-Correlation-Id' = $lockCorrelation }
    Assert-Step '23 admin locks the smoke staff' 200 $lock.Status
    $forAdmin = Get-Events $admin "correlationId=$lockCorrelation"
    $lockedEvent = Get-FirstEvent $forAdmin
    Assert-Step '24 admin sees one event of that request: user.locked' '1 user.locked' "$(@($forAdmin.Items).Count) $(Get-Field $lockedEvent 'eventType')"
    Assert-Step '25 ... targetEmail is the smoke staff' $staffEmail (Get-Field $lockedEvent 'targetEmail')
    # (Compared here, not shown: the admin address is not printed.)
    Assert-Step '26 ... actorEmail is the admin' $true ((Get-Field $lockedEvent 'actorEmail') -ceq $adminEmail)
    $forManager = Get-Events $manager "correlationId=$lockCorrelation"
    Assert-Step '27 manager: the same request has no event' '200 0 0' "$($forManager.Status) $(@($forManager.Items).Count) $($forManager.Total)"
    $asked = Get-Events $manager "correlationId=$lockCorrelation&eventType=user.locked"
    Assert-Step '28 ... also when asking for user.locked: an empty page' '200 0 0' "$($asked.Status) $(@($asked.Items).Count) $($asked.Total)"
    $anyUserEvent = Get-Events $manager 'eventType=user.created&eventType=user.locked&pageSize=100'
    Assert-Step '29 manager: no user.* event at all' '200 0 0' "$($anyUserEvent.Status) $(@($anyUserEvent.Items).Count) $($anyUserEvent.Total)"
    $creations = Get-Events $admin 'eventType=user.created&pageSize=100'
    $createdTargets = @($creations.Items | ForEach-Object { [string]$_.targetEmail })
    Assert-Step '30 admin sees the creation of both smoke users' $true (($createdTargets -ccontains $managerEmail) -and ($createdTargets -ccontains $staffEmail))

    # Nothing of any answer carries the outbox error or the reason of a rejected message, under any name.
    [void](Get-Events $admin 'pageSize=100')
    [void](Get-Events $admin 'eventType=outbox.publish_failed&pageSize=100')
    [void](Get-Events $admin 'eventType=order.message_rejected&eventType=stock_sync.message_rejected&pageSize=100')
    Assert-Step '31 no answer names lastError' 0 @($script:EventAnswers | Where-Object { $_ -match 'lastError' }).Count
    Assert-Step '32 ... or the reason of a rejected message' 0 @($script:EventAnswers | Where-Object { $_ -match '"reason"' }).Count

    $end = Invoke-ApiProblem PUT $mappingPath $manager @{ externalSku = $mappingSku; status = 'Inactive' } 'en'
    Assert-Step '33 set the smoke mapping Inactive' 200 $end.Status
    $deactivated = ($end.Status -eq 200)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke mapping never stays Active and the smoke accounts are locked again, also after a failed step.
    if ($manager -and $website -and $mapping -and $mapping.Body -and $mapping.Body.channelProductId -and (-not $deactivated)) {
        $cleanup = Invoke-ApiProblem PUT "/api/channels/$website/products/$($mapping.Body.channelProductId)" $manager @{ externalSku = $mappingSku; status = 'Inactive' } 'en'
        Assert-Step '34 deactivate the smoke mapping (clean-up)' 200 $cleanup.Status
    }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '35 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if ($admin -and $staffUser -and $staffUser.Body) {
        Assert-Step '36 lock the smoke staff' 200 (Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin).Status
    }
}
Invoke-FrontendChecks 37
Complete-Smoke
