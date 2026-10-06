# Signs a webhook the way a channel would and posts it to POST /api/webhooks/{slug} (README "Webhooks").
# Windows PowerShell 5.1 and PowerShell 7 compatible. ASCII only on purpose.
#
#   powershell -ExecutionPolicy Bypass -File scripts\send-webhook.ps1 -Channel website -BodyFile order.json
#
# The secret of the channel is read from backend/src/Omnichannel.Api/.env (WEBHOOK_SECRET_<SLUG>, written by
# scripts\setup-webhook-secrets.ps1). The bytes of the file are signed and sent exactly as they are in the file:
# HMAC-SHA256 with the secret over "{timestamp}.{bytes}", sent as X-Webhook-Timestamp and X-Webhook-Signature: v1=<hex>.
# Prints the status and the answer - never the secret or the signature.
#
#   -Timestamp <unix seconds>   another timestamp than now (an old one is refused: 401 webhook_timestamp_invalid)
#   -BaseUrl <url>              the API (default http://localhost:5080)
#   -SignatureOf <file>         sign the bytes of this file instead of the body's - shows that a body that was changed
#                               after signing is refused (401 webhook_signature_invalid)
#   -CorrelationId <id>         sent as X-Correlation-Id (the task of the order carries it)
#   -EnvFile <file>             another .env than the API's
#   -PassThru                   return { Status; Text; Body } instead of printing (used by the smoke script)
param(
    [Parameter(Mandatory = $true)][string]$Channel,
    [Parameter(Mandatory = $true)][string]$BodyFile,
    [string]$Timestamp,
    [string]$BaseUrl = 'http://localhost:5080',
    [string]$SignatureOf,
    [string]$CorrelationId,
    [string]$EnvFile,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $EnvFile) { $EnvFile = Join-Path $repoRoot 'backend\src\Omnichannel.Api\.env' }

$slug = $Channel.Trim().ToLowerInvariant()
if ($slug -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { throw "'$Channel' is not a channel slug (for example: website, pos, digital-platform)." }
$name = 'WEBHOOK_SECRET_' + $slug.ToUpperInvariant().Replace('-', '_')

$secret = $null
if (Test-Path -LiteralPath $EnvFile) {
    foreach ($line in Get-Content -LiteralPath $EnvFile) {
        if ($line -match "^\s*$([regex]::Escape($name))\s*=\s*(.*?)\s*$") { $secret = $Matches[1].Trim('"') }
    }
}
if (-not $secret) {
    throw "No $name in $EnvFile. Run scripts\setup-webhook-secrets.ps1 first (it creates the secrets of website, pos and digital-platform), then restart the API."
}

if (-not (Test-Path -LiteralPath $BodyFile)) { throw "Body file '$BodyFile' does not exist." }
$body = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $BodyFile).Path)
$signed = $body
if ($SignatureOf) {
    if (-not (Test-Path -LiteralPath $SignatureOf)) { throw "File '$SignatureOf' does not exist." }
    $signed = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $SignatureOf).Path)
}

if (-not $Timestamp) { $Timestamp = [string][DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }
if ($Timestamp -notmatch '^[0-9]+$') { throw 'Timestamp must be a number of seconds since 1970-01-01 UTC (digits only).' }

# "{timestamp}.{body}": the timestamp as it is sent, a dot, the bytes as they are sent.
$prefix = [System.Text.Encoding]::ASCII.GetBytes("$Timestamp.")
$message = New-Object byte[] ($prefix.Length + $signed.Length)
[Array]::Copy($prefix, 0, $message, 0, $prefix.Length)
[Array]::Copy($signed, 0, $message, $prefix.Length, $signed.Length)
$hmac = New-Object System.Security.Cryptography.HMACSHA256 -ArgumentList (, [System.Text.Encoding]::UTF8.GetBytes($secret))
try {
    $signature = ([System.BitConverter]::ToString($hmac.ComputeHash($message))).Replace('-', '').ToLowerInvariant()
} finally {
    $hmac.Dispose()
    $secret = $null
}

$headers = @{ 'X-Webhook-Timestamp' = $Timestamp; 'X-Webhook-Signature' = "v1=$signature" }
if ($CorrelationId) { $headers['X-Correlation-Id'] = $CorrelationId }
$uri = $BaseUrl.TrimEnd('/') + "/api/webhooks/$slug"

$status = 0
$text = ''
try {
    $response = Invoke-WebRequest -Method POST -Uri $uri -UseBasicParsing -ContentType 'application/json' -Headers $headers -Body $body
    $status = [int]$response.StatusCode
    $text = [string]$response.Content
} catch {
    $failed = $_.Exception.Response
    if ($null -eq $failed) { throw }
    $status = [int]$failed.StatusCode
    if ($failed -is [System.Net.HttpWebResponse]) {
        # Windows PowerShell 5.1: read the body of an error answer as UTF-8 ourselves.
        $stream = $failed.GetResponseStream()
        if ($stream.CanSeek) { $stream.Position = 0 }
        $text = (New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)).ReadToEnd()
    } else {
        $text = [string]$_.ErrorDetails.Message
    }
}

if ($PassThru) {
    $json = $null
    if ($text) { try { $json = $text | ConvertFrom-Json } catch { $json = $null } }
    return [pscustomobject]@{ Status = $status; Text = $text; Body = $json }
}

Write-Host "POST $uri"
Write-Host "HTTP $status"
if ($text) { Write-Host $text }
