# Sets IMAGE_TAG - the tag of the images a production stack (docker/docker-compose.prod.yml) builds and runs - in the
# environment file of that stack. docs/runbook.md "Release", "Rollback". Windows PowerShell 5.1 compatible, ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File scripts\deploy\set-image-tag.ps1 -EnvFile docker\.env.prod              # this commit
#   powershell -ExecutionPolicy Bypass -File scripts\deploy\set-image-tag.ps1 -EnvFile docker\.env.prod -Tag <tag>   # a rollback
#
# Without -Tag the tag is the short commit (12 characters) of the checkout. Only that one line is changed (added when
# the file has none); the previous and the new tag are printed - neither is a secret - and nothing else of the file.
# The previous tag is what a rollback puts back: note it.
param(
    [Parameter(Mandatory = $true)][string]$EnvFile,
    [string]$Tag
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Tag) {
    $Tag = [string](& git -C $repoRoot rev-parse --short=12 HEAD)
    if (($LASTEXITCODE -ne 0) -or (-not $Tag)) { throw 'The commit of the checkout could not be read (git rev-parse); give -Tag.' }
    $Tag = $Tag.Trim()
}
if ($Tag -cnotmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$') { throw '-Tag must be an image tag: letters, digits, "_", "." and "-", at most 128 characters.' }
if (-not (Test-Path $EnvFile)) { throw "Environment file not found: $EnvFile" }

$path = (Resolve-Path $EnvFile).Path
$text = [System.IO.File]::ReadAllText($path)
$newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$lines = New-Object System.Collections.Generic.List[string]
$lines.AddRange([string[]]($text -split "`r?`n"))

$previous = '(not set)'
$found = $false
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -cmatch '^IMAGE_TAG=(.*)$') {
        if ($found) { throw 'The file has more than one IMAGE_TAG line.' }
        $found = $true
        if ($Matches[1]) { $previous = $Matches[1] }
        $lines[$i] = "IMAGE_TAG=$Tag"
    }
}
if (-not $found) {
    # Before the empty element a final line end leaves behind.
    $at = $lines.Count
    if (($at -gt 0) -and ($lines[$at - 1] -eq '')) { $at-- }
    $lines.Insert($at, "IMAGE_TAG=$Tag")
}

# UTF-8 without a BOM, the line ends the file had: docker compose reads the file as it is.
[System.IO.File]::WriteAllText($path, ($lines -join $newline), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "IMAGE_TAG: $previous -> $Tag   ($path)"
