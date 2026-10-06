# One-time setup of the webhook secrets of the development environment: one secret per seeded channel
# (website, pos, digital-platform). Windows PowerShell 5.1 and PowerShell 7 compatible. ASCII only on purpose (5.1
# misreads UTF-8 files without a BOM).
#
#   powershell -ExecutionPolicy Bypass -File scripts\setup-webhook-secrets.ps1
#
# What it does, for each of the three channels:
#   1. backend/src/Omnichannel.Api/.env (git-ignored; created when it does not exist): adds WEBHOOK_SECRET_<SLUG>
#      (WEBHOOK_SECRET_WEBSITE, WEBHOOK_SECRET_POS, WEBHOOK_SECRET_DIGITAL_PLATFORM) when it is missing - 32 random
#      bytes, base64url. An entry that is there is never changed.
#   2. User-secrets of Omnichannel.Api: Webhooks:Channels:<slug>:Secrets:0, the value of the .env - so the API verifies
#      with the secret that scripts\send-webhook.ps1 and the smoke script sign with.
# It never prints a secret. Run it again at any time: it changes nothing that is already set, and repeats step 2.
# A running API reads the secrets at its next start.
#
# -EnvFile / -UserSecretsId: other targets than the developer's own files (used to try the script out).
param(
    [string]$EnvFile,
    [string]$UserSecretsId
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $EnvFile) { $EnvFile = Join-Path $repoRoot 'backend\src\Omnichannel.Api\.env' }

$Slugs = @('website', 'pos', 'digital-platform')
$MinLength = 32

function Get-EnvName([string]$Slug) { return 'WEBHOOK_SECRET_' + $Slug.ToUpperInvariant().Replace('-', '_') }

function Get-EnvValue($Lines, [string]$Name) {
    foreach ($line in $Lines) {
        if ($line -match "^\s*$([regex]::Escape($Name))\s*=\s*(.*?)\s*$") { return $Matches[1].Trim('"') }
    }
    return $null
}

# 32 random bytes as base64url (43 characters of A-Z a-z 0-9 - _): safe in a .env line, in JSON and in an HTTP header.
function New-WebhookSecret {
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $bytes = New-Object byte[] 32
        $random.GetBytes($bytes)
        return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    } finally {
        $random.Dispose()
    }
}

$lines = @()
if (Test-Path -LiteralPath $EnvFile) {
    $lines = @(Get-Content -LiteralPath $EnvFile)
} else {
    Write-Host "$EnvFile does not exist yet - creating it."
}

$added = New-Object System.Collections.Generic.List[string]
foreach ($slug in $Slugs) {
    $name = Get-EnvName $slug
    $current = Get-EnvValue $lines $name
    if ($current) { continue }
    $entry = "$name=$(New-WebhookSecret)"
    if ($null -eq $current) {
        $lines = @($lines) + $entry
    } else {
        # The name is there without a value.
        $lines = @($lines | ForEach-Object { if ($_ -match "^\s*$([regex]::Escape($name))\s*=") { $entry } else { $_ } })
    }
    [void]$added.Add($name)
}

if ($added.Count -gt 0) {
    # UTF-8 without a BOM, like the other tools that read the file.
    [System.IO.File]::WriteAllLines($EnvFile, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "$EnvFile - added: $($added -join ', ')"
} else {
    Write-Host "$EnvFile - nothing to add: the three entries are there."
}

# What goes to the tool is JSON: a value somebody typed in by hand must be long enough for the API (it would not start
# otherwise) and must not need escaping. The message names the entry, never its value.
$pairs = New-Object System.Collections.Generic.List[string]
foreach ($slug in $Slugs) {
    $name = Get-EnvName $slug
    $value = Get-EnvValue $lines $name
    if (($null -eq $value) -or ($value.Length -lt $MinLength) -or ($value -notmatch '^[A-Za-z0-9_.~+/=-]+$')) {
        throw "$name in $EnvFile must be at least $MinLength characters of letters, digits and - _ . ~ + / = (delete the line to get a new secret)."
    }
    [void]$pairs.Add('"Webhooks:Channels:' + $slug + ':Secrets:0":"' + $value + '"')
}
$json = '{' + ($pairs -join ',') + '}'

# What is piped to a native command must arrive as plain JSON, without a byte-order mark in front: $OutputEncoding
# encodes the text, and Windows PowerShell 5.1 starts the command with the console's input encoding and writes that
# encoding's mark first (a UTF-8 console would break the tool's parser). Both are ASCII for the call below - the JSON is
# ASCII - and the console gets its own back. The values go to the tool on its standard input, never on a command line.
$OutputEncoding = [System.Text.Encoding]::ASCII
$consoleInputEncoding = $null
try {
    $consoleInputEncoding = [Console]::InputEncoding
    [Console]::InputEncoding = [System.Text.Encoding]::ASCII
} catch {
    # No console attached (output and input redirected): nothing writes a mark then.
    $consoleInputEncoding = $null
}

try {
    Push-Location (Join-Path $repoRoot 'backend\src\Omnichannel.Api')
    try {
        # cmd merges stderr into stdout: under Windows PowerShell 5.1 anything a native command writes to stderr would
        # end the script. The output (a count, or an error without values) is not shown.
        if ($UserSecretsId) {
            $null = $json | cmd /c "dotnet user-secrets set --id $UserSecretsId 2>&1"
        } else {
            $null = $json | cmd /c 'dotnet user-secrets set 2>&1'
        }
        if ($LASTEXITCODE -ne 0) { throw "dotnet user-secrets set failed for Omnichannel.Api (exit code $LASTEXITCODE). Is the .NET SDK installed?" }
    } finally {
        Pop-Location
    }
} finally {
    if ($consoleInputEncoding) { [Console]::InputEncoding = $consoleInputEncoding }
    $json = $null
}

$settings = @($Slugs | ForEach-Object { "Webhooks:Channels:${_}:Secrets:0" }) -join ', '
Write-Host "User-secrets of Omnichannel.Api - set: $settings"
Write-Host ''
Write-Host 'An API that is running reads the secrets at its next start.'
Write-Host 'Send a signed webhook: powershell -ExecutionPolicy Bypass -File scripts\send-webhook.ps1 -Channel website -BodyFile <order.json>'
