# Self-test of the strict Assert-Step, the result table and the SMOKE_SKIP_FRONTEND switch of _common.ps1. Needs no
# API, no database and no secrets.
# Usage (repository root):  powershell -ExecutionPolicy Bypass -File scripts\smoke\_assert-selftest.ps1
. (Join-Path $PSScriptRoot '_common.ps1')

$script:Cases = New-Object System.Collections.Generic.List[object]

# Runs Assert-Step with the two values and records whether its verdict is the wanted one ('pass', 'fail' or 'throw').
function Test-Case([string]$Name, [string]$Wanted, $Expected, $Actual) {
    $script:Results.Clear()
    try {
        Assert-Step $Name $Expected $Actual
        $got = if ($script:Results[0].OK -eq $true) { 'pass' } else { 'fail' }
    } catch {
        $got = 'throw'
    }
    $script:Cases.Add([pscustomobject]@{ Case = $Name; Wanted = $Wanted; Got = $got; OK = ($Wanted -ceq $got) })
}

function Add-Case([string]$Name, [bool]$Ok) {
    $script:Cases.Add([pscustomobject]@{ Case = $Name; Wanted = 'True'; Got = "$Ok"; OK = $Ok })
}

$json = '{"int":5,"big":5000000000,"dec":30000.50,"exp":1e2,"yes":true,"no":false,"none":null,"text":"Open","list":[1],"obj":{"a":1}}' | ConvertFrom-Json

# null only equals null.
Test-Case 'null = null' 'pass' $null $null
Test-Case 'null vs empty string' 'fail' $null ''
Test-Case 'empty string vs null' 'fail' '' $null
Test-Case 'null vs 0' 'fail' $null 0
Test-Case 'null vs false' 'fail' $null $false
Test-Case 'JSON null = null' 'pass' $null $json.none
Test-Case 'missing member = null' 'pass' $null $json.missing

# A boolean only equals a boolean.
Test-Case 'true = true' 'pass' $true $true
Test-Case 'false = false' 'pass' $false $false
Test-Case 'true vs false' 'fail' $true $false
Test-Case 'true vs any text' 'fail' $true 'any text'
Test-Case 'true vs the text True' 'fail' $true 'True'
Test-Case 'true vs the text False' 'fail' $true 'False'
Test-Case 'true vs 1' 'fail' $true 1
Test-Case 'false vs 0' 'fail' $false 0
Test-Case 'false vs empty string' 'fail' $false ''
Test-Case 'false vs null' 'fail' $false $null
Test-Case 'the text True vs true' 'fail' 'True' $true
Test-Case 'true = JSON true' 'pass' $true $json.yes
Test-Case 'false = JSON false' 'pass' $false $json.no

# Numbers are compared by value, whatever their numeric type; never with a string.
Test-Case '1 vs the text 1' 'fail' 1 '1'
Test-Case 'the text 1 vs 1' 'fail' '1' 1
Test-Case 'int 5 = long 5' 'pass' ([int]5) ([long]5)
Test-Case 'int 5 = decimal 5' 'pass' ([int]5) ([decimal]5)
Test-Case 'int 5 = double 5' 'pass' ([int]5) ([double]5)
Test-Case 'decimal 30000.50 = double 30000.5' 'pass' ([decimal]30000.50) ([double]30000.5)
Test-Case 'long 5 vs int 6' 'fail' ([long]5) ([int]6)
Test-Case 'decimal 0.5 vs int 0' 'fail' ([decimal]0.5) ([int]0)
Test-Case 'int 5 = JSON 5' 'pass' 5 $json.int
Test-Case 'long = JSON number above Int32' 'pass' ([long]5000000000) $json.big
Test-Case 'decimal = JSON decimal' 'pass' ([decimal]30000.5) $json.dec
Test-Case 'int 100 = JSON 1e2' 'pass' 100 $json.exp
Test-Case 'the text 5 vs JSON 5' 'fail' '5' $json.int

# Strings are compared case-sensitively.
Test-Case 'Open = Open' 'pass' 'Open' 'Open'
Test-Case 'Open vs open' 'fail' 'Open' 'open'
Test-Case 'Open vs Open with a trailing space' 'fail' 'Open' 'Open '
Test-Case 'empty string = empty string' 'pass' '' ''
Test-Case 'Open = JSON text' 'pass' 'Open' $json.text

# Every other type throws: the caller compares explicitly.
Test-Case 'array vs array' 'throw' @(1, 2) @(1, 2)
Test-Case 'number vs one-element array' 'throw' 1 $json.list
Test-Case 'empty array vs null' 'throw' @() $null
Test-Case 'object vs object' 'throw' $json.obj $json.obj
Test-Case 'hashtable vs hashtable' 'throw' @{ a = 1 } @{ a = 1 }
Test-Case 'true vs object' 'throw' $true $json.obj
Test-Case 'date vs date' 'throw' ([datetime]'2026-10-03') ([datetime]'2026-10-03')
Test-Case 'guid vs its text' 'throw' ([guid]'11111111-1111-1111-1111-111111111111') '11111111-1111-1111-1111-111111111111'

# A failed step shows both values with their type names; a passed step keeps the plain values.
$script:Results.Clear()
Assert-Step 'shown' $true 'any text'
Add-Case 'failed step: expected shown with its type' ($script:Results[0].Expected -ceq 'True [Boolean]')
Add-Case 'failed step: actual shown with its type' ($script:Results[0].Actual -ceq 'any text [String]')
$script:Results.Clear()
Assert-Step 'shown' 1 $null
Add-Case 'failed step: null shown as null' ($script:Results[0].Actual -ceq ' [null]')
$script:Results.Clear()
Assert-Step 'shown' 200 200
Add-Case 'passed step: plain values' (($script:Results[0].Expected -is [int]) -and ($script:Results[0].Actual -is [int]))
Add-Case 'a thrown comparison records no step' ($(try { Assert-Step 'thrown' 1 @(1); $false } catch { $script:Results.Count -eq 1 }))

# The result table: OK first, and values of any length stay inside their columns (no column is dropped, no line is longer
# than the table).
$script:Results.Clear()
Assert-Step '01 short' 200 200
Assert-Step ('02 a step whose description is much longer than its column: ' + ('word ' * 30)) ('Z' * 150) ('Z' * 150)
Assert-Step '03 after the long one' $true 'any text'
$table = Format-SmokeTable $script:Results
$lines = @($table -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
$tableWidth = $script:TableWidths.OK + $script:TableWidths.Step + $script:TableWidths.Expected + $script:TableWidths.Actual + 3
Add-Case 'table: the header starts with OK and has all four columns' ($lines[0] -cmatch '^OK\s+Step\s+Expected\s+Actual\s*$')
Add-Case 'table: no line is longer than the table' (@($lines | Where-Object { $_.TrimEnd().Length -gt $tableWidth }).Count -eq 0)
Add-Case 'table: every step starts its row with its verdict' (
    (@($lines | Where-Object { $_ -cmatch '^True\s+01 short' }).Count -eq 1) -and
    (@($lines | Where-Object { $_ -cmatch '^True\s+02 a step' }).Count -eq 1) -and
    (@($lines | Where-Object { $_ -cmatch '^False\s+03 after the long one' }).Count -eq 1))
# Both values of step 02 are in the table completely: 2 x 150 characters, spread over several lines.
Add-Case 'table: a long value is wrapped, not cut' (($table.ToCharArray() | Where-Object { $_ -ceq [char]'Z' }).Count -eq 300)
$script:Results.Clear()

# SMOKE_SKIP_FRONTEND: only the value 1, only for another environment (SMOKE_BASE_URL); refused on the local default.
function Get-SkipVerdict([string]$Skip, [bool]$Foreign) {
    try { return "$(Test-SkipFrontend $Skip $Foreign)" } catch { return 'throw' }
}
Add-Case 'skip: not set, local default -> the checks run' ((Get-SkipVerdict '' $false) -ceq 'False')
Add-Case 'skip: not set, another environment -> the checks run' ((Get-SkipVerdict '' $true) -ceq 'False')
Add-Case 'skip: 1 with another environment -> skipped' ((Get-SkipVerdict '1' $true) -ceq 'True')
Add-Case 'skip: 1 on the local default -> refused' ((Get-SkipVerdict '1' $false) -ceq 'throw')
Add-Case 'skip: 0 on the local default -> the checks run' ((Get-SkipVerdict '0' $false) -ceq 'False')
Add-Case 'skip: another value than 1 -> the checks run' ((Get-SkipVerdict 'true' $true) -ceq 'False')
Add-Case 'skip: the refusal names both variables' ($(try { [void](Test-SkipFrontend '1' $false); $false } catch { ($_.Exception.Message -clike '*SMOKE_SKIP_FRONTEND*') -and ($_.Exception.Message -clike '*SMOKE_BASE_URL*') }))
# A skipped run records exactly one passed step that names the variable, and starts no npm.
$savedSkip = $script:SkipFrontend
$script:SkipFrontend = $true
$script:Results.Clear()
Invoke-FrontendChecks 7
Add-Case 'skip: one step in the table' ($script:Results.Count -eq 1)
Add-Case 'skip: the step is numbered, names the variable and passes' (
    ($script:Results[0].Step -ceq '07 frontend checks skipped (SMOKE_SKIP_FRONTEND)') -and ($script:Results[0].OK -eq $true) -and
    ($script:Results[0].Expected -ceq 'skipped') -and ($script:Results[0].Actual -ceq 'skipped'))
$script:SkipFrontend = $savedSkip
$script:Results.Clear()

$script:Cases | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$failed = @($script:Cases | Where-Object { -not $_.OK }).Count
if ($failed -eq 0) { Write-Host "PASSED: all $($script:Cases.Count) cases." -ForegroundColor Green; exit 0 }
Write-Host "FAILED: $failed of $($script:Cases.Count) cases." -ForegroundColor Red
exit 1
