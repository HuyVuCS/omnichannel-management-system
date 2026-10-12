# Smoke check for task 025: the production stack (docker/docker-compose.prod.yml) through its reverse proxy.
# It never starts anything: the stack must be up, with two Workers instances (WORKERS_REPLICAS=2).
#
# Required environment (the script refuses to run against the local development default):
#   SMOKE_BASE_URL         the API origin of the proxy, e.g. http://localhost:8080
#   SMOKE_STORAGE_ORIGIN   the storage origin of the proxy (STORAGE_PUBLIC_URL of the stack), e.g. http://localhost:8081
#   SMOKE_ADMIN_EMAIL / SMOKE_ADMIN_PASSWORD   the bootstrap admin of that stack, or
#   SMOKE_ENV_FILE         the environment file of that stack (docker\.env.prod): the admin is read from it
# Optional: SMOKE_FRONTEND_ORIGIN (FRONTEND_ORIGIN of the stack; default http://localhost:4173).
#
# What it leaves behind: smoke products (one with an image), customers, two orders (one paid, with its invoice), one
# export file (swept after the retention time); the smoke mappings are deactivated and the smoke accounts locked, also
# after a failed step. It never prints a token or a presigned link.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-025.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $env:SMOKE_BASE_URL) { throw 'SMOKE_BASE_URL is required: this script checks a production stack through its proxy, never the development default.' }
if (-not $env:SMOKE_STORAGE_ORIGIN) { throw 'SMOKE_STORAGE_ORIGIN is required: the public origin of the storage site (STORAGE_PUBLIC_URL of the stack).' }
$storageOrigin = $env:SMOKE_STORAGE_ORIGIN.TrimEnd('/')
$frontendOrigin = if ($env:SMOKE_FRONTEND_ORIGIN) { $env:SMOKE_FRONTEND_ORIGIN } else { 'http://localhost:4173' }
$TaskWaitSeconds = 90
$SyncProducts = 8

# One request without following a redirect and without throwing on an error status. Returns Status and the response
# headers (Status 0 = no HTTP answer).
function Invoke-Raw([string]$Method, [string]$Url, [hashtable]$Headers, [byte[]]$Body, [string]$ContentType) {
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Method = $Method
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    if ($Headers) { foreach ($name in $Headers.Keys) { $request.Headers.Add($name, [string]$Headers[$name]) } }
    if ($null -ne $Body) {
        $request.ContentType = $ContentType
        $request.ContentLength = $Body.Length
        $stream = $request.GetRequestStream()
        $stream.Write($Body, 0, $Body.Length)
        $stream.Close()
    }
    $response = $null
    try { $response = $request.GetResponse() }
    catch [System.Net.WebException] { $response = $_.Exception.Response }
    if ($null -eq $response) { return [pscustomobject]@{ Status = 0; Headers = @{}; Bytes = [byte[]]@() } }
    try {
        $headers = @{}
        foreach ($name in $response.Headers.AllKeys) { $headers[$name.ToLowerInvariant()] = [string]$response.Headers[$name] }
        $buffer = New-Object System.IO.MemoryStream
        $response.GetResponseStream().CopyTo($buffer)
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $headers; Bytes = $buffer.ToArray() }
    } finally { $response.Close() }
}

function Get-Header($Answer, [string]$Name) { if ($Answer.Headers.ContainsKey($Name)) { return [string]$Answer.Headers[$Name] } else { return 'none' } }

# The origin (scheme://host[:port]) of a link, without anything of its path or query.
function Get-Origin([string]$Url) { if ($Url) { return ([uri]$Url).GetLeftPart([System.UriPartial]::Authority) } else { return 'no link' } }

function Get-FieldError($Answer, [string]$Field) {
    if ($Answer.Body -and $Answer.Body.errors -and $Answer.Body.errors.$Field) { return [string]@($Answer.Body.errors.$Field)[0] } else { return 'none' }
}

function Test-StartsWithPk([byte[]]$Bytes) { return (($Bytes.Length -gt 2) -and ($Bytes[0] -eq 0x50) -and ($Bytes[1] -eq 0x4B)) }

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

# Reads from a hub connection until a message contains $Marker or $Seconds are over. Returns $true when it was seen.
# Messages of the hub protocol end with the record separator 0x1E; several can arrive in one frame.
function Wait-HubMessage($Socket, [string]$Marker, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $buffer = New-Object byte[] 16384
    $text = ''
    while ((Get-Date) -lt $deadline) {
        $source = New-Object System.Threading.CancellationTokenSource
        $source.CancelAfter(2000)
        try {
            $receive = $Socket.ReceiveAsync((New-Object System.ArraySegment[byte] -ArgumentList @(, $buffer)), $source.Token)
            $receive.Wait()
            $text += [System.Text.Encoding]::UTF8.GetString($buffer, 0, $receive.Result.Count)
            if ($text.Contains($Marker)) { return $true }
        } catch {
            # A cancelled receive aborts the socket: nothing more can be read from it.
            if ($Socket.State -ne [System.Net.WebSockets.WebSocketState]::Open) { return $false }
        } finally { $source.Dispose() }
    }
    return $false
}

$png = [System.Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==')
$admin = $null
$manager = $null
$managerUser = $null
$website = $null
$mappings = New-Object System.Collections.Generic.List[object]
$socket = $null

try {
    Start-Smoke 'Task 025 - the production stack through its reverse proxy'

    # 1. The proxy: what it serves, and that nothing is redirected or refused that should not be.
    $health = Invoke-Raw GET "$script:BaseUrl/health" $null $null $null
    Assert-Step '01 /health through the proxy -> 200' 200 $health.Status
    Assert-Step '02 a path that is not /api or /health is not served -> 404' 404 (Invoke-Raw GET "$script:BaseUrl/swagger/index.html" $null $null $null).Status
    $anonymous = Invoke-Raw GET "$script:BaseUrl/api/auth/me" $null $null $null
    Assert-Step '03 an API request through the proxy is answered, not redirected (401 without a token)' '401 none' "$($anonymous.Status) $(Get-Header $anonymous 'location')"

    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    Assert-Step '04 the bootstrap admin logs in through the proxy' $true ([bool]$admin)

    # 2. CORS: the frontend origin and nobody else.
    $preflight = @{ 'Origin' = $frontendOrigin; 'Access-Control-Request-Method' = 'POST'; 'Access-Control-Request-Headers' = 'authorization,content-type,x-correlation-id' }
    $allowed = Invoke-Raw OPTIONS "$script:BaseUrl/api/products" $preflight $null $null
    Assert-Step "05 preflight from the frontend origin ($frontendOrigin) -> allowed" $frontendOrigin (Get-Header $allowed 'access-control-allow-origin')
    Assert-Step '06 ... with credentials (the hub negotiates with them)' 'true' (Get-Header $allowed 'access-control-allow-credentials')
    $preflight['Origin'] = 'https://someone-else.example'
    $refused = Invoke-Raw OPTIONS "$script:BaseUrl/api/products" $preflight $null $null
    Assert-Step '07 preflight from another origin -> no CORS header' 'none' (Get-Header $refused 'access-control-allow-origin')

    $n = Get-Random -Maximum 99999
    $managerEmail = "smoke-025-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '08 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    # 3. The realtime hub over a WebSocket through the proxy.
    $negotiate = Invoke-Api POST '/api/hubs/notifications/negotiate?negotiateVersion=1' $manager
    Assert-Step '09 hub negotiation through the proxy -> 200' 200 $negotiate.Status
    $transports = @($negotiate.Body.availableTransports | ForEach-Object { [string]$_.transport })
    Assert-Step '10 ... offers the WebSockets transport' $true ($transports -ccontains 'WebSockets')
    $hubUri = New-Object System.UriBuilder("$script:BaseUrl/api/hubs/notifications")
    $hubUri.Scheme = if ($hubUri.Scheme -eq 'https') { 'wss' } else { 'ws' }
    $hubUri.Query = "id=$([uri]::EscapeDataString([string]$negotiate.Body.connectionToken))&access_token=$([uri]::EscapeDataString($manager))"
    $socket = New-Object System.Net.WebSockets.ClientWebSocket
    $connected = $socket.ConnectAsync($hubUri.Uri, [System.Threading.CancellationToken]::None).Wait(15000)
    $hubUri = $null
    Assert-Step '11 a WebSocket to the hub is established through the proxy' 'Open' ([string]$socket.State)
    $handshake = [System.Text.Encoding]::UTF8.GetBytes('{"protocol":"json","version":1}' + [char]0x1E)
    [void]$socket.SendAsync((New-Object System.ArraySegment[byte] -ArgumentList @(, $handshake)), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).Wait(5000)
    Assert-Step '12 ... the hub accepts the handshake' $true (Wait-HubMessage $socket ('{}' + [char]0x1E) 10)

    # 4. Products, stock and mappings: an import that fans out into several stock-sync tasks on one channel queue.
    $website = [string]@((Invoke-Api GET '/api/channels' $manager).Body | Where-Object { $_.slug -eq 'website' })[0].channelId
    Assert-Step '13 the website channel exists' $true ([bool]$website)
    $productIds = New-Object System.Collections.Generic.List[string]
    $created = 0
    for ($i = 1; $i -le $SyncProducts; $i++) {
        $product = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-025-$n-$i"; name = "Smoke product 025-$n-$i"; price = 15000; lowStockThreshold = 0 }
        if ($product.Status -eq 201) { $created++; $productIds.Add([string]$product.Body.productId) }
    }
    Assert-Step "14 create $SyncProducts smoke products" $SyncProducts $created
    $initialTasks = New-Object System.Collections.Generic.List[string]
    foreach ($id in $productIds) {
        $mapping = Invoke-ApiProblem POST "/api/channels/$website/products" $manager @{ productId = $id; externalSku = "SMOKE-025-$n-EXT-$($mappings.Count + 1)" } 'en'
        if ($mapping.Status -eq 201) { $mappings.Add($mapping.Body); if ($mapping.Body.syncTaskId) { $initialTasks.Add([string]$mapping.Body.syncTaskId) } }
    }
    Assert-Step "15 map them on website (each starts an initial stock sync)" "$SyncProducts $SyncProducts" "$($mappings.Count) $($initialTasks.Count)"
    $initialDone = @($initialTasks | ForEach-Object { Get-TaskStatus (Wait-Task $_ $manager $TaskWaitSeconds) } | Where-Object { $_ -ceq 'Succeeded' }).Count
    Assert-Step '16 ... every initial sync succeeds (are the Workers of the stack running?)' $SyncProducts $initialDone

    $importCorrelation = "smoke-025-import-$n"
    $import = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "SMOKE-025-GRN-$n"; items = @($productIds | ForEach-Object { @{ productId = $_; stock = 10 } }) } @{ 'X-Correlation-Id' = $importCorrelation }
    Assert-Step "17 one import of 10 units for each of the $SyncProducts products" 200 $import.Status
    Assert-Step '18 ... its hint arrives over the WebSocket (no polling)' $true (Wait-HubMessage $socket '"target":"hints"' 15)

    # 5. Two Workers instances: every task of the import is processed exactly once, by either of them.
    $syncTasks = @()
    $deadline = (Get-Date).AddSeconds($TaskWaitSeconds)
    do {
        Start-Sleep -Seconds 1
        $syncTasks = @((Invoke-Api GET "/api/processing/tasks?taskType=StockSync&correlationId=$importCorrelation&pageSize=100" $manager).Body.items)
        $open = @($syncTasks | Where-Object { ($_.status -cne 'Succeeded') -and ($_.status -cne 'Failed') }).Count
    } while ((($syncTasks.Count -lt $SyncProducts) -or ($open -gt 0)) -and ((Get-Date) -lt $deadline))
    Assert-Step "19 the import became $SyncProducts stock-sync tasks on website" $SyncProducts $syncTasks.Count
    Assert-Step '20 ... all Succeeded' $SyncProducts @($syncTasks | Where-Object { $_.status -ceq 'Succeeded' }).Count
    Assert-Step '21 ... each with exactly one attempt (no task was processed twice)' $SyncProducts @($syncTasks | Where-Object { $_.attemptCount -eq 1 }).Count
    $instances = @($syncTasks + @($initialTasks | ForEach-Object { (Invoke-Api GET "/api/processing/tasks/$_" $manager).Body }) | ForEach-Object {
            if ($_.lastAttempt) { [string]$_.lastAttempt.workerInstanceId } elseif ($_.attempts) { [string]@($_.attempts)[0].workerInstanceId }
        } | Where-Object { $_ } | Sort-Object -Unique)
    Assert-Step '22 ... processed by two Workers instances (two instance ids in the attempts; scale workers=2)' 2 $instances.Count

    # 6. A product image: presigned for the public storage origin, uploaded there, served from there.
    $imageProduct = $productIds[0]
    $presign = Invoke-Api POST "/api/products/$imageProduct/image/presign" $manager @{ contentType = 'image/png'; sizeBytes = $png.Length }
    Assert-Step '23 presign a product image -> 200' 200 $presign.Status
    Assert-Step "24 ... the upload link is on the public storage origin ($storageOrigin)" $storageOrigin (Get-Origin ([string]$presign.Body.uploadUrl))
    $put = Invoke-Raw PUT ([string]$presign.Body.uploadUrl) @{ 'Origin' = $frontendOrigin } $png 'image/png'
    Assert-Step '25 PUT to the storage through the proxy (no Authorization header) -> 200' 200 $put.Status
    Assert-Step '26 ... with the CORS header a browser on the frontend origin needs' $frontendOrigin (Get-Header $put 'access-control-allow-origin')
    $confirm = Invoke-ApiProblem PUT "/api/products/$imageProduct/image" $manager @{ objectKey = $presign.Body.objectKey } 'en'
    Assert-Step '27 confirm the image (the API reads the object inside the network) -> 200' 200 $confirm.Status
    $imageUrl = [string]$confirm.Body.image.url
    Assert-Step '28 ... the image link is on the public storage origin' $storageOrigin (Get-Origin $imageUrl)
    $image = Invoke-Raw GET $imageUrl @{ 'Origin' = $frontendOrigin } $null $null
    Assert-Step '29 GET the image link -> 200 with the uploaded bytes' "200 $($png.Length)" "$($image.Status) $($image.Bytes.Length)"
    Assert-Step '30 ... with the CORS header' $frontendOrigin (Get-Header $image 'access-control-allow-origin')
    Assert-Step '30b ... as data that is never a page (task 026): its type, nosniff, the sandbox policy' "image/png | nosniff | default-src 'none'; sandbox" "$(Get-Header $image 'content-type') | $(Get-Header $image 'x-content-type-options') | $(Get-Header $image 'content-security-policy')"
    $otherOrigin = Invoke-Raw GET $imageUrl @{ 'Origin' = 'https://someone-else.example' } $null $null
    Assert-Step '31 ... and none for another origin' 'none' (Get-Header $otherOrigin 'access-control-allow-origin')
    # 6b. The bucket is private: without a signature the storage origin gives nothing and takes nothing.
    $objectUrl = ([uri]$imageUrl).GetLeftPart([System.UriPartial]::Path)
    $bucketUrl = "$storageOrigin/" + ([uri]$imageUrl).Segments[1]
    Assert-Step '32 unsigned GET of the image object (the link without its query) -> 403' 403 (Invoke-Raw GET $objectUrl $null $null $null).Status
    Assert-Step '33 unsigned listing of the bucket -> 403' '403 403' "$((Invoke-Raw GET $bucketUrl $null $null $null).Status) $((Invoke-Raw GET ($bucketUrl + '?list-type=2') $null $null $null).Status)"
    $unsigned = Invoke-Api POST "/api/products/$($productIds[2])/image/presign" $manager @{ contentType = 'image/png'; sizeBytes = $png.Length }
    $unsignedPut = Invoke-Raw PUT ([uri][string]$unsigned.Body.uploadUrl).GetLeftPart([System.UriPartial]::Path) $null $png 'image/png'
    Assert-Step '34 unsigned PUT of a new key (an upload link without its query) -> 403' '200 403' "$($unsigned.Status) $($unsignedPut.Status)"
    $absent = Invoke-ApiProblem PUT "/api/products/$($productIds[2])/image" $manager @{ objectKey = $unsigned.Body.objectKey } 'en'
    Assert-Step '35 ... and the key does not exist afterwards (the API finds no upload)' '400 No file has been uploaded for this key.' "$($absent.Status) $(Get-FieldError $absent 'objectKey')"
    $objectUrl = $null
    $unsigned = $null
    $imageUrl = $null

    # 7. An export: built by a Workers instance, stored inside the network, downloaded from the public storage origin.
    $export = Invoke-Api POST '/api/reports/inventory/export' $manager @{ search = "SMOKE-025-$n"; lowStockOnly = $false; timeZone = 'Asia/Ho_Chi_Minh' }
    Assert-Step '36 inventory export -> 202' 202 $export.Status
    $exportTask = Wait-Task ([string]$export.Body.taskId) $manager $TaskWaitSeconds
    Assert-Step "37 the export is built within $TaskWaitSeconds s" 'Succeeded' (Get-TaskStatus $exportTask)
    $link = Invoke-Api GET "/api/reports/exports/$($export.Body.taskId)/download" $manager
    Assert-Step '38 download link -> 200' 200 $link.Status
    Assert-Step '39 ... on the public storage origin' $storageOrigin (Get-Origin ([string]$link.Body.url))
    $file = Invoke-Raw GET ([string]$link.Body.url) $null $null $null
    Assert-Step '40 GET the link -> 200, the bytes start with PK (an xlsx file)' '200 True' "$($file.Status) $(Test-StartsWithPk $file.Bytes)"
    Assert-Step '41 ... served as an attachment (the stored Content-Disposition)' $true ((Get-Header $file 'content-disposition') -clike 'attachment*')
    Assert-Step '41b ... with nosniff and the sandbox policy (task 026)' "nosniff | default-src 'none'; sandbox" "$(Get-Header $file 'x-content-type-options') | $(Get-Header $file 'content-security-policy')"
    $link = $null

    # 8. An order, its payment, the invoice and the confirmation e-mail.
    $customer = Invoke-Api POST '/api/customers' $manager @{ name = "Smoke customer 025-$n"; phone = $null; email = "smoke-025-$n@example.com"; address = $null }
    Assert-Step '42 create smoke customer' 201 $customer.Status
    $submitted = Invoke-Api POST '/api/orders/website' $manager @{ externalOrderId = "SMOKE-025-A-$n"; customerId = $customer.Body.customerId; items = @(@{ productId = $productIds[1]; stock = 2 }) }
    Assert-Step '43 submit an order -> 202' 202 $submitted.Status
    $orderTask = Wait-Task ([string]$submitted.Body.taskId) $manager $TaskWaitSeconds
    $orderId = if ($orderTask) { [string]$orderTask.orderId } else { '' }
    Assert-Step '44 the Order Worker reserves it' 'Reserved' ([string](Invoke-Api GET "/api/orders/$orderId" $manager).Body.status)
    $paid = Invoke-ApiProblem POST "/api/orders/$orderId/payment" $manager @{ paymentMethod = 'Cash' } 'en'
    Assert-Step '45 the manager pays it -> 200' 200 $paid.Status
    Assert-Step '46 the invoice task succeeds (Invoice Worker)' 'Succeeded' (Get-TaskStatus (Wait-Task ([string]$paid.Body.invoiceTaskId) $manager $TaskWaitSeconds))
    Assert-Step '47 the e-mail task succeeds (Notification Worker)' 'Succeeded' (Get-TaskStatus (Wait-Task ([string]$paid.Body.emailTaskId) $manager $TaskWaitSeconds))
    Assert-Step '48 the order has its invoice number' $true ([string](Invoke-Api GET "/api/orders/$orderId" $manager).Body.invoice.number -cmatch '^SIM-\d{6}-[0-9A-F]{16}$')
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    if ($socket) { $socket.Dispose() }
    # The smoke mappings are deactivated and the smoke account is locked, also after a failed step.
    if ($manager -and $website -and ($mappings.Count -gt 0)) {
        $off = @($mappings | ForEach-Object { (Invoke-ApiProblem PUT "/api/channels/$website/products/$($_.channelProductId)" $manager @{ externalSku = [string]$_.externalSku; status = 'Inactive' } 'en').Status } | Where-Object { $_ -eq 200 }).Count
        Assert-Step '49 deactivate the smoke mappings' $mappings.Count $off
    }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '50 lock smoke account' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
}
Invoke-FrontendChecks 51
Complete-Smoke
