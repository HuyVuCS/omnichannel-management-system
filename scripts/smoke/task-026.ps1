# Smoke check for task 026: the security headers and the body limit of a production stack through its reverse proxy.
# Read-only: it logs in nowhere and writes nothing. It never starts anything.
#
# Required environment:
#   SMOKE_BASE_URL         the API origin of the proxy, e.g. http://localhost:8080
#   SMOKE_STORAGE_ORIGIN   the storage origin of the proxy (STORAGE_PUBLIC_URL of the stack), e.g. http://localhost:8081
# What needs an account (an image and an export file served with the storage headers) is in task-025.ps1; the static
# host of the frontend is in task-025b.ps1.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-026.ps1
. (Join-Path $PSScriptRoot '_common.ps1')
. (Join-Path $PSScriptRoot '_hardening.ps1')

if (-not $env:SMOKE_BASE_URL) { throw 'SMOKE_BASE_URL is required: the API origin of the proxy of a production stack.' }
if (-not $env:SMOKE_STORAGE_ORIGIN) { throw 'SMOKE_STORAGE_ORIGIN is required: the public origin of the storage site.' }

try {
    Write-Host '== Task 026 - security headers and the body limit through the proxy =='
    [void](Invoke-HardeningChecks $script:BaseUrl $env:SMOKE_STORAGE_ORIGIN.TrimEnd('/') 1)
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
Complete-Smoke
