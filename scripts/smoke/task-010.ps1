# Smoke check for task 010 (Part A: lockout vs. sessions, request localisation; Part B: frontend build and tests).
# Needs PostgreSQL running and Node.js (see frontend/.nvmrc); starts the API if needed.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-010.ps1
# Note: the login endpoint allows 10 attempts per minute and IP; this script uses 8. Wait a minute after another smoke run.
. (Join-Path $PSScriptRoot '_common.ps1')

# Content-Language of an API answer (also of error answers, which Invoke-Api does not expose).
function Get-ContentLanguage([string]$Token, [string]$AcceptLanguage) {
    $headers = @{ Authorization = "Bearer $Token"; 'Accept-Language' = $AcceptLanguage }
    try {
        $r = Invoke-WebRequest -Method POST -Uri "$script:BaseUrl/api/users" -UseBasicParsing -ContentType 'application/json' -Headers $headers -Body '{"email":"","password":"","role":""}'
        return "unexpected HTTP $([int]$r.StatusCode)"
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        if ([int]$resp.StatusCode -ne 400) { return "unexpected HTTP $([int]$resp.StatusCode)" }
        # Windows PowerShell 5.1: HttpWebResponse; PowerShell 7: HttpResponseMessage.
        if ($resp -is [System.Net.HttpWebResponse]) { return [string]$resp.Headers['Content-Language'] }
        return [string]($resp.Content.Headers.ContentLanguage -join ',')
    }
}

try {
    Start-Smoke 'Task 010 - sessions survive a temporary lockout, localisation, frontend'
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null

    $pw = New-TestPassword
    $n = Get-Random -Maximum 99999
    $email = "smoke-010-$n@example.com"

    $c = Invoke-Api POST '/api/users' $admin @{ email = $email; password = $pw; role = 'Staff' }
    Assert-Step '01 admin creates Staff user' 201 $c.Status
    $uid = $c.Body.userId
    $staff = Get-Token $email $pw
    Assert-Step '02 Staff token works' 200 (Invoke-Api GET '/api/products' $staff).Status

    # Temporary lockout: 5 wrong passwords (anyone who knows the e-mail can do this).
    $wrong = 0
    for ($i = 0; $i -lt 5; $i++) {
        if ((Invoke-Api POST '/api/auth/login' $null @{ email = $email; password = 'Wrong-' + (New-TestPassword) }).Status -eq 401) { $wrong++ }
    }
    Assert-Step '03 five wrong passwords are rejected' 5 $wrong
    Assert-Step '04 correct password while locked out' 401 (Invoke-Api POST '/api/auth/login' $null @{ email = $email; password = $pw }).Status
    Assert-Step '05 user shown as locked' $true (Invoke-Api GET "/api/users/$uid" $admin).Body.isLocked
    Assert-Step '06 existing session still works (API)' 200 (Invoke-Api GET '/api/products' $staff).Status
    Assert-Step '07 existing session still works (/me)' 200 (Invoke-Api GET '/api/auth/me' $staff).Status

    Assert-Step '08 admin unlocks user' 200 (Invoke-Api POST "/api/users/$uid/unlock" $admin).Status
    Assert-Step '09 session still works after unlock' 200 (Invoke-Api GET '/api/products' $staff).Status

    # An admin lock does end the session (new security stamp).
    Assert-Step '10 admin locks user' 200 (Invoke-Api POST "/api/users/$uid/lock" $admin).Status
    Assert-Step '11 session after admin lock' 401 (Invoke-Api GET '/api/products' $staff).Status
    Assert-Step '12 /me after admin lock' 401 (Invoke-Api GET '/api/auth/me' $staff).Status

    # Validation messages follow Accept-Language (vi / en, default en).
    Assert-Step '13 Accept-Language vi' 'vi' (Get-ContentLanguage $admin 'vi')
    Assert-Step '14 Accept-Language en' 'en' (Get-ContentLanguage $admin 'en')
    Assert-Step '15 unsupported language falls back' 'en' (Get-ContentLanguage $admin 'fr-FR')
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
# Frontend: production build and tests (steps 16-18), after the API this script started is stopped.
Invoke-FrontendChecks 16
Complete-Smoke
