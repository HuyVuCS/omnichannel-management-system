# Smoke check for task 021 (Reports page: what the page asks the API for - an export requested, built, listed and
# downloaded - and the end of an export's life as far as it can be checked without waiting for the retention time).
# Needs PostgreSQL and RabbitMQ running (README section 1), migration ReportExports applied (README section 4), the
# object storage emulator up, and its keys in the user-secrets of the API AND of the Workers host
# (scripts\setup-object-storage.ps1 - README "Reports and Excel export").
# Uses a running API / Workers host or - for the local default only - builds and starts them, and stops what it started.
# When the storage does not answer, the script fails with a step that says so. It never prints a download link: a
# presigned link is a credential until it expires. The smoke accounts are locked, also after a failed step.
# Ends with the frontend build and tests (after the hosts this script started are stopped).
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-021.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

$ExportWaitSeconds = 120

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

# An xlsx file is a zip archive: it starts with the two bytes "PK".
function Test-StartsWithPk([byte[]]$Bytes) { return (($Bytes.Length -gt 2) -and ($Bytes[0] -eq 0x50) -and ($Bytes[1] -eq 0x4B)) }

$timeZone = 'Asia/Ho_Chi_Minh'
$admin = $null
$users = New-Object System.Collections.Generic.List[object]

try {
    Start-Smoke 'Task 021 - Reports page: request, list, live status source, download'
    Start-SmokeWorkers
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    $managerEmail = "smoke-021-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $users.Add($managerUser)
    $manager = Get-Token $managerEmail $password

    $staffEmail = "smoke-021-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $users.Add($staffUser)
    $staff = Get-Token $staffEmail $password
    $password = $null

    # The page is for Manager and Admin: its list refuses Staff.
    Assert-Step '03 staff: the list of exports is forbidden' 403 (Invoke-Api GET '/api/reports/exports' $staff).Status

    # What the inventory form of the page sends: the search text, "low stock only" and the browser's time zone.
    $sku = "SMOKE-021-$n"
    $product = Invoke-Api POST '/api/products' $manager @{ sku = $sku; name = "Smoke product 021-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '04 create smoke product' 201 $product.Status
    $accepted = Invoke-Api POST '/api/reports/inventory/export' $manager @{ search = $sku; lowStockOnly = $false; timeZone = $timeZone }
    Assert-Step '05 manager: inventory export -> 202' 202 $accepted.Status
    $exportId = [string]$accepted.Body.taskId

    $waited = Wait-Export $exportId $manager $ExportWaitSeconds
    Assert-Step '06 the Report Export Worker reached the object storage (is the emulator up, do the Workers have its keys? README "Reports and Excel export")' $true (-not $waited.StorageDown)
    if ($waited.StorageDown) { throw 'The Report Export Worker cannot reach the object storage: start it, run scripts\setup-object-storage.ps1, restart the Workers host and run the script again.' }
    Assert-Step "07 the export is built within $ExportWaitSeconds s (is the Workers host running?)" 'Succeeded' $waited.Status

    # The table of the page: the requester's exports, newest first, with what the row shows.
    $list = Invoke-Api GET '/api/reports/exports?page=1&pageSize=20' $manager
    Assert-Step '08 the list of the requester -> 200' 200 $list.Status
    $row = @($list.Body.items) | Where-Object { [string]$_.taskId -ceq $exportId }
    Assert-Step '09 ... shows the export with status Succeeded' 'Succeeded' ([string]$row.status)
    Assert-Step '10 ... as an inventory report with one row and a size' $true (([string]$row.reportType -ceq 'Inventory') -and ($row.rowCount -eq 1) -and ([long]$row.sizeBytes -gt 0))
    Assert-Step '11 ... with the filters and the time zone of the request in its parameters' "$sku False $timeZone" "$($row.parameters.search) $($row.parameters.lowStockOnly) $($row.parameters.timeZone)"
    Assert-Step '12 the filter reportType=Orders leaves it out' 0 @(@((Invoke-Api GET '/api/reports/exports?reportType=Orders' $manager).Body.items) | Where-Object { [string]$_.taskId -ceq $exportId }).Count

    # The download button: a link for a few minutes, followed by the browser without the API's token.
    $link = Invoke-Api GET "/api/reports/exports/$exportId/download" $manager
    Assert-Step '13 download link -> 200' 200 $link.Status
    $file = Invoke-Storage ([string]$link.Body.url)
    Assert-Step '14 GET the link (no Authorization header) -> 200' 200 $file.Status
    Assert-Step '15 ... the bytes start with PK (an xlsx file)' $true (Test-StartsWithPk $file.Bytes)
    Assert-Step '16 ... served as an attachment (the browser saves it, the page stays)' $true ($file.Disposition -clike 'attachment; filename="inventory_*.xlsx"')

    # The retry of the task detail page: only a Failed task can be retried - a finished export is not one.
    $retry = Invoke-ApiProblem POST "/api/processing/tasks/$exportId/retry" $manager $null 'en'
    Assert-Step '17 retry of the succeeded export task -> 409' 409 $retry.Status
    Assert-Step '18 ... task_not_failed' 'task_not_failed' ([string]$retry.Body.errorCode)

    # The labels of the page's neighbours: the dashboard lists the task type, the audit names the requester.
    $summary = Invoke-Api GET '/api/dashboard/summary' $staff
    Assert-Step '19 dashboard summary -> 200' 200 $summary.Status
    $types = @($summary.Body.processing | ForEach-Object { [string]$_.taskType })
    Assert-Step '20 ... its processing section lists ReportExport' $true ($types -ccontains 'ReportExport')
    $events = @((Invoke-Api GET "/api/system-events?eventType=report.export_requested&taskId=$exportId" $manager).Body.items)
    Assert-Step '21 report.export_requested names the requester and the report' "$managerEmail Inventory" "$($events[0].actorEmail) $($events[0].details.reportType)"
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # The smoke accounts are locked, also after a failed step.
    $step = 22
    foreach ($user in $users) {
        if ($admin -and $user -and $user.Body) {
            Assert-Step ('{0:00} lock smoke account {1}' -f $step, ($step - 21)) 200 (Invoke-Api POST "/api/users/$($user.Body.userId)/lock" $admin).Status
            $step++
        }
    }
}
Invoke-FrontendChecks 24
Complete-Smoke
