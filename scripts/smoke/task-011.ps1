# Smoke check for task 011 (Part A: localised validation messages, isLowStock on products; frontend build and tests).
# Needs PostgreSQL running and Node.js (see frontend/.nvmrc); starts the API if needed.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-011.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

# First validation message for $Key of a request that must answer 400, in the language asked for.
# Returns a text starting with "unexpected" when the answer is not a 400 with that key.
function Get-ValidationMessage([string]$Method, [string]$Path, [string]$Token, [string]$Body, [string]$Language, [string]$Key) {
    $p = @{ Method = $Method; Uri = "$script:BaseUrl$Path"; UseBasicParsing = $true; ContentType = 'application/json'
            Headers = @{ Authorization = "Bearer $Token"; 'Accept-Language' = $Language } }
    if ($Body) { $p.Body = $Body }
    try {
        $r = Invoke-WebRequest @p
        return "unexpected HTTP $([int]$r.StatusCode)"
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        if ([int]$resp.StatusCode -ne 400) { return "unexpected HTTP $([int]$resp.StatusCode)" }
        if ($resp -is [System.Net.HttpWebResponse]) {
            # Windows PowerShell 5.1: read the body as UTF-8 ourselves.
            $stream = $resp.GetResponseStream()
            if ($stream.CanSeek) { $stream.Position = 0 }
            $text = (New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)).ReadToEnd()
        } else {
            $text = $_.ErrorDetails.Message
        }
        $errors = ($text | ConvertFrom-Json).errors
        foreach ($property in $errors.PSObject.Properties) {
            if ($property.Name -ieq $Key) { return [string]$property.Value[0] }
        }
        return "unexpected: no error for '$Key'"
    }
}

# True when the text contains a character outside ASCII (Vietnamese messages do, the English ones do not).
function Test-NonAscii([string]$Text) { return ($Text -cmatch '[^\x00-\x7F]') }

try {
    Start-Smoke 'Task 011 - localised validation messages, isLowStock, frontend'
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # Custom validation messages follow Accept-Language; keys and errorCode do not change.
    $badSku = '{"sku":"bad sku!","name":"Smoke","price":1,"lowStockThreshold":0}'
    $skuVi = Get-ValidationMessage POST '/api/products' $admin $badSku 'vi' 'sku'
    $skuEn = Get-ValidationMessage POST '/api/products' $admin $badSku 'en' 'sku'
    Assert-Step '01 SKU format message, vi: Vietnamese text' $true (Test-NonAscii $skuVi)
    Assert-Step '02 SKU format message, en: English text' $true ($skuEn -like "*must be at most 64 characters*")
    Assert-Step '03 SKU messages differ by language' $true ($skuVi -ne $skuEn)

    $range = '/api/orders?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z'
    $rangeVi = Get-ValidationMessage GET $range $admin $null 'vi' 'to'
    $rangeEn = Get-ValidationMessage GET $range $admin $null 'en' 'to'
    Assert-Step '04 date range message, vi: Vietnamese text' $true (Test-NonAscii $rangeVi)
    Assert-Step '05 date range message, en' "'To date' must not be earlier than 'From date'." $rangeEn

    $weak = '{"email":"smoke-011-weak' + $n + '@example.com","password":"weakpass","role":"Staff"}'
    Assert-Step '06 password policy message, vi: Vietnamese text' $true (Test-NonAscii (Get-ValidationMessage POST '/api/users' $admin $weak 'vi' 'password'))

    # isLowStock on products: the shared server rule (AvailableStock <= LowStockThreshold).
    $sku = "SMOKE-011-$n"
    $created = Invoke-Api POST '/api/products' $admin @{ sku = $sku; name = "Smoke product $n"; price = 15000; lowStockThreshold = 5 }
    Assert-Step '07 create product (threshold 5, no stock)' 201 $created.Status
    Assert-Step '08 new product is low on stock' $true $created.Body.isLowStock
    $productId = $created.Body.productId

    $import = Invoke-Api POST '/api/inventory/import' $admin @{ documentNumber = "SMOKE-011-GRN-$n"; items = @(@{ productId = $productId; stock = 5 }) }
    Assert-Step '09 import 5 units' 200 $import.Status
    Assert-Step '10 at the threshold: still low (detail)' $true (Invoke-Api GET "/api/products/$productId" $admin).Body.isLowStock

    $import = Invoke-Api POST '/api/inventory/import' $admin @{ documentNumber = "SMOKE-011-GRN2-$n"; items = @(@{ productId = $productId; stock = 1 }) }
    Assert-Step '11 import 1 more unit' 200 $import.Status
    $listed = (Invoke-Api GET "/api/products?search=$sku" $admin).Body.items[0]
    Assert-Step '12 above the threshold: not low (list)' $false $listed.isLowStock
    Assert-Step '13 available stock in the list' 6 $listed.availableStock
    $inventory = (Invoke-Api GET "/api/inventory?search=$sku" $admin).Body.items[0]
    Assert-Step '14 inventory list agrees with the product list' $listed.isLowStock $inventory.isLowStock
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
# Frontend: production build and tests (steps 15-17), after the API this script started is stopped.
Invoke-FrontendChecks 15
Complete-Smoke
