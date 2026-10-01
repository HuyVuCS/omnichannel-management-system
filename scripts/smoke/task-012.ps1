# Smoke check for task 012 (camelCase validation keys, localised field names, createdByEmail on movements, stock import
# idempotency, lowStockOnly filter; frontend build and tests).
# Needs PostgreSQL running and Node.js (see frontend/.nvmrc); starts the API if needed.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-012.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

# The keys of "errors" of a 400 answer, joined with a comma; a text starting with "unexpected" otherwise.
function Get-ErrorKeys($Response) {
    if ($Response.Status -ne 400) { return "unexpected HTTP $($Response.Status)" }
    return (@($Response.Body.errors.PSObject.Properties | ForEach-Object { $_.Name }) -join ',')
}

# First message for $Key of a 400 answer; a text starting with "unexpected" otherwise.
function Get-FirstMessage($Response, [string]$Key) {
    if ($Response.Status -ne 400) { return "unexpected HTTP $($Response.Status)" }
    foreach ($property in $Response.Body.errors.PSObject.Properties) {
        if ($property.Name -ceq $Key) { return [string]$property.Value[0] }
    }
    return "unexpected: no error for '$Key'"
}

# True when the text contains a character outside ASCII (Vietnamese messages do, the English ones do not).
function Test-NonAscii([string]$Text) { return ($Text -cmatch '[^\x00-\x7F]') }

function Invoke-Npm([string]$Arguments) {
    $frontend = Join-Path $script:RepoRoot 'frontend'
    Push-Location $frontend
    try {
        cmd /c "npm $Arguments > NUL 2>&1"
        return $LASTEXITCODE
    } finally {
        Pop-Location
    }
}

try {
    Start-Smoke 'Task 012 - validation keys, field names, movement actor, stock import, frontend'
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    # A1: every key of a 400 is a camelCase JSON path (compared case-sensitively).
    $range = '/api/orders?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z'
    Assert-Step '01 inverted range: key is exactly "to"' $true ((Get-ErrorKeys (Invoke-ApiProblem GET $range $admin $null 'en')) -ceq 'to')
    Assert-Step '02 unknown sort field: key is exactly "sortBy"' $true ((Get-ErrorKeys (Invoke-ApiProblem GET '/api/customers?sortBy=secret' $admin $null 'en')) -ceq 'sortBy')
    Assert-Step '03 model binding error: key is exactly "pageSize"' $true ((Get-ErrorKeys (Invoke-ApiProblem GET '/api/products?pageSize=abc' $admin $null 'en')) -ceq 'pageSize')

    # A2: messages name fields like the UI does, in the request language.
    $badProduct = @{ sku = "SMOKE-012-BAD-$n"; name = 'Smoke'; price = 1; lowStockThreshold = -1 }
    $thresholdEn = Get-FirstMessage (Invoke-ApiProblem POST '/api/products' $admin $badProduct 'en') 'lowStockThreshold'
    $thresholdVi = Get-FirstMessage (Invoke-ApiProblem POST '/api/products' $admin $badProduct 'vi') 'lowStockThreshold'
    Assert-Step '04 field name in a message, en' $true ($thresholdEn -clike "'Low-stock threshold' *")
    Assert-Step '05 field name in a message, vi: Vietnamese, not the technical name' $true ((Test-NonAscii $thresholdVi) -and ($thresholdVi -notlike '*Stock Threshold*') -and ($thresholdVi -notlike 'unexpected*'))
    $rangeEn = Get-FirstMessage (Invoke-ApiProblem GET $range $admin $null 'en') 'to'
    $rangeVi = Get-FirstMessage (Invoke-ApiProblem GET $range $admin $null 'vi') 'to'
    Assert-Step '06 date range message, en' "'To date' must not be earlier than 'From date'." $rangeEn
    Assert-Step '07 date range message, vi: Vietnamese field names' $true ((Test-NonAscii $rangeVi) -and ($rangeVi -notlike "*'To'*") -and ($rangeVi -notlike "*'From'*"))

    # A smoke Manager (random password) imports stock: no Admin is needed for it.
    $email = "smoke-012-$n@example.com"
    $password = New-TestPassword
    $user = Invoke-Api POST '/api/users' $admin @{ email = $email; password = $password; role = 'Manager' }
    Assert-Step '08 create smoke manager' 201 $user.Status
    $manager = Get-Token $email $password
    $password = $null

    $sku = "SMOKE-012-$n"
    $created = Invoke-Api POST '/api/products' $manager @{ sku = $sku; name = "Smoke product $n"; price = 15000; lowStockThreshold = 5 }
    Assert-Step '09 create product (threshold 5)' 201 $created.Status
    $productId = $created.Body.productId

    $document = "SMOKE-012-GRN-$n"
    $import = @{ documentNumber = $document; items = @(@{ productId = $productId; stock = 3 }) }
    $first = Invoke-ApiProblem POST '/api/inventory/import' $manager $import 'en'
    Assert-Step '10 import 3 units' 200 $first.Status
    Assert-Step '11 reference of the import' $document $first.Body.referenceId

    # The movement names who imported: createdByEmail next to the unchanged createdBy (the user id).
    $movement = (Invoke-Api GET "/api/inventory/movements?referenceType=StockImport&referenceId=$document" $manager).Body.items[0]
    Assert-Step '12 movement: createdByEmail' $email $movement.createdByEmail
    Assert-Step '13 movement: createdBy is the user id' $user.Body.userId $movement.createdBy
    Assert-Step '14 movement: type and quantity' 'Import 3' "$($movement.type) $($movement.stock)"

    # The same document again: rejected, nothing changes.
    $again = Invoke-ApiProblem POST '/api/inventory/import' $manager $import 'en'
    Assert-Step '15 re-import of the same document' 409 $again.Status
    Assert-Step '16 re-import: errorCode' 'stock_import_already_applied' $again.Body.errorCode
    Assert-Step '17 stock unchanged after the rejected re-import' 3 (Invoke-Api GET "/api/products/$productId" $manager).Body.totalStock

    # lowStockOnly: 3 available with threshold 5 is low; after 10 more it is not.
    $low = (Invoke-Api GET "/api/inventory?lowStockOnly=true&search=$sku" $manager).Body
    Assert-Step '18 lowStockOnly contains the product' 1 $low.totalCount
    Assert-Step '19 ... flagged by the server' $true $low.items[0].isLowStock
    $more = Invoke-Api POST '/api/inventory/import' $manager @{ documentNumber = "$document-2"; items = @(@{ productId = $productId; stock = 10 }) }
    Assert-Step '20 import 10 more units' 200 $more.Status
    Assert-Step '21 lowStockOnly no longer contains it' 0 (Invoke-Api GET "/api/inventory?lowStockOnly=true&search=$sku" $manager).Body.totalCount

    # The smoke account is locked again.
    Assert-Step '22 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($user.Body.userId)/lock" $admin).Status

    # Frontend: production build and tests.
    if (-not (Test-Path (Join-Path $script:RepoRoot 'frontend\node_modules'))) {
        Write-Host 'Installing frontend packages (npm ci)...'
        Assert-Step '23 npm ci' 0 (Invoke-Npm 'ci')
    }
    Write-Host 'Building the frontend...'
    Assert-Step '24 npm run build' 0 (Invoke-Npm 'run build')
    Write-Host 'Running the frontend tests...'
    Assert-Step '25 npm test' 0 (Invoke-Npm 'test')
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
Complete-Smoke
