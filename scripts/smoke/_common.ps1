# Shared helpers for smoke checks. Windows PowerShell 5.1 and PowerShell 7 compatible.
# ASCII only on purpose: Windows PowerShell 5.1 misreads UTF-8 files without a BOM.
# Secrets: the admin credentials are read from backend/src/Omnichannel.Api/.env (git-ignored) and never printed.
# Another environment (not the local development one): set SMOKE_BASE_URL, SMOKE_ADMIN_EMAIL and SMOKE_ADMIN_PASSWORD.
# Hosts are started only for the local default (SMOKE_BASE_URL not set): a host started here runs with the development
# user-secrets, so it must never be started on behalf of another environment. With SMOKE_BASE_URL set, the API and the
# Workers host of that environment must already be running.

$ErrorActionPreference = 'Stop'
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:ForeignEnvironment = [bool]$env:SMOKE_BASE_URL
$script:BaseUrl  = if ($env:SMOKE_BASE_URL) { $env:SMOKE_BASE_URL } else { 'http://localhost:5080' }
$script:Results  = New-Object System.Collections.Generic.List[object]
$script:StartedApi = $null
$script:StartedWorkers = $null
$script:WorkersLog = $null

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
    if ($env:SMOKE_ADMIN_EMAIL -and $env:SMOKE_ADMIN_PASSWORD) {
        return @{ Email = $env:SMOKE_ADMIN_EMAIL; Password = $env:SMOKE_ADMIN_PASSWORD }
    }
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

# The kind of a value as Assert-Step compares it: 'null', 'bool', 'number' or 'string'; $null for every other type.
function Get-AssertKind($Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [bool]) { return 'bool' }
    if ($Value -is [string]) { return 'string' }
    foreach ($type in [byte], [sbyte], [int16], [uint16], [int32], [uint32], [int64], [uint64], [decimal], [double], [single]) {
        if ($Value -is $type) { return 'number' }
    }
    return $null
}

function Get-AssertTypeName($Value) { if ($null -eq $Value) { return 'null' } else { return $Value.GetType().Name } }

# Strict equality: both values must be of the same kind. $null only equals $null, a boolean only a boolean, numbers are
# compared by value whatever their numeric type (ConvertFrom-Json returns Int32, Int64, Decimal and Double), strings
# case-sensitively. PowerShell's own -eq is too loose for a check: $true -eq 'any text', 1 -eq '1' and 'Open' -eq 'open'
# are all true. Any other type (array, object, hashtable, date) throws: the caller compares explicitly and passes the
# boolean result.
function Test-AssertEqual($Expected, $Actual) {
    $kind = Get-AssertKind $Expected
    $actualKind = Get-AssertKind $Actual
    if (($null -eq $kind) -or ($null -eq $actualKind)) {
        throw "Assert-Step compares null, booleans, numbers and strings only (expected: $(Get-AssertTypeName $Expected), actual: $(Get-AssertTypeName $Actual)); compare explicitly and pass the boolean result."
    }
    if ($kind -ne $actualKind) { return $false }
    if ($kind -eq 'null') { return $true }
    if ($kind -eq 'string') { return ($Expected -ceq $Actual) }
    if ($kind -eq 'bool') { return ([bool]$Expected -eq [bool]$Actual) }
    foreach ($value in $Expected, $Actual) {
        if (($value -is [double]) -or ($value -is [single])) { return ([double]$Expected -eq [double]$Actual) }
    }
    return ([decimal]$Expected -eq [decimal]$Actual)
}

# Records one check. A failed step shows both values with their type names.
function Assert-Step([string]$Step, $Expected, $Actual) {
    $ok = Test-AssertEqual $Expected $Actual
    if (-not $ok) {
        $Expected = "$Expected [$(Get-AssertTypeName $Expected)]"
        $Actual = "$Actual [$(Get-AssertTypeName $Actual)]"
    }
    $script:Results.Add([pscustomobject]@{ Step = $Step; Expected = $Expected; Actual = $Actual; OK = $ok })
}

function New-TestPassword { return 'Sm' + [guid]::NewGuid().ToString('N').Substring(0, 14) + '9Q' }

function Test-ApiUp {
    try { return ((Invoke-WebRequest -Uri "$script:BaseUrl/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) } catch { return $false }
}

# Uses a running API if there is one; otherwise, for the local default only, builds and starts it and stops it again in
# Stop-Smoke. An unreachable API of another environment (SMOKE_BASE_URL) is an error: nothing is started for it.
function Start-Smoke([string]$Title) {
    Write-Host "== $Title =="
    if (Test-ApiUp) {
        Write-Host "Using the API already running at $script:BaseUrl (restart it first if it runs an older build)."
        return
    }
    if ($script:ForeignEnvironment) {
        Write-Host 'Start the API of that environment first: this script starts an API only for the local default (SMOKE_BASE_URL not set).'
        throw "API at $script:BaseUrl (SMOKE_BASE_URL) is not reachable"
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

# True when a Workers host runs on this machine (started with "dotnet run" or from its build output).
function Test-WorkersRunning {
    $found = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        ($_.Name -eq 'dotnet.exe' -or $_.Name -eq 'Omnichannel.Workers.exe') -and
        $_.CommandLine -and ($_.CommandLine -like '*Omnichannel.Workers*') -and ($_.CommandLine -notlike '* build *')
    }
    return (@($found).Count -gt 0)
}

# Uses a running Workers host if there is one; otherwise, for the local default only, builds and starts it (Development:
# the channel simulator's SKU rules are on), writes its output to a log file, and stops it again in Stop-Smoke. Needs the
# Workers user-secrets (README section 6). For another environment (SMOKE_BASE_URL) nothing is started: a Workers host
# found on this machine is assumed to be that environment's, and none at all is an error.
function Start-SmokeWorkers {
    if (Test-WorkersRunning) {
        Write-Host 'Using the Workers host already running (restart it first if it runs an older build).'
        return
    }
    if ($script:ForeignEnvironment) {
        Write-Host 'Start the Workers host of that environment first: this script starts one only for the local default (SMOKE_BASE_URL not set).'
        throw "No Workers host is running for $script:BaseUrl (SMOKE_BASE_URL)"
    }
    Write-Host 'Workers host not running - building and starting it for this check...'
    $proj = Join-Path $script:RepoRoot 'backend\src\Omnichannel.Workers'
    & dotnet build $proj -c Debug --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Workers build failed.' }
    $dll = Join-Path $proj 'bin\Debug\net10.0\Omnichannel.Workers.dll'
    $env:DOTNET_ENVIRONMENT = 'Development'
    $script:WorkersLog = Join-Path ([System.IO.Path]::GetTempPath()) ('omnichannel-workers-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    $script:StartedWorkers = Start-Process -FilePath 'dotnet' -ArgumentList "`"$dll`"" -WorkingDirectory $proj -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $script:WorkersLog -RedirectStandardError ($script:WorkersLog + '.err')
    Start-Sleep -Seconds 8
    if ($script:StartedWorkers.HasExited) {
        $script:StartedWorkers = $null
        throw "The Workers host stopped right after its start (log: $script:WorkersLog). Are its user-secrets set (README section 6)?"
    }
    Write-Host "Workers host started (log: $script:WorkersLog)."
}

function Stop-Smoke {
    if ($script:StartedWorkers -and -not $script:StartedWorkers.HasExited) {
        Stop-Process -Id $script:StartedWorkers.Id -Force
        [void]$script:StartedWorkers.WaitForExit(10000)
        Write-Host 'Workers host stopped.'
    }
    $script:StartedWorkers = $null
    if ($script:StartedApi -and -not $script:StartedApi.HasExited) {
        Stop-Process -Id $script:StartedApi.Id -Force
        [void]$script:StartedApi.WaitForExit(10000)
        Write-Host 'API stopped.'
    }
    $script:StartedApi = $null
}

# Runs npm in the frontend folder; returns the exit code. The output goes to a log file, which is named when npm fails
# (which test failed is in there).
function Invoke-Npm([string]$Arguments) {
    $frontend = Join-Path $script:RepoRoot 'frontend'
    $log = Join-Path ([System.IO.Path]::GetTempPath()) ('omnichannel-smoke-npm-' + ($Arguments -replace '[^a-z]', '') + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
    Push-Location $frontend
    try {
        cmd /c "npm $Arguments > `"$log`" 2>&1"
        $code = $LASTEXITCODE
        if ($code -ne 0) { Write-Host "npm $Arguments failed (exit code $code); output: $log" -ForegroundColor Yellow }
        return $code
    } finally {
        Pop-Location
    }
}

# Frontend production build and tests, recorded as the steps $FirstStep (npm ci, only when packages are missing),
# $FirstStep + 1 (build) and $FirstStep + 2 (tests). Call it after the API checks and the clean-up, right before
# Complete-Smoke: the hosts this script started are stopped first, because the test suite is memory-bound and times out
# next to them. A host this script did not start is left alone.
function Invoke-FrontendChecks([int]$FirstStep, [string[]]$RequiredPackages = @()) {
    try {
        Stop-Smoke
        if ((([uri]$script:BaseUrl).IsLoopback -and (Test-ApiUp)) -or (Test-WorkersRunning)) {
            Write-Host 'An API / Workers host that this script did not start is still running: the frontend checks run next to it and may be slow.' -ForegroundColor Yellow
        }
        $modules = Join-Path $script:RepoRoot 'frontend\node_modules'
        $missing = @(@('.') + $RequiredPackages | Where-Object { -not (Test-Path (Join-Path $modules $_)) }).Count
        if ($missing -gt 0) {
            Write-Host 'Installing frontend packages (npm ci)...'
            Assert-Step ('{0:00} npm ci' -f $FirstStep) 0 (Invoke-Npm 'ci')
        }
        Write-Host 'Building the frontend...'
        Assert-Step ('{0:00} npm run build' -f ($FirstStep + 1)) 0 (Invoke-Npm 'run build')
        Write-Host 'Running the frontend tests (several minutes)...'
        Assert-Step ('{0:00} npm test' -f ($FirstStep + 2)) 0 (Invoke-Npm 'test')
    }
    catch {
        Assert-Step "unexpected error in the frontend checks: $($_.Exception.Message)" 'no error' 'error'
    }
}

# The result table as text. OK is the first column, and every column has a fixed width: a long value continues on the
# next line inside its own column (-Wrap) and never pushes a column - the verdict least of all - out of the table.
$script:TableWidths = @{ OK = 5; Step = 64; Expected = 34; Actual = 34 }
function Format-SmokeTable($Results) {
    $columns = @(
        @{ Label = 'OK'; Expression = { "$($_.OK)" }; Width = $script:TableWidths.OK },
        @{ Label = 'Step'; Expression = { "$($_.Step)" }; Width = $script:TableWidths.Step },
        @{ Label = 'Expected'; Expression = { "$($_.Expected)" }; Width = $script:TableWidths.Expected },
        @{ Label = 'Actual'; Expression = { "$($_.Actual)" }; Width = $script:TableWidths.Actual }
    )
    return ($Results | Format-Table -Property $columns -Wrap | Out-String -Width 160)
}

function Complete-Smoke {
    Stop-Smoke
    Format-SmokeTable $script:Results | Write-Host
    $failed = @($script:Results | Where-Object { -not $_.OK }).Count
    if ($failed -eq 0) { Write-Host "PASSED: all $($script:Results.Count) checks." -ForegroundColor Green; exit 0 }
    Write-Host "FAILED: $failed of $($script:Results.Count) checks." -ForegroundColor Red
    exit 1
}
