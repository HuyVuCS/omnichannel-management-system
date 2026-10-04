# Smoke check for task 019 (product images in S3-compatible object storage: presign, upload to the storage, confirm, remove).
# Needs PostgreSQL running, migration ProductImage applied (README section 4), and the object storage emulator up with
# the keys of the API (README "Product images": scripts\setup-object-storage.ps1, then docker compose up -d).
# Uses a running API or - for the local default only - builds and starts it (Development), and stops what it started.
# The storage is reached at the address of the upload link the API hands out; when it does not answer, the script
# fails with a step that says so. Never prints a link: a presigned link is a credential until it expires.
# The smoke products end without an image and the smoke accounts are locked, also after a failed step.
# The frontend build and tests run last, after the host this script started was stopped.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\task-019.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

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

# The first message of a field in a validation problem, or 'none'.
function Get-FieldError($Answer, [string]$Field) {
    if ($Answer.Body -and $Answer.Body.errors -and $Answer.Body.errors.$Field) { return [string]@($Answer.Body.errors.$Field)[0] } else { return 'none' }
}

# A property of a product's image as text; 'no image' when the product has none.
function Get-ImageField($Product, [string]$Name) {
    if ($Product -and $Product.image -and ($null -ne $Product.image.$Name)) { return [string]$Product.image.$Name } else { return 'no image' }
}

# Presign + upload for a product. Returns the presign answer's body with UploadStatus added; nothing is confirmed.
function Send-Upload([string]$ProductId, [string]$Token, [string]$ContentType, [byte[]]$Bytes) {
    $presign = Invoke-Api POST "/api/products/$ProductId/image/presign" $Token @{ contentType = $ContentType; sizeBytes = $Bytes.Length }
    if ($presign.Status -ne 200) { return [pscustomobject]@{ PresignStatus = $presign.Status; UploadStatus = 0; objectKey = $null } }
    $put = Invoke-Storage $presign.Body.method $presign.Body.uploadUrl $Bytes $presign.Body.headers.'Content-Type'
    return [pscustomobject]@{ PresignStatus = 200; UploadStatus = $put.Status; objectKey = [string]$presign.Body.objectKey }
}

function Test-Success([int]$Status) { return (($Status -ge 200) -and ($Status -lt 300)) }

# A 1 x 1 PNG, a JPEG by its first bytes (the API checks the signature, not the picture), and a text file.
$png = [System.Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==')
$jpeg = [byte[]](@(0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01) + @(1..200 | ForEach-Object { $_ % 251 }))
$text = [System.Text.Encoding]::ASCII.GetBytes('This is a text file, whatever it is declared as.')
$maxImageBytes = 5 * 1024 * 1024

$admin = $null
$manager = $null
$managerUser = $null
$staffUser = $null
$productId = $null
$otherId = $null

try {
    Start-Smoke 'Task 019 - product images: presign, upload to the storage, confirm, replace, remove'
    $cred  = Get-AdminCredential
    $admin = Get-Token $cred.Email $cred.Password
    $cred  = $null
    $n = Get-Random -Maximum 99999

    $managerEmail = "smoke-019-mgr-$n@example.com"
    $password = New-TestPassword
    $managerUser = Invoke-Api POST '/api/users' $admin @{ email = $managerEmail; password = $password; role = 'Manager' }
    Assert-Step '01 create smoke manager' 201 $managerUser.Status
    $manager = Get-Token $managerEmail $password
    $password = $null

    $staffEmail = "smoke-019-staff-$n@example.com"
    $password = New-TestPassword
    $staffUser = Invoke-Api POST '/api/users' $admin @{ email = $staffEmail; password = $password; role = 'Staff' }
    Assert-Step '02 create smoke staff' 201 $staffUser.Status
    $staff = Get-Token $staffEmail $password
    $password = $null

    $product = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-019-$n"; name = "Smoke product 019-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '03 create smoke product' 201 $product.Status
    $productId = $product.Body.productId
    Assert-Step '04 ... it has no image' 'no image' (Get-ImageField $product.Body 'contentType')
    $other = Invoke-Api POST '/api/products' $manager @{ sku = "SMOKE-019B-$n"; name = "Smoke product 019B-$n"; price = 15000; lowStockThreshold = 0 }
    Assert-Step '05 create a second smoke product' 201 $other.Status
    $otherId = $other.Body.productId

    # Roles and what cannot become an image.
    Assert-Step '06 staff: presign is forbidden' 403 (Invoke-Api POST "/api/products/$productId/image/presign" $staff @{ contentType = 'image/png'; sizeBytes = $png.Length }).Status
    Assert-Step '07 ... confirm too' 403 (Invoke-Api PUT "/api/products/$productId/image" $staff @{ objectKey = "products/$productId/$([guid]::NewGuid())" }).Status
    Assert-Step '08 ... and remove' 403 (Invoke-Api DELETE "/api/products/$productId/image" $staff).Status
    $svg = Invoke-ApiProblem POST "/api/products/$productId/image/presign" $manager @{ contentType = 'image/svg+xml'; sizeBytes = 100 } 'en'
    Assert-Step '09 presign image/svg+xml -> 400' 400 $svg.Status
    Assert-Step '10 ... on contentType' 'The image must be a JPEG, PNG or WebP file.' (Get-FieldError $svg 'contentType')
    $big = Invoke-ApiProblem POST "/api/products/$productId/image/presign" $manager @{ contentType = 'image/png'; sizeBytes = ($maxImageBytes + 1) } 'en'
    Assert-Step '11 presign above the size limit -> 400' 400 $big.Status
    Assert-Step '12 ... on sizeBytes' 'The image must not be larger than 5 MB.' (Get-FieldError $big 'sizeBytes')

    # Presign: a key of this product and a link to the storage.
    $presign = Invoke-Api POST "/api/products/$productId/image/presign" $manager @{ contentType = 'image/png'; sizeBytes = $png.Length }
    Assert-Step '13 presign a PNG -> 200' 200 $presign.Status
    Assert-Step '14 ... method PUT, key under products/{productId}/' $true (($presign.Body.method -ceq 'PUT') -and ([string]$presign.Body.objectKey).StartsWith("products/$productId/"))
    Assert-Step '15 ... the only header to send is Content-Type image/png' 'image/png' ([string]$presign.Body.headers.'Content-Type')
    $uploadUri = [uri]$presign.Body.uploadUrl
    $storageOrigin = $uploadUri.GetLeftPart([System.UriPartial]::Authority)
    Assert-Step '16 nothing is stored on the product by a presign' 'no image' (Get-ImageField (Invoke-Api GET "/api/products/$productId" $manager).Body 'contentType')

    # The storage itself: any HTTP answer from the origin of the upload link.
    $probe = Invoke-Storage GET "$storageOrigin/" $null $null
    Assert-Step "17 the object storage answers (is the emulator up? docker compose up -d)" $true ($probe.Status -ne 0)
    if ($probe.Status -eq 0) { throw 'The object storage does not answer: start it (README "Product images") and run the script again.' }

    $put = Invoke-Storage $presign.Body.method $presign.Body.uploadUrl $png 'image/png'
    Assert-Step '18 PUT the PNG to the storage with the link -> 2xx' $true (Test-Success $put.Status)
    $confirm = Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $presign.Body.objectKey } 'en'
    Assert-Step '19 confirm -> 200' 200 $confirm.Status
    Assert-Step '20 ... image: PNG of the uploaded size' "image/png $($png.Length)" "$(Get-ImageField $confirm.Body 'contentType') $(Get-ImageField $confirm.Body 'sizeBytes')"
    $pngUrl = [string]$confirm.Body.image.url
    $firstUpdatedAt = Get-ImageField $confirm.Body 'updatedAt'
    $download = Invoke-Storage GET $pngUrl $null $null
    Assert-Step '21 GET image.url -> 200' 200 $download.Status
    Assert-Step '22 ... the same bytes' $true (Test-SameBytes $png $download.Bytes)
    $unsigned = Invoke-Storage GET ([uri]$pngUrl).GetLeftPart([System.UriPartial]::Path) $null $null
    Assert-Step '23 the same object without the signature is rejected' 403 $unsigned.Status

    # Every role reads the image; the link of another answer works as well.
    $asStaff = Invoke-Api GET "/api/products/$productId" $staff
    Assert-Step '24 staff reads the product with its image' 'image/png' (Get-ImageField $asStaff.Body 'contentType')
    Assert-Step '25 ... and its link returns the image' $true (Test-SameBytes $png (Invoke-Storage GET ([string]$asStaff.Body.image.url) $null $null).Bytes)
    $listed = @((Invoke-Api GET "/api/products?search=SMOKE-019-$n" $manager).Body.items)
    Assert-Step '26 the product list carries the image' 'image/png' (Get-ImageField ($listed | Where-Object { $_.productId -eq $productId }) 'contentType')

    # A text file declared as PNG: the storage takes it, the confirmation does not, and the object is deleted.
    $fake = Send-Upload $productId $manager 'image/png' $text
    Assert-Step '27 PUT a text file declared as PNG -> 2xx' $true (Test-Success $fake.UploadStatus)
    $rejected = Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $fake.objectKey } 'en'
    Assert-Step '28 confirm it -> 400' 400 $rejected.Status
    Assert-Step '29 ... on objectKey: not the image it claims to be' 'The content of the file is not an image of the type it was uploaded as.' (Get-FieldError $rejected 'objectKey')
    $again = Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $fake.objectKey } 'en'
    Assert-Step '30 its object is gone: confirming again finds no upload' 'No file has been uploaded for this key.' (Get-FieldError $again 'objectKey')
    Assert-Step '31 the product still has its PNG' $firstUpdatedAt (Get-ImageField (Invoke-Api GET "/api/products/$productId" $manager).Body 'updatedAt')

    # The key of another product.
    $foreign = Send-Upload $otherId $manager 'image/png' $png
    Assert-Step '32 upload for the second product -> 2xx' $true (Test-Success $foreign.UploadStatus)
    $wrongProduct = Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $foreign.objectKey } 'en'
    Assert-Step '33 confirming it on the first product -> 400' 400 $wrongProduct.Status
    Assert-Step '34 ... on objectKey' 'This is not an upload key of this product.' (Get-FieldError $wrongProduct 'objectKey')
    Assert-Step '35 ... and it is still the second product''s to confirm' 200 (Invoke-Api PUT "/api/products/$otherId/image" $manager @{ objectKey = $foreign.objectKey }).Status

    # Replace with a JPEG: the previous object is deleted, so its link is dead.
    $replacement = Send-Upload $productId $manager 'image/jpeg' $jpeg
    Assert-Step '36 upload a JPEG -> 2xx' $true (Test-Success $replacement.UploadStatus)
    $replaced = Invoke-ApiProblem PUT "/api/products/$productId/image" $manager @{ objectKey = $replacement.objectKey } 'en'
    Assert-Step '37 confirm -> 200, the image is the JPEG' "200 image/jpeg $($jpeg.Length)" "$($replaced.Status) $(Get-ImageField $replaced.Body 'contentType') $(Get-ImageField $replaced.Body 'sizeBytes')"
    Assert-Step '38 the link of the replaced PNG -> 404' 404 (Invoke-Storage GET $pngUrl $null $null).Status
    $jpegUrl = [string]$replaced.Body.image.url
    Assert-Step '39 the new link returns the JPEG' $true (Test-SameBytes $jpeg (Invoke-Storage GET $jpegUrl $null $null).Bytes)
    Assert-Step '40 confirming the current image again -> 200, nothing changes' "200 $(Get-ImageField $replaced.Body 'updatedAt')" (& {
        $repeat = Invoke-Api PUT "/api/products/$productId/image" $manager @{ objectKey = $replacement.objectKey }
        "$($repeat.Status) $(Get-ImageField $repeat.Body 'updatedAt')"
    })
    Assert-Step '41 ... and its object is still there' 200 (Invoke-Storage GET $jpegUrl $null $null).Status

    # Remove.
    Assert-Step '42 remove the image -> 204' 204 (Invoke-Api DELETE "/api/products/$productId/image" $manager).Status
    Assert-Step '43 ... the product has no image' 'no image' (Get-ImageField (Invoke-Api GET "/api/products/$productId" $manager).Body 'contentType')
    Assert-Step '44 ... its link -> 404' 404 (Invoke-Storage GET $jpegUrl $null $null).Status
    Assert-Step '45 remove again (no image) -> 204' 204 (Invoke-Api DELETE "/api/products/$productId/image" $manager).Status
}
catch {
    Assert-Step "unexpected error: $($_.Exception.Message)" 'no error' 'error'
}
finally {
    # No smoke product keeps an image (and so no object), and the smoke accounts are locked, also after a failed step.
    foreach ($id in $productId, $otherId) {
        if ($manager -and $id) { [void](Invoke-Api DELETE "/api/products/$id/image" $manager) }
    }
    if ($manager -and $otherId) {
        Assert-Step '46 the smoke products end without an image' 'no image' (Get-ImageField (Invoke-Api GET "/api/products/$otherId" $manager).Body 'contentType')
    }
    if ($admin -and $managerUser -and $managerUser.Body) {
        Assert-Step '47 lock the smoke manager' 200 (Invoke-Api POST "/api/users/$($managerUser.Body.userId)/lock" $admin).Status
    }
    if ($admin -and $staffUser -and $staffUser.Body) {
        Assert-Step '48 lock the smoke staff' 200 (Invoke-Api POST "/api/users/$($staffUser.Body.userId)/lock" $admin).Status
    }
}
Invoke-FrontendChecks 49
Complete-Smoke
