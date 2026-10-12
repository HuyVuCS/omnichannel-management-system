# Checks of task 026 that need no account and write nothing: the security headers of the API and of the storage origin
# and the limit of a request body. Shared by task-026.ps1 (a stack through its proxy) and task-025b.ps1 (the live system).
# Dot-source after _common.ps1. Windows PowerShell 5.1 compatible, ASCII only.

$script:ApiPolicy = "default-src 'none'; frame-ancestors 'none'"
$script:StoragePolicy = "default-src 'none'; sandbox"
# RequestBodyLimit.MaxBytes of the API = request_body max_size of the proxy's API site.
$script:BodyLimitBytes = 131072

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

    # B2: a body above the limit is answered, not dropped.
    $tooLarge = New-Object byte[] ($script:BodyLimitBytes + 1)
    for ($i = 0; $i -lt $tooLarge.Length; $i++) { $tooLarge[$i] = 32 }
    $announced = Invoke-HardeningRequest POST "$ApiOrigin/api/auth/login" $tooLarge
    Assert-Step ('{0:00} a body of limit + 1 bytes -> 413 with the same headers' -f $n) "413 $expected" (Get-ApiHeaderSummary $announced); $n++
    $problem = $null
    try { $problem = $announced.Text | ConvertFrom-Json } catch { }
    $code = if ($problem) { [string]$problem.errorCode } else { 'no problem document' }
    $correlation = if ($problem -and $problem.correlationId) { ([string]$problem.correlationId -ceq (Get-HardeningHeader $announced 'x-correlation-id')) } else { $false }
    Assert-Step ('{0:00} ... as a problem document: request_body_too_large' -f $n) 'request_body_too_large' $code; $n++
    Assert-Step ('{0:00} ... with the correlation id of the X-Correlation-Id header' -f $n) $true $correlation; $n++
    $chunked = Invoke-HardeningRequest POST "$ApiOrigin/api/auth/login" $tooLarge -Chunked
    Assert-Step ('{0:00} the same body without a Content-Length -> 413 (answered, not a dropped connection)' -f $n) 413 $chunked.Status; $n++
    $atLimit = New-Object byte[] $script:BodyLimitBytes
    for ($i = 0; $i -lt $atLimit.Length; $i++) { $atLimit[$i] = 32 }
    $atLimit[0] = 123; $atLimit[1] = 125
    Assert-Step ('{0:00} a body of exactly the limit is read (400: an empty login)' -f $n) 400 (Invoke-HardeningRequest POST "$ApiOrigin/api/auth/login" $atLimit).Status; $n++

    # C1: whatever the storage origin answers is data, never a page.
    $storage = Invoke-HardeningRequest GET "$StorageOrigin/"
    Assert-Step ('{0:00} storage origin (unsigned request, 403): nosniff and the sandbox policy' -f $n) "403 | nosniff | $script:StoragePolicy" "$($storage.Status) | $(Get-HardeningHeader $storage 'x-content-type-options') | $(Get-HardeningHeader $storage 'content-security-policy')"; $n++

    return $n
}
