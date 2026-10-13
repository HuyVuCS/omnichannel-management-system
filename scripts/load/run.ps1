# Load test of the reserve path (task 026b, Part E) against a THROWAWAY production-like stack - never production,
# never development: it writes thousands of orders. Windows PowerShell 5.1 compatible, ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File scripts\load\run.ps1 -Project omni026b -BaseUrl http://localhost:18080 `
#       -EnvFile docker\.env.prod.026b -Scenario all
#
# Per scenario (contention | spread | duplicates | all) it
#   1. creates a customer and the products with their stock through the API (as the stack's first Admin),
#   2. runs scripts\load\reserve.js with k6 - the official image, pinned by digest, as a container on the stack's edge
#      network (nothing is installed; the access token reaches it through the environment, not the command line),
#   3. waits until no task is unfinished and no message waits, sampling "docker stats" of postgres, rabbitmq and workers,
#   4. prints the numbers of the intake (k6), of the Order Worker (task timestamps) and the invariants
#      (scripts\load\invariants.sql, run inside the stack's PostgreSQL container).
# The exit code is 1 when an expectation of a scenario or an invariant does not hold.
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$EnvFile,
    [ValidateSet('contention', 'spread', 'duplicates', 'all')][string]$Scenario = 'all',
    [int]$DrainSeconds = 600
)

$ErrorActionPreference = 'Stop'
# The image the scripts were written for; "docker buildx imagetools inspect grafana/k6:<tag>" shows the digest of a tag.
$K6Image = 'grafana/k6:1.3.0@sha256:3ddc8b1a33a2c3d8edc6e99b6a762ae36cba08788463458f5e6a7703e14eb77d'

if (($Project -eq 'omnichannel-prod') -or ($Project -eq 'omnichannel')) { throw 'A load test never runs against production or development: give the compose project of a throwaway stack.' }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$envPath = (Resolve-Path $EnvFile).Path
Set-Location $repoRoot
$BaseUrl = $BaseUrl.TrimEnd('/')
$failures = New-Object System.Collections.Generic.List[string]

function Invoke-Docker([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $lines = @(& docker @Arguments 2>$null) } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "docker $($Arguments[0]) failed (exit code $LASTEXITCODE)." }
    return $lines
}

function Get-ServiceContainers([string]$Service) {
    return @(Invoke-Docker @('ps', '-q', '--filter', "label=com.docker.compose.project=$Project", '--filter', "label=com.docker.compose.service=$Service") | Where-Object { $_ })
}

function Read-EnvValue([string]$Name) {
    foreach ($line in [System.IO.File]::ReadAllLines($envPath)) {
        if ($line -cmatch "^$Name=(.*)$") { return $Matches[1] }
    }
    throw "$Name is not in the environment file."
}

function Invoke-Api([string]$Method, [string]$Path, $Body) {
    $p = @{ Method = $Method; Uri = "$BaseUrl$Path"; ContentType = 'application/json'; Headers = @{ Authorization = "Bearer $script:Token" } }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 8) }
    return Invoke-RestMethod @p
}

# A statement file run inside the stack's PostgreSQL container (the file is copied: no quoting through the shells).
function Invoke-Sql([string]$Sql, [switch]$Table) {
    $file = Join-Path ([System.IO.Path]::GetTempPath()) ('omni-load-' + [guid]::NewGuid().ToString('N') + '.sql')
    [System.IO.File]::WriteAllText($file, $Sql, (New-Object System.Text.UTF8Encoding($false)))
    try {
        [void](Invoke-Docker @('cp', $file, "$($script:Postgres):/tmp/omni-load.sql"))
        $format = if ($Table) { @() } else { @('-At') }
        $rows = @(Invoke-Docker (@('exec', $script:Postgres, 'psql', '-h', '127.0.0.1', '-U', 'postgres', '-d', 'omnichannel', '-v', 'ON_ERROR_STOP=1') + $format + @('-f', '/tmp/omni-load.sql')))
        # The comma keeps a single row an array: unrolled, its [0] would be the first character.
        return , $rows
    } finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

function Expect([string]$What, $Expected, $Actual) {
    $ok = ([string]$Expected -ceq [string]$Actual)
    Write-Host ("  {0}  {1}: expected {2}, actual {3}" -f $(if ($ok) { 'OK  ' } else { 'FAIL' }), $What, $Expected, $Actual)
    if (-not $ok) { $script:failures.Add("$($script:Current): $What - expected $Expected, actual $Actual") }
}

$script:Postgres = @(Get-ServiceContainers 'postgres')[0]
$rabbit = @(Get-ServiceContainers 'rabbitmq')[0]
$workers = @(Get-ServiceContainers 'workers')
$proxy = @(Get-ServiceContainers 'proxy')[0]
if ((-not $script:Postgres) -or (-not $rabbit) -or (-not $proxy) -or ($workers.Count -lt 1)) { throw "The stack $Project is not running (postgres, rabbitmq, proxy and workers are needed)." }
$network = "${Project}_edge"
$watched = @($script:Postgres, $rabbit) + $workers

$login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/login" -ContentType 'application/json' -Body (@{ email = (Read-EnvValue 'BOOTSTRAP_ADMIN_EMAIL'); password = (Read-EnvValue 'BOOTSTRAP_ADMIN_PASSWORD') } | ConvertTo-Json)
$script:Token = [string]$login.accessToken
if (-not $script:Token) { throw 'The login of the first Admin failed.' }

$plan = @{
    contention = @{ Products = 1; Stock = 100 }
    spread     = @{ Products = 50; Stock = 200 }
    duplicates = @{ Products = 10; Stock = 100 }
}
$names = if ($Scenario -eq 'all') { @('contention', 'spread', 'duplicates') } else { @($Scenario) }
$run = [guid]::NewGuid().ToString('N').Substring(0, 8)
Write-Host "Load test $run against $Project ($($workers.Count) Workers), k6 image $K6Image"

foreach ($name in $names) {
    $script:Current = $name
    Write-Host ''
    Write-Host "== $name =="

    # 1. Data: one customer, the products, their stock in one import.
    $customer = Invoke-Api POST '/api/customers' @{ name = "Load customer $run $name"; phone = $null; email = "load-$run-$name@example.com"; address = $null }
    $productIds = @()
    foreach ($i in 1..$plan[$name].Products) {
        $productIds += [string](Invoke-Api POST '/api/products' @{ sku = "LOAD-$run-$name-$i".ToUpperInvariant(); name = "Load product $run $name $i"; price = 10000; lowStockThreshold = 0 }).productId
    }
    [void](Invoke-Api POST '/api/inventory/import' @{ documentNumber = "LOAD-$run-$name"; documentSeries = $null; items = @($productIds | ForEach-Object { @{ productId = $_; stock = $plan[$name].Stock } }) })
    # The import's own messages (inventory.changed) are consumed before the test starts.
    Start-Sleep -Seconds 3

    # 2. k6, with "docker stats" sampled next to it until the queues are empty.
    $statsFile = Join-Path ([System.IO.Path]::GetTempPath()) "omni-load-$run-$name.stats"
    $sampler = Start-Job -ArgumentList (, $watched), $statsFile -ScriptBlock {
        param($containers, $file)
        while ($true) {
            $lines = & docker stats --no-stream --format '{{.Name}};{{.CPUPerc}};{{.MemUsage}}' @containers 2>$null
            if ($lines) { Add-Content -Path $file -Value $lines }
            Start-Sleep -Seconds 2
        }
    }
    $env:TOKEN = $script:Token
    $summaryFile = Join-Path ([System.IO.Path]::GetTempPath()) "omni-load-$run-$name.json"
    $k6 = "docker run --rm -i --network $network -e TOKEN -e BASE_URL=http://proxy:8080 -e SCENARIO=$name -e RUN_ID=$run -e CUSTOMER_ID=$($customer.customerId) -e PRODUCT_IDS=$($productIds -join ',') $K6Image run --quiet --no-usage-report -"
    $started = Get-Date
    cmd /c "$k6 < scripts\load\reserve.js > `"$summaryFile`" 2>nul"
    $k6Exit = $LASTEXITCODE
    $env:TOKEN = $null
    if ($k6Exit -ne 0) { Stop-Job $sampler ; Remove-Job $sampler ; throw "k6 ended with exit code $k6Exit." }
    $summary = Get-Content $summaryFile -Raw | ConvertFrom-Json

    # 3. Until nothing is unfinished and nothing waits.
    $deadline = (Get-Date).AddSeconds($DrainSeconds)
    do {
        Start-Sleep -Seconds 2
        $open = [int](Invoke-Sql 'select (select count(*) from "ProcessingTasks" where "Status" in (''Pending'', ''InProgress'', ''RetryScheduled'')) + (select count(*) from "OutboxEvents" where "Status" = ''Pending'');')[0]
    } while (($open -gt 0) -and ((Get-Date) -lt $deadline))
    $drained = Get-Date
    Stop-Job $sampler ; Remove-Job $sampler

    # 4. Numbers.
    $prefix = "LOAD-$run-$name-"
    $byStatus = @{}
    foreach ($row in (Invoke-Sql "select `"Status`", count(*) from `"Orders`" where `"ExternalOrderId`" like '$prefix%' group by 1;")) {
        $parts = $row.Split('|') ; $byStatus[$parts[0]] = [int]$parts[1]
    }
    $orders = ($byStatus.Values | Measure-Object -Sum).Sum
    $worker = (Invoke-Sql @"
select count(*),
       round((extract(epoch from percentile_cont(0.5) within group (order by t."UpdatedAt" - t."CreatedAt")) * 1000)::numeric),
       round((extract(epoch from percentile_cont(0.95) within group (order by t."UpdatedAt" - t."CreatedAt")) * 1000)::numeric),
       round((extract(epoch from max(t."UpdatedAt" - t."CreatedAt")) * 1000)::numeric),
       round((count(*) / greatest(extract(epoch from max(t."UpdatedAt") - min(t."CreatedAt")), 0.001))::numeric, 1),
       (select count(*) from "ProcessingAttempts" a join "ProcessingTasks" t2 on t2."TaskId" = a."TaskId" join "Orders" o2 on o2."OrderId" = t2."OrderId"
        where t2."TaskType" = 'OrderProcessing' and o2."ExternalOrderId" like '$prefix%')
from "ProcessingTasks" t join "Orders" o on o."OrderId" = t."OrderId"
where t."TaskType" = 'OrderProcessing' and o."ExternalOrderId" like '$prefix%';
"@)[0].Split('|')
    $stock = (Invoke-Sql "select sum(i.`"TotalStock`"), sum(i.`"ReservedStock`"), sum(i.`"AvailableStock`") from `"Inventories`" i join `"Products`" p on p.`"ProductId`" = i.`"ProductId`" where p.`"Sku`" like '$($prefix.ToUpperInvariant())%';")[0]
    # The last inventory.changed messages are still on their way to the stock-sync consumers when the tasks are done:
    # the queues get half a minute to empty. A message in a dead-letter queue never leaves it.
    $queueDeadline = (Get-Date).AddSeconds(30)
    do {
        $queues = @(Invoke-Docker @('exec', $rabbit, 'rabbitmqctl', '-q', 'list_queues', 'name', 'messages') | Where-Object { $_ -match '\s[1-9]\d*$' })
        if ($queues.Count -gt 0) { Start-Sleep -Seconds 2 }
    } while (($queues.Count -gt 0) -and ((Get-Date) -lt $queueDeadline))

    Write-Host ("  intake: {0} requests in {1:N1} s = {2:N1}/s; answer p50 {3:N0} ms, p95 {4:N0} ms, p99 {5:N0} ms, max {6:N0} ms; dropped iterations {7}" -f `
        $summary.requests, $summary.testSeconds, $summary.requestsPerSecond, $summary.durationMs.p50, $summary.durationMs.p95, $summary.durationMs.p99, $summary.durationMs.max, $summary.droppedIterations)
    $statuses = @($summary.statuses.PSObject.Properties | Where-Object { $_.Value -gt 0 } | ForEach-Object { "$($_.Name): $($_.Value)" }) -join ', '
    Write-Host "  intake answers: $statuses"
    Write-Host ("  Order Worker: {0} tasks, intake -> finished p50 {1} ms, p95 {2} ms, max {3} ms; {4} tasks/s over the run; {5} attempts; drained {6:N0} s after k6 ended" -f `
        $worker[0], $worker[1], $worker[2], $worker[3], $worker[4], $worker[5], (($drained - $started).TotalSeconds - $summary.testSeconds))
    Write-Host "  orders: $(@($byStatus.Keys | Sort-Object | ForEach-Object { "$_ $($byStatus[$_])" }) -join ', '); stock of the products (total|reserved|available): $stock"
    if (Test-Path $statsFile) {
        $samples = Get-Content $statsFile | ForEach-Object { $p = $_.Split(';') ; [pscustomobject]@{ Name = $p[0]; Cpu = [double]($p[1].TrimEnd('%')); Mem = [double](($p[2] -split '/')[0] -replace '[^0-9.]', '') ; Unit = (($p[2] -split '/')[0] -replace '[0-9. ]', '') } }
        foreach ($group in ($samples | Group-Object Name | Sort-Object Name)) {
            $cpu = ($group.Group | Measure-Object Cpu -Maximum -Average)
            $mem = ($group.Group | Measure-Object Mem -Maximum)
            Write-Host ("  {0}: CPU avg {1:N0} %, max {2:N0} %; memory max {3:N0} {4} ({5} samples)" -f $group.Name, $cpu.Average, $cpu.Maximum, $mem.Maximum, $group.Group[0].Unit, $group.Count)
        }
        Remove-Item $statsFile -ErrorAction SilentlyContinue
    }
    Remove-Item $summaryFile -ErrorAction SilentlyContinue

    # 5. What the scenario must show.
    Expect 'unfinished tasks and waiting messages after the drain' 0 $open
    Expect 'queues and dead-letter queues that still hold a message' 0 $queues.Count
    Expect 'requests the intake did not accept (not 202)' 0 ($summary.requests - $summary.statuses.'202')
    switch ($name) {
        'contention' {
            Expect 'orders' 300 $orders
            Expect 'orders Reserved (the stock was 100)' 100 ([int]$byStatus['Reserved'])
            Expect 'orders refused for insufficient stock' 200 ([int]$byStatus['InsufficientStock'])
            Expect 'stock total|reserved|available' '100|100|0' $stock
        }
        'spread' {
            # 20 per second for 100 s: k6 starts 2,000 or 2,001 iterations (one at each end of the time).
            Expect 'requests at least' $true ($summary.requests -ge 2000)
            Expect 'orders = accepted requests' $summary.requests $orders
            Expect 'orders Reserved (the stock was enough for all)' $orders ([int]$byStatus['Reserved'])
            Expect 'iterations k6 could not start in time' 0 $summary.droppedIterations
        }
        'duplicates' {
            Expect 'requests (100 external order ids, twice each)' 200 $summary.requests
            Expect 'orders' 100 $orders
            Expect 'orders Reserved' 100 ([int]$byStatus['Reserved'])
            Expect 'stock total|reserved|available' '1000|100|900' $stock
        }
    }
    Expect 'attempts of the Order Worker = orders (no order processed twice)' $orders ([int]$worker[5])

    Write-Host '  invariants (violations must be 0):'
    foreach ($row in (Invoke-Sql ([System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'invariants.sql'))) | Where-Object { $_ -match '\|' })) {
        $parts = $row.Split('|')
        Write-Host ("    {0,5}  {1}" -f $parts[1].Trim(), $parts[0].Trim())
        if ([int]$parts[1] -ne 0) { $script:failures.Add("$name invariant: $($parts[0].Trim()) - $($parts[1].Trim()) violations") }
    }
}

Write-Host ''
if ($failures.Count -eq 0) { Write-Host 'PASSED: every expectation and every invariant holds.' ; exit 0 }
Write-Host "FAILED: $($failures.Count)" ; $failures | ForEach-Object { Write-Host "  $_" } ; exit 1
