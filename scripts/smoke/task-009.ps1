# Smoke check for task 009 (user management, token revocation). Needs PostgreSQL running; starts the API if needed.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-009.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

try {
    Start-Smoke 'Task 009 - user management'
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null

    $pw1 = New-TestPassword; $pw2 = New-TestPassword
    $n = Get-Random -Maximum 99999
    $email = "smoke-staff$n@example.com"

    $c = Invoke-Api POST '/api/users' $admin @{ email = $email; password = $pw1; role = 'Staff' }
    Assert-Step '01 admin creates Staff user' 201 $c.Status
    $uid = $c.Body.userId
    Assert-Step '02 duplicate e-mail (other case)' 409 (Invoke-Api POST '/api/users' $admin @{ email = $email.ToUpper(); password = $pw1; role = 'Staff' }).Status
    Assert-Step '03 weak password' 400 (Invoke-Api POST '/api/users' $admin @{ email = "smoke-weak$n@example.com"; password = 'abc'; role = 'Staff' }).Status

    $staff = Get-Token $email $pw1
    Assert-Step '04 Staff reads /api/products' 200 (Invoke-Api GET '/api/products' $staff).Status
    Assert-Step '05 Staff calls /api/users' 403 (Invoke-Api GET '/api/users' $staff).Status
    Assert-Step '06 /me shows Staff' 'Staff' (Invoke-Api GET '/api/auth/me' $staff).Body.roles[0]

    Assert-Step '07 admin locks user' 200 (Invoke-Api POST "/api/users/$uid/lock" $admin).Status
    Assert-Step '08 old token after lock' 401 (Invoke-Api GET '/api/products' $staff).Status
    Assert-Step '09 login while locked' 401 (Invoke-Api POST '/api/auth/login' $null @{ email = $email; password = $pw1 }).Status

    Assert-Step '10 admin unlocks user' 200 (Invoke-Api POST "/api/users/$uid/unlock" $admin).Status
    $staff2 = Get-Token $email $pw1
    Assert-Step '11 new login after unlock' 200 (Invoke-Api GET '/api/products' $staff2).Status
    Assert-Step '12 token from before the lock' 401 (Invoke-Api GET '/api/products' $staff).Status

    Assert-Step '13 change role to Manager' 200 (Invoke-Api PUT "/api/users/$uid/role" $admin @{ role = 'Manager' }).Status
    Assert-Step '14 old token after role change' 401 (Invoke-Api GET '/api/products' $staff2).Status
    $mgr = Get-Token $email $pw1
    Assert-Step '15 new login carries Manager' 'Manager' (Invoke-Api GET '/api/auth/me' $mgr).Body.roles[0]

    Assert-Step '16 admin resets password' 200 (Invoke-Api POST "/api/users/$uid/reset-password" $admin @{ newPassword = $pw2 }).Status
    Assert-Step '17 old password rejected' 401 (Invoke-Api POST '/api/auth/login' $null @{ email = $email; password = $pw1 }).Status
    Assert-Step '18 new password works' 200 (Invoke-Api POST '/api/auth/login' $null @{ email = $email; password = $pw2 }).Status
    Assert-Step '19 token from before the reset' 401 (Invoke-Api GET '/api/products' $mgr).Status

    $me = (Invoke-Api GET '/api/auth/me' $admin).Body
    Assert-Step '20 admin locks self' 409 (Invoke-Api POST "/api/users/$($me.id)/lock" $admin).Status
    Assert-Step '21 admin changes own role' 409 (Invoke-Api PUT "/api/users/$($me.id)/role" $admin @{ role = 'Staff' }).Status
    Assert-Step '22 staff token on /api/users is never 200' $true ((Invoke-Api GET '/api/users' $staff2).Status -ne 200)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
Complete-Smoke
