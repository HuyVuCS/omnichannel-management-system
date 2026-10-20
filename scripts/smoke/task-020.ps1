# Smoke check for task 020 (Excel exports built in the background: request, build by the Report Export Worker, download).
# Needs PostgreSQL and RabbitMQ running (docs/operations-guide.md section 1), migration ReportExports applied (docs/operations-guide.md section 4), the
# object storage emulator up, and its keys in the user-secrets of the API AND of the Workers host
# (scripts\setup-object-storage.ps1, then docker compose up -d - docs/operations-guide.md "Reports and Excel export").
# Uses a running API / Workers host or - for the local default only - builds and starts them, and stops what it started.
# When the storage does not answer, the script fails with a step that says so. It never prints a download link: a
# presigned link is a credential until it expires. The smoke accounts are locked, also after a failed step.
# No frontend checks: this task changes no frontend file.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-020.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

$ExportWaitSeconds = 120

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

# Asks for an export once per second until it has ended, at most $Seconds. Returns Status (the export's last status, or
# 'not answered') and StorageDown: true as soon as an attempt of its task failed because the storage did not answer -
# then waiting on is pointless, the worker would only retry.
function Wait-Export([string]$TaskId, [string]$Token, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $status = 'not answered'
    while ((Get-Date) -lt $deadline) {
        $export = (Invoke-Api GET "/api/reports/exports/$TaskId" $Token).Body
        if ($export) { $status = [string]$export.status }
        if (($status -ceq 'Succeeded') -or ($status -ceq 'Failed') -or ($status -ceq 'Expired')) {
            return [pscustomobject]@{ Status = $status; StorageDown = ([string]$export.errorCode -ceq 'storage_unavailable') }
        }
        if ($status -ceq 'RetryScheduled') {
            $task = (Invoke-Api GET "/api/processing/tasks/$TaskId" $Token).Body
            $codes = @($task.attempts | ForEach-Object { [string]$_.errorCode })
            if ($codes -ccontains 'storage_unavailable') { return [pscustomobject]@{ Status = $status; StorageDown = $true } }
        }
        Start-Sleep -Seconds 1
    }
    return [pscustomobject]@{ Status = $status; StorageDown = $false }
}

# A request to the storage (not to the API): no Authorization header. Returns Status, Bytes and the Content-Disposition
# header; Status 0 = no HTTP answer.
function Invoke-Storage([string]$Url) {
    try {
        $r = Invoke-WebRequest -Method GET -Uri $Url -UseBasicParsing -TimeoutSec 60
        $bytes = if ($r.RawContentStream) { $r.RawContentStream.ToArray() } else { [byte[]]@() }
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Bytes = $bytes; Disposition = [string]$r.Headers['Content-Disposition'] }
    } catch {
        $resp = $_.Exception.Response
        $status = if ($null -eq $resp) { 0 } else { [int]$resp.StatusCode }
        return [pscustomobject]@{ Status = $status; Bytes = [byte[]]@(); Disposition = '' }
    }
}

# The first message of a field in a validation problem, or 'none'.
function Get-FieldError($Answer, [string]$Field) {
    if ($Answer.Body -and $Answer.Body.errors -and $Answer.Body.errors.$Field) { return [string]@($Answer.Body.errors.$Field)[0] } else { return 'none' }
}

# An xlsx file is a zip archive: it starts with the two bytes "PK".
function Test-StartsWithPk([byte[]]$Bytes) { return (($Bytes.Length -gt 2) -and ($Bytes[0] -eq 0x50) -and ($Bytes[1] -eq 0x4B)) }

$timeZone = 'Asia/Ho_Chi_Minh'
$admin = $null
$users = New-Object System.Collections.Generic.List[object]

try {
    Start-Smoke 'Task 020 - Excel exports in the background: request, build, download'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Three smoke accounts: the requester, another manager, and a staff member.
    $managerEmail = "smoke-020-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $users.Add($managerUser)
    $manager = Get-Token $managerEmail $password

    $otherEmail = "smoke-020-mgr2-$n@example.com"
    $password = New-TestPassword
    $otherUser = Invoke-Api POST '/api/users' $admin @{ email = $otherEmail; password = $password; role = 'Manager' }
    Assert-Step '02 create a second smoke manager' 201 $otherUser.Status
    $users.Add($otherUser)
    $other = Get-Token $otherEmail $password

    $staffEmail = "smoke-020-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '03 create smoke staff' 201 $staffUser.Status
    $users.Add($staffUser)
    $staff = Get-Token $staffEmail $password
    $password = $null

    # Something to export: one product with stock, one customer, one order with one line (reserved by the Order Worker).
    $sku = "SMOKE-020-$n"
    $product = Invoke-Api POST '/api/products' $manager @{ sku = $sku; name = "Smoke product 020-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '04 create smoke product' 201 $product.Status
    $productId = $product.Body.productId
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-020-GRN-$n"; items = @(@{ productId = $productId; stock = 10 }) }
    Assert-Step '05 import 10 units' 200 $import.Status
    $customer = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 020-$n"; phone = $null; email = $null; address = $null }
    Assert-Step '06 create smoke customer' 201 $customer.Status
    $customerId = $customer.Body.customerId
    $submitted = Invoke-Api POST '/api/orders/website' $staff @{ externalOrderId = "SMOKE-020-$n"; customerId = $customerId; items = @(@{ productId = $productId; stock = 2 }) }
    Assert-Step '07 submit an order on website -> 202' 202 $submitted.Status
    $orderTask = Wait-Task $submitted.Body.taskId $staff 60
    Assert-Step '08 ... processed by the Order Worker (is the Workers host running?)' 'Succeeded' $(if ($orderTask) { [string]$orderTask.status } else { 'not finished' })

    $from = (Get-Date).AddDays(-1).ToString('o')
    $to = (Get-Date).AddMinutes(5).ToString('o')
    $ordersRequest = @{ customerId = $customerId; from = $from; to = $to; timeZone = $timeZone }

    # Who may export, and what a request must carry.
    Assert-Step '09 staff: orders export is forbidden' 403 (Invoke-Api POST '/api/reports/orders/export' $staff $ordersRequest).Status
    Assert-Step '10 ... inventory export too' 403 (Invoke-Api POST '/api/reports/inventory/export' $staff @{ timeZone = $timeZone }).Status
    Assert-Step '11 ... and the list of exports' 403 (Invoke-Api GET '/api/reports/exports' $staff).Status
    $inverted = Invoke-ApiProblem POST '/api/reports/orders/export' $manager @{ from = $to; to = $from; timeZone = $timeZone } 'en'
    Assert-Step '12 a range that ends before it starts -> 400' 400 $inverted.Status
    Assert-Step '13 ... on the field to' "'To date' must not be earlier than 'From date'." (Get-FieldError $inverted 'to')
    $noRange = Invoke-ApiProblem POST '/api/reports/orders/export' $manager @{ timeZone = $timeZone } 'en'
    Assert-Step '14 no range at all -> 400 on from' "'From date' must not be empty." (Get-FieldError $noRange 'from')
    $noZone = Invoke-ApiProblem POST '/api/reports/inventory/export' $manager @{ timeZone = 'Mars/Olympus_Mons' } 'en'
    Assert-Step '15 an unknown time zone -> 400 on timeZone' "'Time zone' must be the name of a time zone, for example Asia/Ho_Chi_Minh." (Get-FieldError $noZone 'timeZone')

    # The orders export: accepted at once, built in the background.
    $accepted = Invoke-Api POST '/api/reports/orders/export' $manager $ordersRequest
    Assert-Step '16 orders export -> 202' 202 $accepted.Status
    $exportId = [string]$accepted.Body.taskId
    $waited = Wait-Export $exportId $manager $ExportWaitSeconds
    Assert-Step '17 the Report Export Worker reached the object storage (is the emulator up, do the Workers have its keys? docs/operations-guide.md "Reports and Excel export")' $true (-not $waited.StorageDown)
    if ($waited.StorageDown) { throw 'The Report Export Worker cannot reach the object storage: start it, run scripts\setup-object-storage.ps1, restart the Workers host and run the script again.' }
    Assert-Step "18 the export is built within $ExportWaitSeconds s (is the Workers host running?)" 'Succeeded' $waited.Status

    $detail = (Invoke-Api GET "/api/reports/exports/$exportId" $manager).Body
    Assert-Step '19 its detail: an orders report' 'Orders' ([string]$detail.reportType)
    Assert-Step '20 ... with 2 rows: the order and its line' 2 $detail.rowCount
    Assert-Step '21 ... a file name orders_<date>-<time>.xlsx' $true ([string]$detail.fileName -cmatch '^orders_\d{8}-\d{4}\.xlsx$')
    Assert-Step '22 ... kept for 7 days' $true (([datetimeoffset]$detail.expiresAt - [datetimeoffset]$detail.createdAt).TotalDays -ge 6.99)
    Assert-Step '23 ... the time zone of the request in its parameters' $timeZone ([string]$detail.parameters.timeZone)
    $task = (Invoke-Api GET "/api/processing/tasks/$exportId" $manager).Body
    Assert-Step '24 its task: type ReportExport, one attempt by the ReportExportWorker' 'ReportExport 1 ReportExportWorker' "$($task.taskType) $(@($task.attempts).Count) $(@($task.attempts)[0].workerName)"

    # The download: a link for a few minutes, used without the API's token.
    $link = Invoke-Api GET "/api/reports/exports/$exportId/download" $manager
    Assert-Step '25 download link -> 200' 200 $link.Status
    Assert-Step '26 ... with the file name of the export' ([string]$detail.fileName) ([string]$link.Body.fileName)
    $file = Invoke-Storage ([string]$link.Body.url)
    Assert-Step '27 GET the link (no Authorization header) -> 200' 200 $file.Status
    Assert-Step '28 ... the bytes start with PK (an xlsx file)' $true (Test-StartsWithPk $file.Bytes)
    Assert-Step '29 ... as many bytes as the export says' ([long]$detail.sizeBytes) ([long]$file.Bytes.Length)
    Assert-Step '30 ... served as an attachment with the file name' "attachment; filename=`"$($detail.fileName)`"" $file.Disposition
    $unsigned = Invoke-Storage ([uri][string]$link.Body.url).GetLeftPart([System.UriPartial]::Path)
    Assert-Step '31 the same object without the signature is rejected' 403 $unsigned.Status

    # Nobody else sees the export - not even that it exists.
    Assert-Step '32 the second manager: detail -> 404' 404 (Invoke-Api GET "/api/reports/exports/$exportId" $other).Status
    Assert-Step '33 ... download -> 404' 404 (Invoke-Api GET "/api/reports/exports/$exportId/download" $other).Status
    Assert-Step '34 ... and an empty list' 0 @((Invoke-Api GET '/api/reports/exports' $other).Body.items).Count
    Assert-Step '35 the administrator: detail -> 404' 404 (Invoke-Api GET "/api/reports/exports/$exportId" $admin).Status

    # The inventory export.
    $inventory = Invoke-Api POST '/api/reports/inventory/export' $manager @{ search = $sku; lowStockOnly = $false; timeZone = $timeZone }
    Assert-Step '36 inventory export -> 202' 202 $inventory.Status
    $inventoryId = [string]$inventory.Body.taskId
    Assert-Step "37 ... built within $ExportWaitSeconds s" 'Succeeded' (Wait-Export $inventoryId $manager $ExportWaitSeconds).Status
    $inventoryDetail = (Invoke-Api GET "/api/reports/exports/$inventoryId" $manager).Body
    Assert-Step '38 ... one row: the smoke product' 1 $inventoryDetail.rowCount
    $inventoryLink = Invoke-Api GET "/api/reports/exports/$inventoryId/download" $manager
    $inventoryFile = Invoke-Storage ([string]$inventoryLink.Body.url)
    Assert-Step '39 ... its file starts with PK and is an attachment' $true ((Test-StartsWithPk $inventoryFile.Bytes) -and ($inventoryFile.Disposition -clike 'attachment; filename="inventory_*.xlsx"'))
    $own = @((Invoke-Api GET '/api/reports/exports' $manager).Body.items)
    Assert-Step '40 the requester lists both exports, newest first' "$inventoryId $exportId" "$($own[0].taskId) $($own[1].taskId)"

    # The request is audited: who asked for which report.
    $events = @((Invoke-Api GET "/api/system-events?eventType=report.export_requested&taskId=$exportId" $manager).Body.items)
    Assert-Step '41 report.export_requested in the system events' 1 $events.Count
    Assert-Step '42 ... with the requester''s e-mail' $managerEmail ([string]$events[0].actorEmail)
    Assert-Step '43 ... and the report type' 'Orders' ([string]$events[0].details.reportType)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke accounts are locked, also after a failed step.
    $step = 44
    foreach ($user in $users) {
        if ($admin -and $user -and $user.Body) {
            Assert-Step ('{0:00} lock smoke account {1}' -f $step, ($step - 43)) 200 (Invoke-Api POST "/api/users/$($user.Body.userId)/lock" $admin).Status
            $step++
        }
    }
}
Complete-Smoke
