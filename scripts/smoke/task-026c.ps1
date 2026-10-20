# Smoke check for task 026c Part B: an upload goes to a staging key and is copied to a final key when it is confirmed
# (B1), the size of the upload is signed into its link (B3) and - on a throwaway stack only - the image clean-up
# takes what nothing refers to and leaves the image in use (B2).
#
# Default: the local development API (started and stopped by this script when it is not running; no Workers needed)
# with the object storage emulator up (docs/operations-guide.md "Product images").
# Another environment (a production-like stack through its proxy):
#   SMOKE_BASE_URL          the API origin of the proxy, e.g. http://localhost:18080
#   SMOKE_ENV_FILE          the environment file of that stack (its admin), or SMOKE_ADMIN_EMAIL / SMOKE_ADMIN_PASSWORD
# The clean-up drill (never on production or development: the script refuses those projects):
#   SMOKE_IMAGE_SWEEP=1
#   SMOKE_COMPOSE_PROJECT   the compose project of the throwaway stack. Its Workers must run with a short
#                           ImageCleanup__Interval (seconds) and ImageCleanup__MinAge=00:02:00 (the minimum); its
#                           object storage is stopped for a moment to leave an image object nothing refers to
#   SMOKE_IMAGE_SWEEP_WAIT_SECONDS   how long to wait for the clean-up (default 300)
#
# Never prints a link: a presigned link is a credential until it expires. What it leaves behind: three smoke products
# (without an image) and a locked smoke manager; without the drill one staging object that the clean-up removes a day
# later.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-026c.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

$drill = ($env:SMOKE_IMAGE_SWEEP -eq '1')
$project = [string]$env:SMOKE_COMPOSE_PROJECT
$sweepWaitSeconds = if ($env:SMOKE_IMAGE_SWEEP_WAIT_SECONDS) { [int]$env:SMOKE_IMAGE_SWEEP_WAIT_SECONDS } else { 300 }
if ($drill -and ((-not $project) -or ($project -eq 'omnichannel-prod') -or ($project -eq 'omnichannel'))) {
    throw 'The clean-up drill needs SMOKE_COMPOSE_PROJECT = a throwaway stack; it never runs on omnichannel-prod or on development.'
}

# A request to the storage (not to the API): no Authorization header. Returns Status and Bytes; Status 0 = no HTTP answer.
function Invoke-Storage([string]$Method, [string]$Url, [byte[]]$Body, [string]$ContentType) {
    $p = @{ Method = $Method; Uri = $Url; UseBasicParsing = $true; TimeoutSec = 30 }
    if ($null -ne $Body) { $p.Body = $Body; $p.ContentType = $ContentType }
    try {
        $r = Invoke-WebRequest @p
        $bytes = if ($r.RawContentStream) { $r.RawContentStream.ToArray() } else { [byte[]]@() }
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Bytes = $bytes }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { return [pscustomobject]@{ Status = 0; Bytes = [byte[]]@() } }
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Bytes = [byte[]]@() }
    }
}

function Test-SameBytes([byte[]]$Left, [byte[]]$Right) {
    return ([System.Convert]::ToBase64String($Left) -ceq [System.Convert]::ToBase64String($Right))
}

function Test-Success([int]$Status) { return (($Status -ge 200) -and ($Status -lt 300)) }

# The first message of a field in a validation problem, or 'none'.
function Get-FieldError($Answer, [string]$Field) {
    if ($Answer.Body -and $Answer.Body.errors -and $Answer.Body.errors.$Field) { return [string]@($Answer.Body.errors.$Field)[0] } else { return 'none' }
}

# A property of a product's image as text; 'no image' when the product has none.
function Get-ImageField($Product, [string]$Name) {
    if ($Product -and $Product.image -and ($null -ne $Product.image.$Name)) { return [string]$Product.image.$Name } else { return 'no image' }
}

# The key of the object an image link reads: the path of the link without the bucket ("/{bucket}/{key}").
function Get-KeyOfLink([string]$Url) {
    $segments = ([uri]$Url).AbsolutePath.TrimStart('/').Split('/')
    return (($segments | Select-Object -Skip 1) -join '/')
}

# Presign, upload and confirm: the product's image. Returns the confirm answer with UploadKey added.
function Set-SmokeImage([string]$ProductId, [string]$Token, [byte[]]$Bytes) {
    $presign = Invoke-Api POST "/api/products/$ProductId/image/presign" $Token @{ contentType = 'image/png'; sizeBytes = $Bytes.Length }
    if ($presign.Status -ne 200) { return [pscustomobject]@{ Status = $presign.Status; Body = $null; UploadKey = $null } }
    [void](Invoke-Storage PUT ([string]$presign.Body.uploadUrl) $Bytes 'image/png')
    $confirm = Invoke-ApiProblem PUT "/api/products/$ProductId/image" $Token @{ objectKey = $presign.Body.objectKey } 'en'
    return [pscustomobject]@{ Status = $confirm.Status; Body = $confirm.Body; UploadKey = [string]$presign.Body.objectKey }
}

# Runs docker without its output and returns the exit code. Docker writes progress to standard error, which Windows
# PowerShell 5.1 turns into an error under ErrorActionPreference Stop.
function Invoke-Docker([string[]]$Arguments, [switch]$Output) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $lines = @(& docker @Arguments 2>$null) } finally { $ErrorActionPreference = $previous }
    if ($Output) { return ($lines -join "`n") }
    return $LASTEXITCODE
}

# The ids of the containers of a service of the drill's compose project.
function Get-ServiceContainers([string]$Service) {
    $ids = Invoke-Docker @('ps', '-a', '-q', '--filter', "label=com.docker.compose.project=$project", '--filter', "label=com.docker.compose.service=$Service") -Output
    return @($ids -split "`n" | Where-Object { $_ })
}

# Every key the clean-up events since $Since name as deleted.
function Get-SweptKeys([string]$Token, [datetime]$Since) {
    $from = [uri]::EscapeDataString($Since.ToUniversalTime().ToString('o'))
    $events = (Invoke-Api GET "/api/system-events?eventType=product_image.orphans_deleted&from=$from&pageSize=100" $Token).Body
    $keys = @()
    if ($events -and $events.items) { foreach ($event in $events.items) { if ($event.details -and $event.details.keys) { $keys += @($event.details.keys) } } }
    return $keys
}

# A 1 x 1 PNG; a second "image" of exactly the same length and other content (the PNG signature, then zeros).
$png = [System.Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==')
$otherPng = New-Object byte[] $png.Length
[System.Array]::Copy($png, $otherPng, 8)

$admin = $null
$manager = $null
$managerUser = $null
$productIds = @()

try {
    Start-Smoke 'Task 026c - staging upload, copy at confirmation, signed length, image clean-up'
    $credential = Get-AdminCredential
    $admin = Get-Token $credential.Email $credential.Password
    $credential = $null
    $n = Get-Random -Minimum 10000 -Maximum 99999
    $started = Get-Date

    $managerEmail = "smoke-026c-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null
    foreach ($i in 1..3) {
        $created = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-026C-$n-$i"; name = "Smoke product 026c-$n-$i"; price = 15000; lowStockThreshold = 0 }
        if ($created.Status -eq 201) { $productIds += [string]$created.Body.productId }
    }
    Assert-Step '02 create three smoke products' 3 $productIds.Count
    $productId = $productIds[0]

    # ---- B1 / B3: the link is for a staging key, and for exactly the declared type and length ------------------------
    $presign = Invoke-Api POST "/api/products/$productId/image/presign" $manager @{ contentType = 'image/png'; sizeBytes = $png.Length }
    Assert-Step '03 presign a PNG -> 200' 200 $presign.Status
    $uploadKey = [string]$presign.Body.objectKey
    Assert-Step '04 ... the key is a staging key: uploads/products/{productId}/' $true ($uploadKey.StartsWith("uploads/products/$productId/"))
    $uploadUrl = [string]$presign.Body.uploadUrl
    Assert-Step '05 ... the link writes that key' $uploadKey (Get-KeyOfLink $uploadUrl)
    $probe = Invoke-Storage GET (([uri]$uploadUrl).GetLeftPart([System.UriPartial]::Authority) + '/') $null $null
    Assert-Step '06 the object storage answers (is it up?)' $true ($probe.Status -ne 0)
    if ($probe.Status -eq 0) { throw 'The object storage does not answer: start it (docs/operations-guide.md "Product images") and run the script again.' }

    $longer = New-Object byte[] ($png.Length + 1)
    [System.Array]::Copy($png, $longer, $png.Length)
    $shorter = New-Object byte[] ($png.Length - 1)
    [System.Array]::Copy($png, $shorter, $shorter.Length)
    Assert-Step '07 B3: a body one byte longer than declared is refused by the storage (403)' 403 (Invoke-Storage PUT $uploadUrl $longer 'image/png').Status
    Assert-Step '08 B3: ... one byte shorter too (403)' 403 (Invoke-Storage PUT $uploadUrl $shorter 'image/png').Status
    Assert-Step '09 B3: ... and nothing was stored by them (confirm finds no upload)' 'No file has been uploaded for this key.' (Get-FieldError (Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $uploadKey } 'en') 'objectKey')
    Assert-Step '10 PUT the PNG of the declared length -> 2xx' $true (Test-Success (Invoke-Storage PUT $uploadUrl $png 'image/png').Status)

    $confirm = Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $uploadKey } 'en'
    Assert-Step '11 confirm with the staging key -> 200, the image is the PNG' "200 image/png $($png.Length)" "$($confirm.Status) $(Get-ImageField $confirm.Body 'contentType') $(Get-ImageField $confirm.Body 'sizeBytes')"
    $imageUrl = [string]$confirm.Body.image.url
    $imageKey = Get-KeyOfLink $imageUrl
    $updatedAt = Get-ImageField $confirm.Body 'updatedAt'
    Assert-Step '12 ... served from a final key products/{productId}/, not from the staging key' $true ($imageKey.StartsWith("products/$productId/") -and ($imageKey -cne $uploadKey))
    Assert-Step '13 ... with the uploaded bytes' $true (Test-SameBytes $png (Invoke-Storage GET $imageUrl $null $null).Bytes)

    # ---- review 019: the upload link after the confirmation ----------------------------------------------------------
    Assert-Step '14 the same upload link again, other bytes of the signed length -> the storage takes them (2xx)' $true (Test-Success (Invoke-Storage PUT $uploadUrl $otherPng 'image/png').Status)
    Assert-Step '15 ... the image is served unchanged: the link wrote the staging key only' $true (Test-SameBytes $png (Invoke-Storage GET $imageUrl $null $null).Bytes)
    $fresh = (Invoke-Api GET "/api/products/$productId" $manager).Body
    Assert-Step '16 ... also through a new link of the product' $true (Test-SameBytes $png (Invoke-Storage GET ([string]$fresh.image.url) $null $null).Bytes)
    Assert-Step '17 ... the product names the same object as before' $imageKey (Get-KeyOfLink ([string]$fresh.image.url))
    $repeat = Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $uploadKey } 'en'
    Assert-Step '18 confirming that upload again -> 200, nothing changes (not the rewritten bytes)' "200 $updatedAt" "$($repeat.Status) $(Get-ImageField $repeat.Body 'updatedAt')"
    Assert-Step '19 ... the image is still the PNG' $true (Test-SameBytes $png (Invoke-Storage GET ([string]$repeat.Body.image.url) $null $null).Bytes)
    $finalAsUpload = Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $imageKey } 'en'
    Assert-Step '20 the final key of the image is no upload key -> 400' "400 This is not an upload key of this product." "$($finalAsUpload.Status) $(Get-FieldError $finalAsUpload 'objectKey')"

    # Replace: the new image is another final object, the previous one is gone.
    $replaced = Set-SmokeImage $productId $manager $otherPng
    Assert-Step '21 replace the image (a new upload) -> 200' 200 $replaced.Status
    $secondKey = Get-KeyOfLink ([string]$replaced.Body.image.url)
    Assert-Step '22 ... another final object' $true ($secondKey.StartsWith("products/$productId/") -and ($secondKey -cne $imageKey))
    Assert-Step '23 ... the link of the replaced image -> 404' 404 (Invoke-Storage GET $imageUrl $null $null).Status
    # The staging key of the first upload holds what the link wrote in step 14. The image is no copy of that upload
    # any more, so a confirmation is an ordinary one: the bytes are copied, checked and become a third object.
    $rewritten = Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $uploadKey } 'en'
    $thirdKey = if ($rewritten.Body -and $rewritten.Body.image) { Get-KeyOfLink ([string]$rewritten.Body.image.url) } else { 'no image' }
    Assert-Step '24 what the link wrote after the confirmation becomes an image only by a confirmation of its own (checked, a new object)' $true (($rewritten.Status -eq 200) -and $thirdKey.StartsWith("products/$productId/") -and ($thirdKey -cne $imageKey) -and ($thirdKey -cne $secondKey))

    # ---- B2: the clean-up (throwaway stack only) ----------------------------------------------------------------------
    if ($drill) {
        $keeper = $productIds[1]
        $loser = $productIds[2]
        # An image in use; an upload nobody confirms; and an image object nothing refers to - an image removed while
        # the storage was down (the row is cleared, the object could not be deleted).
        $kept = Set-SmokeImage $keeper $manager $png
        Assert-Step '25 drill: an image in use' 200 $kept.Status
        $keptUrl = [string]$kept.Body.image.url
        $keptKey = Get-KeyOfLink $keptUrl
        $abandoned = Invoke-Api POST "/api/products/$keeper/image/presign" $manager @{ contentType = 'image/png'; sizeBytes = $png.Length }
        $abandonedKey = [string]$abandoned.Body.objectKey
        Assert-Step '26 drill: an upload that is never confirmed -> 2xx' $true (Test-Success (Invoke-Storage PUT ([string]$abandoned.Body.uploadUrl) $png 'image/png').Status)
        $doomed = Set-SmokeImage $loser $manager $png
        $orphanUrl = [string]$doomed.Body.image.url
        $orphanKey = Get-KeyOfLink $orphanUrl
        $storage = Get-ServiceContainers 'objectstorage'
        Assert-Step '27 drill: the storage container of the stack is found' 1 $storage.Count
        [void](Invoke-Docker (@('stop') + $storage))
        $removed = Invoke-Api DELETE "/api/products/$loser/image" $manager
        [void](Invoke-Docker (@('start') + $storage))
        Assert-Step '28 drill: remove an image while the storage is down -> 204 (the row is cleared)' 204 $removed.Status
        $deadline = (Get-Date).AddSeconds(60)
        do { Start-Sleep -Seconds 2; $back = Invoke-Storage GET $orphanUrl $null $null } while (($back.Status -ne 200) -and ((Get-Date) -lt $deadline))
        Assert-Step '29 drill: ... its object is still in the storage, referred to by nothing' "200 | no image" "$($back.Status) | $(Get-ImageField (Invoke-Api GET "/api/products/$loser" $manager).Body 'contentType')"
        $orphanedAt = Get-Date

        # The Workers delete both once they are two minutes old (ImageCleanup__MinAge), and leave the image in use.
        Write-Host "   (waiting up to $sweepWaitSeconds s for the clean-up)"
        $deadline = $orphanedAt.AddSeconds($sweepWaitSeconds)
        $swept = @()
        do {
            Start-Sleep -Seconds 5
            $swept = @(Get-SweptKeys $admin $started)
        } while ((-not (($swept -ccontains $abandonedKey) -and ($swept -ccontains $orphanKey))) -and ((Get-Date) -lt $deadline))
        $waited = [int]((Get-Date) - $orphanedAt).TotalSeconds
        Write-Host "   (clean-up events seen after $waited s)"
        Assert-Step '30 drill: the clean-up reports the unconfirmed upload as deleted' $true ($swept -ccontains $abandonedKey)
        Assert-Step '31 drill: ... and the image object nothing referred to' $true ($swept -ccontains $orphanKey)
        Assert-Step '32 drill: ... not before they were two minutes old' $true ($waited -ge 100)
        Assert-Step '33 drill: the upload is gone (confirm finds none)' 'No file has been uploaded for this key.' (Get-FieldError (Invoke-ApiProblem PUT "/api/products/$keeper/image" $manager @{ objectKey = $abandonedKey } 'en') 'objectKey')
        Assert-Step '34 drill: the unreferenced object is gone (its old link -> 404)' 404 (Invoke-Storage GET $orphanUrl $null $null).Status
        # The image in use is as old as the two, and older than the minimum age by now.
        Assert-Step '35 drill: the image in use was not reported' $false ($swept -ccontains $keptKey)
        $stillThere = (Invoke-Api GET "/api/products/$keeper" $manager).Body
        Assert-Step '36 drill: ... the product still has it, under the same key' $keptKey (Get-KeyOfLink ([string]$stillThere.image.url))
        Assert-Step '37 drill: ... and it is served with its bytes' $true (Test-SameBytes $png (Invoke-Storage GET ([string]$stillThere.image.url) $null $null).Bytes)
        Assert-Step '38 drill: the image of the first product (replaced during this run) is served too' $true (Test-SameBytes $otherPng (Invoke-Storage GET ([string](Invoke-Api GET "/api/products/$productId" $manager).Body.image.url) $null $null).Bytes)
    }
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # No smoke product keeps an image, and the smoke account is locked, also after a failed step.
    $left = 0
    foreach ($id in $productIds) {
        if ($manager) {
            [void](Invoke-Api DELETE "/api/products/$id/image" $manager)
            if ((Get-ImageField (Invoke-Api GET "/api/products/$id" $manager).Body 'contentType') -cne 'no image') { $left++ }
        }
    }
    if ($manager -and $productIds.Count -gt 0) { Assert-Step '90 the smoke products end without an image' 0 $left }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '91 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
}
Invoke-FrontendChecks 92 @('axe-core')
Complete-Smoke
