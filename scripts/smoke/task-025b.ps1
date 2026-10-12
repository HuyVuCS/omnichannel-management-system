# Smoke check for task 025b: the live system from outside - the frontend on its static host and the API origin.
# Read-only: it logs in nowhere and writes nothing. It never starts anything.
#
# Required environment:
#   SMOKE_BASE_URL          the public API origin, e.g. https://machine.tailnet.ts.net
#   SMOKE_FRONTEND_ORIGIN   the public origin of the frontend, e.g. https://project.vercel.app
#   SMOKE_STORAGE_ORIGIN    the public origin of the storage, e.g. https://machine.tailnet.ts.net:8443
#   SMOKE_EXPECT_HSTS       true / false: whether the API is expected to answer Strict-Transport-Security (true only
#                           when the proxy believes the tunnel's X-Forwarded-Proto - docs/runbook.md "Client addresses")
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-025b.ps1
. (Join-Path $PSScriptRoot '_common.ps1')
. (Join-Path $PSScriptRoot '_hardening.ps1')

if (-not $env:SMOKE_BASE_URL) { throw 'SMOKE_BASE_URL is required: the public API origin.' }
if (-not $env:SMOKE_FRONTEND_ORIGIN) { throw 'SMOKE_FRONTEND_ORIGIN is required: the public origin of the frontend.' }
if (-not $env:SMOKE_STORAGE_ORIGIN) { throw 'SMOKE_STORAGE_ORIGIN is required: the public origin of the storage.' }
$storageOrigin = $env:SMOKE_STORAGE_ORIGIN.TrimEnd('/')
if (($env:SMOKE_EXPECT_HSTS -ne 'true') -and ($env:SMOKE_EXPECT_HSTS -ne 'false')) { throw 'SMOKE_EXPECT_HSTS is required: true or false.' }
$frontendOrigin = $env:SMOKE_FRONTEND_ORIGIN.TrimEnd('/')
$expectHsts = ($env:SMOKE_EXPECT_HSTS -eq 'true')

# One request without following a redirect and without throwing on an error status (Status 0 = no HTTP answer).
function Invoke-Raw([string]$Method, [string]$Url, [hashtable]$Headers) {
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Method = $Method
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    if ($Headers) { foreach ($name in $Headers.Keys) { $request.Headers.Add($name, [string]$Headers[$name]) } }
    $response = $null
    try { $response = $request.GetResponse() }
    catch [System.Net.WebException] { $response = $_.Exception.Response }
    if ($null -eq $response) { return [pscustomobject]@{ Status = 0; Headers = @{}; Text = '' } }
    try {
        $headers = @{}
        foreach ($name in $response.Headers.AllKeys) { $headers[$name.ToLowerInvariant()] = [string]$response.Headers[$name] }
        $text = (New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)).ReadToEnd()
        return [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $headers; Text = $text }
    } finally { $response.Close() }
}

function Get-Header($Answer, [string]$Name) { if ($Answer.Headers.ContainsKey($Name)) { return [string]$Answer.Headers[$Name] } else { return 'none' } }

try {
    Write-Host '== Task 025b - the live system from outside =='

    # 1. The static host: the build, the fallback for deep links, the cache rule of hashed files.
    $index = Invoke-Raw GET "$frontendOrigin/" $null
    Assert-Step '01 the frontend origin serves the application (200, HTML)' '200 True' "$($index.Status) $((Get-Header $index 'content-type') -like 'text/html*')"
    $deep = Invoke-Raw GET "$frontendOrigin/orders" $null
    Assert-Step '02 a deep link (/orders) -> 200 with the same HTML (fallback, not a redirect)' '200 True none' "$($deep.Status) $($deep.Text -ceq $index.Text) $(Get-Header $deep 'location')"
    $asset = if ($index.Text -match '(?:src|href)="(/assets/[^"]+\.js)"') { $Matches[1] } else { '' }
    Assert-Step '03 the HTML refers to a script under /assets/' $true ([bool]$asset)
    $assetAnswer = Invoke-Raw GET "$frontendOrigin$asset" $null
    Assert-Step '04 ... served with Cache-Control public, max-age=31536000, immutable' '200 public, max-age=31536000, immutable' "$($assetAnswer.Status) $(Get-Header $assetAnswer 'cache-control')"
    Assert-Step '05 the HTML itself is not immutable' $false ((Get-Header $index 'cache-control') -like '*immutable*')
    $missing = Invoke-Raw GET "$frontendOrigin/assets/no-such-file-025b.js" $null
    Assert-Step '06 a file that is missing under /assets/ -> 404, never the HTML' 404 $missing.Status

    # 2. The API origin.
    $health = Invoke-Raw GET "$script:BaseUrl/health" $null
    Assert-Step '07 /health on the API origin -> 200' 200 $health.Status
    $anonymous = Invoke-Raw GET "$script:BaseUrl/api/auth/me" $null
    Assert-Step '08 an API request is answered, not redirected (401 without a token)' '401 none' "$($anonymous.Status) $(Get-Header $anonymous 'location')"
    Assert-Step "09 Strict-Transport-Security on an API answer: $(if ($expectHsts) { 'present' } else { 'absent' }) (SMOKE_EXPECT_HSTS)" $expectHsts ((Get-Header $anonymous 'strict-transport-security') -cne 'none')
    $plain = Invoke-Raw GET ($script:BaseUrl -replace '^https:', 'http:') $null
    $plainResult = if ($plain.Status -eq 0) { 'not served' }
        elseif (($plain.Status -ge 300) -and ($plain.Status -lt 400) -and ((Get-Header $plain 'location') -like 'https://*')) { 'redirected to https' }
        else { "served ($($plain.Status))" }
    Assert-Step "10 the API host without TLS is not served or redirects to https (observed: $plainResult)" $true ($plainResult -cne "served ($($plain.Status))")

    # 3. CORS: the frontend origin and nobody else - not even another project of the same static host.
    $preflight = @{ 'Origin' = $frontendOrigin; 'Access-Control-Request-Method' = 'POST'; 'Access-Control-Request-Headers' = 'authorization,content-type,x-correlation-id' }
    Assert-Step "11 preflight from the frontend origin ($frontendOrigin) -> allowed" $frontendOrigin (Get-Header (Invoke-Raw OPTIONS "$script:BaseUrl/api/products" $preflight) 'access-control-allow-origin')
    $other = ([uri]$frontendOrigin).Scheme + '://not-' + ([uri]$frontendOrigin).Host
    $preflight['Origin'] = $other
    Assert-Step "12 preflight from $other -> no CORS header" 'none' (Get-Header (Invoke-Raw OPTIONS "$script:BaseUrl/api/products" $preflight) 'access-control-allow-origin')

    # 4. Task 026: the headers of the static host and the policy the build wrote into the page.
    Assert-Step '13 the static host forbids framing, on the page and on a deep link' "frame-ancestors 'none' | frame-ancestors 'none'" "$(Get-Header $index 'content-security-policy') | $(Get-Header $deep 'content-security-policy')"
    Assert-Step '14 ... nosniff, a referrer policy and a permissions policy' 'nosniff | strict-origin-when-cross-origin | camera=(), microphone=(), geolocation=(), payment=()' "$(Get-Header $index 'x-content-type-options') | $(Get-Header $index 'referrer-policy') | $(Get-Header $index 'permissions-policy')"
    Assert-Step '15 ... and on a script under /assets/ (next to its cache rule)' 'nosniff' (Get-Header $assetAnswer 'x-content-type-options')
    $apiOrigin = ([uri]$script:BaseUrl).GetLeftPart([System.UriPartial]::Authority)
    $hubOrigin = $apiOrigin -replace '^http', 'ws'
    $policy = if ($index.Text -match '<meta http-equiv="Content-Security-Policy" content="([^"]*)"') { $Matches[1].Replace('&#39;', "'") } else { 'no policy in the page' }
    $expectedPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' $storageOrigin; connect-src 'self' $apiOrigin $hubOrigin $storageOrigin; object-src 'none'; base-uri 'self'; form-action 'self'"
    Assert-Step '16 the page carries the policy of the build: scripts of the build only, the API, its hub and the storage' $expectedPolicy $policy
    Assert-Step '17 ... before the first script of the page' $true (($index.Text.IndexOf('http-equiv="Content-Security-Policy"') -ge 0) -and ($index.Text.IndexOf('http-equiv="Content-Security-Policy"') -lt $index.Text.IndexOf('<script')))

    # 5. Task 026: the API and the storage origin (no account, nothing written).
    [void](Invoke-HardeningChecks $script:BaseUrl $storageOrigin 18)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
Complete-Smoke
