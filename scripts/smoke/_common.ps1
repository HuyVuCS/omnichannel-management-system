# Shared helpers for smoke checks. Windows PowerShell 5.1 and PowerShell 7 compatible.
# ASCII only on purpose: Windows PowerShell 5.1 misreads UTF-8 files without a BOM.
# Secrets: the admin credentials are read from backend/src/Omnichannel.Api/.env (git-ignored) and never printed.

$ErrorActionPreference = 'Stop'
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:BaseUrl  = if ($env:SMOKE_BASE_URL) { $env:SMOKE_BASE_URL } else { 'http://localhost:5080' }
$script:Results  = New-Object System.Collections.Generic.List[object]
$script:StartedApi = $null

function Read-DotEnv([string]$path) {
    $values = @{}
    if (Test-Path $path) {
        foreach ($line in Get-Content $path) {
            if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') { $values[$Matches[1]] = $Matches[2].Trim('"') }
        }
    }
    return $values
}

function Get-AdminCredential {
    $envFile = Join-Path $script:RepoRoot 'backend\src\Omnichannel.Api\.env'
    $values = Read-DotEnv $envFile
    if ($values['BOOTSTRAP_ADMIN_EMAIL'] -and $values['BOOTSTRAP_ADMIN_PASSWORD']) {
        return @{ Email = $values['BOOTSTRAP_ADMIN_EMAIL']; Password = $values['BOOTSTRAP_ADMIN_PASSWORD'] }
    }
    Write-Host "No admin credentials in $envFile - asking instead."
    $email = Read-Host 'Admin email'
    $sec = Read-Host 'Admin password' -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))
    return @{ Email = $email; Password = $plain }
}

function Invoke-Api([string]$Method, [string]$Path, [string]$Token, $Body, [hashtable]$Headers) {
    $p = @{ Method = $Method; Uri = "$script:BaseUrl$Path"; UseBasicParsing = $true; ContentType = 'application/json'; Headers = @{} }
    if ($Token) { $p.Headers.Authorization = "Bearer $Token" }
    if ($Headers) { foreach ($k in $Headers.Keys) { $p.Headers[$k] = $Headers[$k] } }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 8) }
    try {
        $r = Invoke-WebRequest @p
        $json = if ($r.Content) { $r.Content | ConvertFrom-Json } else { $null }
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $json }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $null }
    }
}

# Like Invoke-Api, but an error answer keeps its body (the problem document: errorCode, errors), read as UTF-8.
# $Language sets Accept-Language: validation messages follow it.
function Invoke-ApiProblem([string]$Method, [string]$Path, [string]$Token, $Body, [string]$Language) {
    $p = @{ Method = $Method; Uri = "$script:BaseUrl$Path"; UseBasicParsing = $true; ContentType = 'application/json'; Headers = @{} }
    if ($Token) { $p.Headers.Authorization = "Bearer $Token" }
    if ($Language) { $p.Headers['Accept-Language'] = $Language }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 8) }
    try {
        $r = Invoke-WebRequest @p
        $json = if ($r.Content) { $r.Content | ConvertFrom-Json } else { $null }
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $json }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        if ($resp -is [System.Net.HttpWebResponse]) {
            # Windows PowerShell 5.1: read the body as UTF-8 ourselves.
            $stream = $resp.GetResponseStream()
            if ($stream.CanSeek) { $stream.Position = 0 }
            $text = (New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)).ReadToEnd()
        } else {
            $text = $_.ErrorDetails.Message
        }
        $json = if ($text) { $text | ConvertFrom-Json } else { $null }
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = $json }
    }
}

function Get-Token([string]$Email, [string]$Password) {
    $r = Invoke-Api POST '/api/auth/login' $null @{ email = $Email; password = $Password }
    if ($r.Status -ne 200) { throw "Login failed for a test account (HTTP $($r.Status))." }
    return $r.Body.accessToken
}

function Assert-Step([string]$Step, $Expected, $Actual) {
    $script:Results.Add([pscustomobject]@{ Step = $Step; Expected = $Expected; Actual = $Actual; OK = ($Expected -eq $Actual) })
}

function New-TestPassword { return 'Sm' + [guid]::NewGuid().ToString('N').Substring(0, 14) + '9Q' }

function Test-ApiUp {
    try { return ((Invoke-WebRequest -Uri "$script:BaseUrl/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) } catch { return $false }
}

# Uses a running API if there is one; otherwise builds and starts it, and stops it again in Stop-Smoke.
function Start-Smoke([string]$Title) {
    Write-Host "== $Title =="
    if (Test-ApiUp) {
        Write-Host "Using the API already running at $script:BaseUrl (restart it first if it runs an older build)."
        return
    }
    Write-Host 'API not running - building and starting it for this check...'
    $proj = Join-Path $script:RepoRoot 'backend\src\Omnichannel.Api'
    & dotnet build $proj -c Debug --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
    $dll = Join-Path $proj 'bin\Debug\net10.0\Omnichannel.Api.dll'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $script:BaseUrl
    $script:StartedApi = Start-Process -FilePath 'dotnet' -ArgumentList "`"$dll`"" -WorkingDirectory $proj -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-ApiUp)) {
        if ((Get-Date) -gt $deadline) { Stop-Smoke; throw 'API did not become healthy within 60 s.' }
        Start-Sleep -Milliseconds 500
    }
    Write-Host 'API started.'
}

function Stop-Smoke {
    if ($script:StartedApi -and -not $script:StartedApi.HasExited) {
        Stop-Process -Id $script:StartedApi.Id -Force
        Write-Host 'API stopped.'
    }
    $script:StartedApi = $null
}

function Complete-Smoke {
    Stop-Smoke
    $script:Results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    $failed = @($script:Results | Where-Object { -not $_.OK }).Count
    if ($failed -eq 0) { Write-Host "PASSED: all $($script:Results.Count) checks." -ForegroundColor Green; exit 0 }
    Write-Host "FAILED: $failed of $($script:Results.Count) checks." -ForegroundColor Red
    exit 1
}
