# Writes the environment file of the production stack (docker/docker-compose.prod.yml) with fresh random values for every
# password and key: PostgreSQL roles, RabbitMQ, the JWT signing key, the object-storage key pair, the webhook secrets.
# Windows PowerShell 5.1 compatible, ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File scripts\deploy\new-prod-env.ps1 -AdminEmail you@example.com `
#       -FrontendOrigin https://your-app.vercel.app -StoragePublicUrl https://files.example.com
#
# - The file (default docker\.env.prod) is git-ignored. An existing file is kept: the script stops unless -Force is given,
#   because new passwords do not fit the volumes of a stack that already ran (PostgreSQL and RabbitMQ keep theirs).
# - No value is printed, with one exception: the password of the first Admin is shown once, at the end - it is needed
#   for the first login and is stored nowhere else. With -PromptAdminPassword it is asked for instead and never shown.
# - Every value is letters and digits only: they are substituted into the compose file and into connection strings.
param(
    [Parameter(Mandatory = $true)][string]$AdminEmail,
    # Origin of the frontend as a browser sends it. The default is vite preview on this machine (a local run).
    [string]$FrontendOrigin = 'http://localhost:4173',
    # Public origin of the proxy's storage site. The default is the local run without TLS.
    [string]$StoragePublicUrl = 'http://localhost:8081',
    [string]$Path,
    [switch]$PromptAdminPassword,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Path) { $Path = Join-Path $repoRoot 'docker\.env.prod' }
$template = Join-Path $repoRoot 'docker\prod.env.example'

function Test-Origin([string]$Value) {
    $uri = $null
    if (-not [uri]::TryCreate($Value, [System.UriKind]::Absolute, [ref]$uri)) { return $false }
    if (($uri.Scheme -ne 'http') -and ($uri.Scheme -ne 'https')) { return $false }
    # An origin: nothing after the host and port.
    return ($uri.GetLeftPart([System.UriPartial]::Authority) -ceq $Value)
}

# Uniform over the alphabet (rejection sampling), from the system's cryptographic generator.
function New-Secret([int]$Length, [string]$Alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789') {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $limit = 256 - (256 % $Alphabet.Length)
        $chars = New-Object System.Text.StringBuilder
        $buffer = New-Object byte[] 64
        while ($chars.Length -lt $Length) {
            $rng.GetBytes($buffer)
            foreach ($b in $buffer) {
                if (($b -lt $limit) -and ($chars.Length -lt $Length)) { [void]$chars.Append($Alphabet[$b % $Alphabet.Length]) }
            }
        }
        return $chars.ToString()
    } finally { $rng.Dispose() }
}

# The API's password policy: at least 12 characters with an upper-case letter, a lower-case letter and a digit.
function Test-AdminPassword([string]$Value) {
    return (($Value.Length -ge 12) -and ($Value -cmatch '[A-Z]') -and ($Value -cmatch '[a-z]') -and ($Value -cmatch '[0-9]') -and ($Value -cmatch '^[A-Za-z0-9]+$'))
}

if (-not (Test-Origin $FrontendOrigin)) { throw '-FrontendOrigin must be an origin: http(s)://host[:port] without a path or a trailing slash.' }
if (-not (Test-Origin $StoragePublicUrl)) { throw '-StoragePublicUrl must be an origin: http(s)://host[:port] without a path or a trailing slash.' }
if ($AdminEmail -notmatch '^[^@\s=]+@[^@\s=]+$') { throw '-AdminEmail must be an e-mail address.' }
if (-not (Test-Path $template)) { throw "Template not found: $template" }
if ((Test-Path $Path) -and (-not $Force)) {
    throw "$Path exists and is kept. New passwords do not fit the volumes of a stack that already ran; use -Force only for a stack that is created from nothing."
}

$adminPassword = $null
if ($PromptAdminPassword) {
    $secure = Read-Host 'Password of the first Admin (at least 12 letters and digits: upper case, lower case and a digit)' -AsSecureString
    $adminPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    if (-not (Test-AdminPassword $adminPassword)) { throw 'The password must have at least 12 characters, letters and digits only, with an upper-case letter, a lower-case letter and a digit.' }
} else {
    do { $adminPassword = New-Secret 20 } until (Test-AdminPassword $adminPassword)
}

$values = @{
    FRONTEND_ORIGIN                  = $FrontendOrigin
    STORAGE_PUBLIC_URL               = $StoragePublicUrl
    POSTGRES_PASSWORD                = New-Secret 32
    OMNI_MIGRATOR_PASSWORD           = New-Secret 32
    OMNI_APP_PASSWORD                = New-Secret 32
    RABBITMQ_PASSWORD                = New-Secret 32
    # 64 letters and digits: about 380 bits.
    JWT_SIGNING_KEY                  = New-Secret 64
    BOOTSTRAP_ADMIN_EMAIL            = $AdminEmail
    BOOTSTRAP_ADMIN_PASSWORD         = $adminPassword
    WEBHOOK_SECRET_WEBSITE           = New-Secret 48
    WEBHOOK_SECRET_POS               = New-Secret 48
    WEBHOOK_SECRET_DIGITAL_PLATFORM  = New-Secret 48
    OBJECT_STORAGE_ACCESS_KEY_ID     = New-Secret 20
    OBJECT_STORAGE_SECRET_ACCESS_KEY = New-Secret 40
}

# The template with its comments; a variable the template gives a value (ports, addresses) keeps it.
$lines = New-Object System.Collections.Generic.List[string]
$written = @{}
foreach ($line in Get-Content $template) {
    if ($line -match '^([A-Z][A-Z0-9_]*)=(.*)$') {
        $name = $Matches[1]
        if ($values.ContainsKey($name)) {
            $lines.Add("$name=$($values[$name])")
            $written[$name] = $true
            continue
        }
    }
    $lines.Add($line)
}
$missing = @($values.Keys | Where-Object { -not $written.ContainsKey($_) })
if ($missing.Count -gt 0) { throw "The template has no line for: $($missing -join ', ')" }

# UTF-8 without a BOM, LF line ends: docker compose reads the file as it is.
[System.IO.File]::WriteAllText($Path, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Written: $Path (git-ignored; $($values.Count) values set, none shown here)."
Write-Host "Frontend origin: $FrontendOrigin"
Write-Host "Storage origin:  $StoragePublicUrl"
if (-not $PromptAdminPassword) {
    Write-Host ''
    Write-Host 'The password of the first Admin - shown only now. Store it in a password manager; it is also in the file above.' -ForegroundColor Yellow
    Write-Host "  $AdminEmail"
    Write-Host "  $adminPassword"
}
$adminPassword = $null
$values = $null
