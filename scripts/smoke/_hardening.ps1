# Checks of task 026 that need no account and write nothing: the security headers of the API and of the storage origin
# and the limit of a request body. Shared by task-026.ps1 (a stack through its proxy) and task-025b.ps1 (the live system).
# Dot-source after _common.ps1. Windows PowerShell 5.1 compatible, ASCII only.

$script:ApiPolicy = "default-src 'none'; frame-ancestors 'none'"
$script:StoragePolicy = "default-src 'none'; sandbox"
# RequestBodyLimit.MaxBytes of the API. The proxy's API site allows 8 times as much (request_body max_size, 1 MiB), so
# a body between the two limits always reaches the API, which answers a 413 a page can read.
$script:BodyLimitBytes = 131072
$script:ProxyBodyLimitBytes = 8 * $script:BodyLimitBytes

# One request without following a redirect and without throwing on an error status (Status 0 = no HTTP answer).
# $Body with -Chunked is sent without a Content-Length.
function Invoke-HardeningRequest([string]$Method, [string]$Url, [byte[]]$Body, [switch]$Chunked) {
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Method = $Method
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $response = $null
    try {
        if ($null -ne $Body) {
            $request.ContentType = 'application/json'
            if ($Chunked) { $request.SendChunked = $true } else { $request.ContentLength = $Body.Length }
            $stream = $request.GetRequestStream()
            $stream.Write($Body, 0, $Body.Length)
            $stream.Close()
        }
        $response = $request.GetResponse()
    }
    catch [System.Net.WebException] { $response = $_.Exception.Response }
    catch [System.IO.IOException] { $response = $null }
    if ($null -eq $response) { return [pscustomobject]@{ Status = 0; Headers = @{}; Text = '' } }
    try {
        $headers = @{}
        foreach ($name in $response.Headers.AllKeys) { $headers[$name.ToLowerInvariant()] = [string]$response.Headers[$name] }
        $text = (New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)).ReadToEnd()
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $headers; Text = $text }
    } finally { $response.Close() }
}

# A body that is refused without being read: the answer can arrive while this client is still sending, and the
# connection is then closed under it (no HTTP answer, Status 0). That is this client, not the server: asked again.
# The checks post to the login endpoint (the only one that reads a body without an account), which this address may
# call ten times a minute: a 429 is that limit, not the answer under test - the minute is waited out, twice at most.
function Invoke-HardeningRefused([string]$Method, [string]$Url, [byte[]]$Body, [switch]$Chunked) {
    $waited = 0
    foreach ($try in 1..7) {
        $answer = Invoke-HardeningRequest $Method $Url $Body -Chunked:$Chunked
        if (($answer.Status -eq 429) -and ($waited -lt 2)) {
            Write-Host '   (the login limit of this address is reached: waiting 61 s)'
            Start-Sleep -Seconds 61
            $waited++
            continue
        }
        if ($answer.Status -ne 0) { return $answer }
        Start-Sleep -Milliseconds 300
    }
    return $answer
}

# "errorCode | the correlationId of the body is the X-Correlation-Id header" of a problem document.
function Get-ProblemSummary($Answer) {
    $problem = $null
    try { $problem = $Answer.Text | ConvertFrom-Json } catch { }
    $code = if ($problem) { [string]$problem.errorCode } else { 'no problem document' }
    $correlation = if ($problem -and $problem.correlationId) { ([string]$problem.correlationId -ceq (Get-HardeningHeader $Answer 'x-correlation-id')) } else { $false }
    return "$code | correlation id $correlation"
}

function Get-HardeningHeader($Answer, [string]$Name) { if ($Answer.Headers.ContainsKey($Name)) { return [string]$Answer.Headers[$Name] } else { return 'none' } }

# "status | nosniff | referrer policy | content security policy | no-store?" of an answer of the API.
function Get-ApiHeaderSummary($Answer) {
    $noStore = ((Get-HardeningHeader $Answer 'cache-control') -split ',\s*') -contains 'no-store'
    return "$($Answer.Status) | $(Get-HardeningHeader $Answer 'x-content-type-options') | $(Get-HardeningHeader $Answer 'referrer-policy') | $(Get-HardeningHeader $Answer 'content-security-policy') | no-store $noStore"
}

# Records the steps $FirstStep .. and returns the number of the next free step.
function Invoke-HardeningChecks([string]$ApiOrigin, [string]$StorageOrigin, [int]$FirstStep) {
    $n = $FirstStep
    $expected = "| nosniff | no-referrer | $script:ApiPolicy | no-store True"

    # B1: every kind of answer of the API carries the headers.
    Assert-Step ('{0:00} API 200 (/health): nosniff, no-referrer, the policy, no-store' -f $n) "200 $expected" (Get-ApiHeaderSummary (Invoke-HardeningRequest GET "$ApiOrigin/health")); $n++
    $anonymous = Invoke-HardeningRequest GET "$ApiOrigin/api/auth/me"
    Assert-Step ('{0:00} API 401 (no token): the same headers' -f $n) "401 $expected" (Get-ApiHeaderSummary $anonymous); $n++
    Assert-Step ('{0:00} ... and no Cross-Origin-Resource-Policy (the frontend is another site)' -f $n) 'none' (Get-HardeningHeader $anonymous 'cross-origin-resource-policy'); $n++

    # B2: a body above the API's limit is answered by the API itself - also through the proxy, whose own limit is
    # 8 times as high - and not dropped.
    $tooLarge = New-Object byte[] ($script:BodyLimitBytes + 1)
    for ($i = 0; $i -lt $tooLarge.Length; $i++) { $tooLarge[$i] = 32 }
    $announced = Invoke-HardeningRefused POST "$ApiOrigin/api/auth/login" $tooLarge
    Assert-Step ('{0:00} a body of limit + 1 bytes -> 413 of the API, with the same headers' -f $n) "413 $expected" (Get-ApiHeaderSummary $announced); $n++
    Assert-Step ('{0:00} ... as a problem document with the correlation id of the X-Correlation-Id header' -f $n) 'request_body_too_large | correlation id True' (Get-ProblemSummary $announced); $n++
    $chunked = Invoke-HardeningRefused POST "$ApiOrigin/api/auth/login" $tooLarge -Chunked
    Assert-Step ('{0:00} the same body without a Content-Length -> the same 413 of the API' -f $n) "413 $expected" (Get-ApiHeaderSummary $chunked); $n++
    Assert-Step ('{0:00} ... as the same problem document' -f $n) 'request_body_too_large | correlation id True' (Get-ProblemSummary $chunked); $n++
    # Above the proxy's limit: refused, by the proxy or by the API - only the status counts. A tunnel in front of the
    # proxy (Funnel on the live system) can report the connection the proxy closed under the rest of the body as 502
    # (seen once in ten live runs of task 026c; the proxy's log had 413): that is a refusal as well.
    $huge = New-Object byte[] ($script:ProxyBodyLimitBytes + 1)
    for ($i = 0; $i -lt $huge.Length; $i++) { $huge[$i] = 32 }
    $hugeAnnounced = (Invoke-HardeningRefused POST "$ApiOrigin/api/auth/login" $huge).Status
    Assert-Step ('{0:00} a body above 1 MiB (the limit of the proxy) is refused: 413 (502 from a tunnel)' -f $n) 'refused' $(if (@(413, 502) -contains $hugeAnnounced) { 'refused' } else { "status $hugeAnnounced" }); $n++
    $hugeChunked = (Invoke-HardeningRefused POST "$ApiOrigin/api/auth/login" $huge -Chunked).Status
    Assert-Step ('{0:00} ... and without a Content-Length' -f $n) 'refused' $(if (@(413, 502) -contains $hugeChunked) { 'refused' } else { "status $hugeChunked" }); $n++
    $atLimit = New-Object byte[] $script:BodyLimitBytes
    for ($i = 0; $i -lt $atLimit.Length; $i++) { $atLimit[$i] = 32 }
    $atLimit[0] = 123; $atLimit[1] = 125
    Assert-Step ('{0:00} a body of exactly the limit is read (400: an empty login)' -f $n) 400 (Invoke-HardeningRefused POST "$ApiOrigin/api/auth/login" $atLimit).Status; $n++

    # C1: whatever the storage origin answers is data, never a page.
    $storage = Invoke-HardeningRequest GET "$StorageOrigin/"
    Assert-Step ('{0:00} storage origin (unsigned request, 403): nosniff and the sandbox policy' -f $n) "403 | nosniff | $script:StoragePolicy" "$($storage.Status) | $(Get-HardeningHeader $storage 'x-content-type-options') | $(Get-HardeningHeader $storage 'content-security-policy')"; $n++

    return $n
}
