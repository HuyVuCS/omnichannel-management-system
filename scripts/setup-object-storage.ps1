# One-time setup of the development object storage (product images): generates its access key pair.
# Windows PowerShell 5.1 and PowerShell 7 compatible. ASCII only on purpose (5.1 misreads UTF-8 files without a BOM).
#
#   powershell -ExecutionPolicy Bypass -File scripts\setup-object-storage.ps1
#
# What it does:
#   1. docker/.env: adds OBJECT_STORAGE_BUCKET, OBJECT_STORAGE_ACCESS_KEY_ID and OBJECT_STORAGE_SECRET_ACCESS_KEY when
#      they are missing (or still hold the placeholder of .env.example). Entries that are there are never changed.
#   2. User-secrets of Omnichannel.Api: ObjectStorage:AccessKeyId and ObjectStorage:SecretAccessKey, the same values as
#      in docker/.env - so the API signs with the key the emulator knows.
# It never prints a key. Run it again at any time: it changes nothing that is already set, and repeats step 2.
# Afterwards: docker compose -f docker/docker-compose.yml --env-file docker/.env up -d
#
# -EnvFile / -UserSecretsId: other targets than the developer's own files (used to try the script out).
param(
    [string]$EnvFile,
    [string]$UserSecretsId
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $EnvFile) { $EnvFile = Join-Path $repoRoot 'docker\.env' }
if (-not (Test-Path $EnvFile)) {
    throw "$EnvFile does not exist. Copy docker/.env.example to docker/.env and fill it in first (README section 2)."
}

$BucketName = 'OBJECT_STORAGE_BUCKET'
$KeyIdName = 'OBJECT_STORAGE_ACCESS_KEY_ID'
$SecretName = 'OBJECT_STORAGE_SECRET_ACCESS_KEY'
$DefaultBucket = 'omnichannel-dev'
$Placeholder = 'replace-with-'

# Letters and digits only: the values are substituted into the compose file ('$' would be read as a variable) and
# travel in a signed URL.
function New-RandomText([int]$Length, [string]$Alphabet) {
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $text = New-Object System.Text.StringBuilder
        $buffer = New-Object byte[] 1
        # Rejection sampling: no byte value is preferred.
        $limit = 256 - (256 % $Alphabet.Length)
        while ($text.Length -lt $Length) {
            $random.GetBytes($buffer)
            if ($buffer[0] -lt $limit) { [void]$text.Append($Alphabet[$buffer[0] % $Alphabet.Length]) }
        }
        return $text.ToString()
    } finally {
        $random.Dispose()
    }
}

function Get-EnvValue($Lines, [string]$Name) {
    foreach ($line in $Lines) {
        if ($line -match "^\s*$([regex]::Escape($Name))\s*=\s*(.*?)\s*$") { return $Matches[1].Trim('"') }
    }
    return $null
}

# Sets $Name in $Lines when it is missing, empty or the placeholder; returns the new list of lines. $Changed collects the names.
function Set-EnvValueIfMissing($Lines, [string]$Name, [string]$NewValue, $Changed) {
    $current = Get-EnvValue $Lines $Name
    if ($current -and -not $current.StartsWith($Placeholder)) { return $Lines }
    [void]$Changed.Add($Name)
    $entry = "$Name=$NewValue"
    if ($null -eq $current) { return @($Lines) + $entry }
    return @($Lines | ForEach-Object { if ($_ -match "^\s*$([regex]::Escape($Name))\s*=") { $entry } else { $_ } })
}

$upperAndDigits = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
$lettersAndDigits = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'

$lines = @(Get-Content -LiteralPath $EnvFile)
$changed = New-Object System.Collections.Generic.List[string]
$lines = Set-EnvValueIfMissing $lines $BucketName $DefaultBucket $changed
$lines = Set-EnvValueIfMissing $lines $KeyIdName (New-RandomText 20 $upperAndDigits) $changed
$lines = Set-EnvValueIfMissing $lines $SecretName (New-RandomText 40 $lettersAndDigits) $changed

if ($changed.Count -gt 0) {
    # UTF-8 without a BOM, as docker compose reads it.
    [System.IO.File]::WriteAllLines($EnvFile, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "$EnvFile - added: $($changed -join ', ')"
} else {
    Write-Host "$EnvFile - nothing to add: the three entries are there."
}

$keyId = Get-EnvValue $lines $KeyIdName
$secret = Get-EnvValue $lines $SecretName
foreach ($value in $keyId, $secret) {
    if ($value -notmatch '^[A-Za-z0-9]+$') {
        throw "$KeyIdName and $SecretName in $EnvFile must consist of letters and digits only."
    }
}

# The same pair for the API. The values go to the tool on its standard input (JSON), never on a command line.
$json = '{"ObjectStorage:AccessKeyId":"' + $keyId + '","ObjectStorage:SecretAccessKey":"' + $secret + '"}'
$apiProject = Join-Path $repoRoot 'backend\src\Omnichannel.Api'
# What is piped to a native command is encoded with $OutputEncoding; a byte-order mark in front would not be JSON.
$OutputEncoding = [System.Text.Encoding]::ASCII
Push-Location $apiProject
try {
    # cmd merges stderr into stdout: under Windows PowerShell 5.1 anything a native command writes to stderr would end
    # the script. The output (a count, or an error without values) is not shown.
    if ($UserSecretsId) {
        $null = $json | cmd /c "dotnet user-secrets set --id $UserSecretsId 2>&1"
    } else {
        $null = $json | cmd /c 'dotnet user-secrets set 2>&1'
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet user-secrets set failed (exit code $LASTEXITCODE). Is the .NET SDK installed?" }
} finally {
    Pop-Location
}
Write-Host 'User-secrets of Omnichannel.Api - set: ObjectStorage:AccessKeyId, ObjectStorage:SecretAccessKey'

$bucket = Get-EnvValue $lines $BucketName
if ($bucket -ne $DefaultBucket) {
    Write-Host "Note: $BucketName is '$bucket'. The API must use the same bucket: set ObjectStorage:BucketName (user-secrets or environment)." -ForegroundColor Yellow
}
Write-Host ''
Write-Host 'Next: docker compose -f docker/docker-compose.yml --env-file docker/.env up -d'
Write-Host 'If the storage container already ran with other keys, recreate it: add --force-recreate objectstorage'
