# Gives one secret of a production stack (docker/docker-compose.prod.yml) a new random value. docs/runbook.md "Secrets".
# Windows PowerShell 5.1 compatible, ASCII only. No value is printed, none is on a command line.
#
#   powershell -ExecutionPolicy Bypass -File scripts\deploy\rotate-secret.ps1 -Name <VARIABLE> -EnvFile docker\.env.prod
#
#   JWT_SIGNING_KEY, RABBITMQ_PASSWORD, OBJECT_STORAGE_ACCESS_KEY_ID, OBJECT_STORAGE_SECRET_ACCESS_KEY
#       the new value is written into the environment file - nothing else. What follows is in the runbook.
#   OMNI_APP_PASSWORD, OMNI_MIGRATOR_PASSWORD, POSTGRES_PASSWORD
#       the role gets the new password in the running database of -ProjectName first (as the superuser over the local
#       socket, without a prompt; the PostgreSQL log gets neither the password nor the statement), then the file.
#   WEBHOOK_SECRET_<CHANNEL>, in two steps so that the sender can be switched without a refused webhook:
#       -OverlapFile <new file outside the repository>   writes a compose override that gives the API the new secret
#                                                        as a second one; the environment file is not changed
#       -CompleteFrom <that file>                        moves the new secret into the environment file and removes
#                                                        the override
#
# The first Admin's bootstrap password is not rotated here (runbook: it is blanked after the first login).
# Every change of the file takes effect with "docker compose ... up -d --wait".
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$EnvFile,
    [string]$ProjectName = 'omnichannel-prod',
    [string]$OverlapFile,
    [string]$CompleteFrom
)

. (Join-Path $PSScriptRoot '_stack.ps1')

$lengths = @{ JWT_SIGNING_KEY = 64; RABBITMQ_PASSWORD = 32; OBJECT_STORAGE_ACCESS_KEY_ID = 20; OBJECT_STORAGE_SECRET_ACCESS_KEY = 40
    OMNI_APP_PASSWORD = 32; OMNI_MIGRATOR_PASSWORD = 32; POSTGRES_PASSWORD = 32 }
$roles = @{ OMNI_APP_PASSWORD = 'omnichannel_app'; OMNI_MIGRATOR_PASSWORD = 'omnichannel_migrator'; POSTGRES_PASSWORD = 'postgres' }
$webhook = $Name -cmatch '^WEBHOOK_SECRET_[A-Z0-9]+(_[A-Z0-9]+)*$'
$overrideKey = 'Webhooks__Channels__' + $Name.Substring([math]::Min($Name.Length, 15)).ToLowerInvariant().Replace('_', '-') + '__Secrets__1'

# The generator of new-prod-env.ps1: uniform over letters and digits, from the system's cryptographic generator.
function New-Secret([int]$Length) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $limit = 256 - (256 % $alphabet.Length)
        $chars = New-Object System.Text.StringBuilder
        $buffer = New-Object byte[] 64
        while ($chars.Length -lt $Length) {
            $rng.GetBytes($buffer)
            foreach ($b in $buffer) {
                if (($b -lt $limit) -and ($chars.Length -lt $Length)) { [void]$chars.Append($alphabet[$b % $alphabet.Length]) }
            }
        }
        return $chars.ToString()
    } finally { $rng.Dispose() }
}

# The file must have exactly one line of the variable; every other byte of it stays as it is.
function Get-EnvText {
    $text = [System.IO.File]::ReadAllText($EnvFile)
    if ([regex]::Matches($text, "(?m)^$Name=[^\r\n]*").Count -ne 1) { throw "$EnvFile must have exactly one line '$Name=...'." }
    return $text
}

function Set-EnvValue([string]$Text, [string]$Value) {
    [System.IO.File]::WriteAllText($EnvFile, [regex]::Replace($Text, "(?m)^$Name=[^\r\n]*", "$Name=$Value"), (New-Object System.Text.UTF8Encoding($false)))
}

try {
    if (-not ($webhook -or $lengths.ContainsKey($Name))) { throw "'$Name' is not a secret this script rotates." }
    if (($OverlapFile -or $CompleteFrom) -and (-not $webhook)) { throw '-OverlapFile and -CompleteFrom are for a WEBHOOK_SECRET_<CHANNEL> only.' }
    if ($webhook -and (-not ($OverlapFile -or $CompleteFrom) -or ($OverlapFile -and $CompleteFrom))) {
        throw 'A webhook secret is rotated in two steps: -OverlapFile <file>, later -CompleteFrom <that file> (runbook "Secrets").'
    }
    $EnvFile = (Resolve-Path $EnvFile).Path
    $text = Get-EnvText

    if ($OverlapFile) {
        if (Test-Path $OverlapFile) { throw "$OverlapFile exists already." }
        $full = [System.IO.Path]::GetFullPath($OverlapFile)
        if ($full.StartsWith($script:RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'The override file must be outside the repository.' }
        $yaml = "services:`n  api:`n    environment:`n      ${overrideKey}: `"$(New-Secret 48)`"`n"
        [System.IO.File]::WriteAllText($full, $yaml, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "${Name}: the new secret is in the override file as the API's second secret (the environment file is unchanged)."
        Write-Host 'Next: "up -d --wait" with the override as a second -f; switch the sender; then -CompleteFrom.'
    }
    elseif ($CompleteFrom) {
        $found = [regex]::Matches([System.IO.File]::ReadAllText((Resolve-Path $CompleteFrom).Path), "(?m)^\s*$([regex]::Escape($overrideKey)):\s*`"([A-Za-z0-9]{32,})`"\s*$")
        if ($found.Count -ne 1) { throw "$CompleteFrom is not the override file of $Name." }
        Set-EnvValue $text $found[0].Groups[1].Value
        Remove-Item -LiteralPath $CompleteFrom -Force
        Write-Host "${Name}: the new secret is now the only one in the environment file; the override file is removed."
        Write-Host 'Next: "up -d --wait" without the override.'
    }
    else {
        $value = New-Secret $lengths[$Name]
        if ($roles.ContainsKey($Name)) {
            Set-StackRolePassword (Get-ServiceContainer $ProjectName 'postgres') $roles[$Name] $value
            try { Set-EnvValue $text $value }
            catch { throw "The role $($roles[$Name]) of $ProjectName HAS a new password, but the file could not be written ($($_.Exception.Message)). Run this again." }
            Write-Host "${Name}: the role $($roles[$Name]) of $ProjectName has a new password; it is in the environment file."
        } else {
            Set-EnvValue $text $value
            Write-Host "${Name}: a new value is in the environment file."
        }
        Write-Host 'Next: the row of this secret in the runbook ("up -d --wait").'
    }
    $code = 0
}
catch {
    Write-Host "NOT ROTATED: $($_.Exception.Message)" -ForegroundColor Red
    $code = 1
}
exit $code
