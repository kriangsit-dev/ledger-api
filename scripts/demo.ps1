<#
    Walks a running Ledger API through the behaviour the README claims, using real HTTP calls.

    Start the API first:
        docker compose up -d
        dotnet run --project src/Ledger.Api

    Then, in a second terminal:
        powershell -ExecutionPolicy Bypass -File scripts/demo.ps1
#>

$ErrorActionPreference = 'Stop'
$Base = 'http://localhost:5080/api/v1'

function Write-Step($number, $text) {
    Write-Host ''
    Write-Host ("{0,2}. {1}" -f $number, $text) -ForegroundColor Cyan
}

function Write-Ok($text) { Write-Host "    PASS  $text" -ForegroundColor Green }
function Write-Bad($text) { Write-Host "    FAIL  $text" -ForegroundColor Red; $script:Failures++ }

function Assert-Equal($expected, $actual, $what) {
    if ($expected -eq $actual) { Write-Ok "$what = $actual" }
    else { Write-Bad "$what : expected $expected, got $actual" }
}

# Returns the HTTP status code of a call that is expected to fail.
function Get-FailureStatus([scriptblock] $call) {
    try {
        & $call | Out-Null
        return 200
    }
    catch {
        if ($_.Exception.Response) { return [int] $_.Exception.Response.StatusCode }
        throw
    }
}

function New-Json($object) { $object | ConvertTo-Json -Depth 6 -Compress }

$script:Failures = 0
$suffix = -join ((48..57) + (65..90) | Get-Random -Count 8 | ForEach-Object { [char] $_ })

# ---------------------------------------------------------------------------

Write-Host ''
Write-Host 'Ledger API — live walkthrough' -ForegroundColor White
Write-Host "Target: $Base"

Write-Step 1 'Exchange client credentials for a bearer token'
$token = (Invoke-RestMethod -Method Post -Uri "$Base/auth/token" -ContentType 'application/json' `
        -Body (New-Json @{ clientId = 'demo-writer'; clientSecret = 'demo-writer-secret' })).accessToken
$write = @{ Authorization = "Bearer $token" }
Write-Ok ("token issued, {0} characters" -f $token.Length)

Write-Step 2 'Open two accounts'
$cash = Invoke-RestMethod -Method Post -Uri "$Base/accounts" -Headers $write -ContentType 'application/json' `
    -Body (New-Json @{ code = "DEMO-CASH-$suffix"; name = 'Cash'; type = 'Asset'; currency = 'THB' })
$sales = Invoke-RestMethod -Method Post -Uri "$Base/accounts" -Headers $write -ContentType 'application/json' `
    -Body (New-Json @{ code = "DEMO-SALES-$suffix"; name = 'Sales'; type = 'Revenue'; currency = 'THB' })
Write-Ok "$($cash.code) (Asset) and $($sales.code) (Revenue)"

$today = (Get-Date).ToString('yyyy-MM-dd')
$key = [guid]::NewGuid().ToString()

# Built from scratch per amount. Editing the JSON string instead would be fragile: PowerShell
# serialises 1500.00 as "1500", so a search-and-replace for "1500.0" silently changes nothing and
# the "different payload" case would quietly test the same payload twice.
function New-SaleBody([decimal] $amount) {
    New-Json @{
        reference   = "INV-$suffix"
        description = 'Cash sale'
        currency    = 'THB'
        occurredOn  = $today
        lines       = @(
            @{ accountId = $cash.id; direction = 'Debit'; amount = $amount },
            @{ accountId = $sales.id; direction = 'Credit'; amount = $amount }
        )
    }
}

$sale = New-SaleBody 1500

Write-Step 3 'Post a balanced entry of 1,500.00 THB'
$first = Invoke-WebRequest -Method Post -Uri "$Base/journal-entries" -Headers ($write + @{ 'Idempotency-Key' = $key }) `
    -ContentType 'application/json' -Body $sale -UseBasicParsing
$firstEntry = $first.Content | ConvertFrom-Json
Assert-Equal 201 ([int] $first.StatusCode) 'status'
Write-Ok "entry $($firstEntry.id), debits $($firstEntry.totalDebits) = credits $($firstEntry.totalCredits)"

Write-Step 4 'Send the very same request again (a client retry)'
$second = Invoke-WebRequest -Method Post -Uri "$Base/journal-entries" -Headers ($write + @{ 'Idempotency-Key' = $key }) `
    -ContentType 'application/json' -Body $sale -UseBasicParsing
$secondEntry = $second.Content | ConvertFrom-Json
Assert-Equal $firstEntry.id $secondEntry.id 'entry id'
if ($second.Headers['Idempotent-Replay']) { Write-Ok 'Idempotent-Replay: true — replayed, not re-posted' }
else { Write-Bad 'no Idempotent-Replay header' }

Write-Step 5 'Reuse the key with a different amount (a client bug, not a retry)'
$status = Get-FailureStatus {
    Invoke-RestMethod -Method Post -Uri "$Base/journal-entries" -Headers ($write + @{ 'Idempotency-Key' = $key }) `
        -ContentType 'application/json' -Body (New-SaleBody 9900)
}
Assert-Equal 409 $status 'status (expect 409 Conflict)'

Write-Step 6 'Post an entry where debits do not equal credits'
$status = Get-FailureStatus {
    Invoke-RestMethod -Method Post -Uri "$Base/journal-entries" -Headers ($write + @{ 'Idempotency-Key' = [guid]::NewGuid() }) `
        -ContentType 'application/json' -Body (New-Json @{
            reference = "BAD-$suffix"; description = 'Unbalanced'; currency = 'THB'; occurredOn = $today
            lines     = @(
                @{ accountId = $cash.id; direction = 'Debit'; amount = 100.00 },
                @{ accountId = $sales.id; direction = 'Credit'; amount = 90.00 })
        })
}
Assert-Equal 422 $status 'status (expect 422 Unprocessable)'

Write-Step 7 'Post without an Idempotency-Key at all'
$status = Get-FailureStatus {
    Invoke-RestMethod -Method Post -Uri "$Base/journal-entries" -Headers $write -ContentType 'application/json' -Body $sale
}
Assert-Equal 400 $status 'status (expect 400 Bad Request)'

Write-Step 8 'Read the cash balance'
$balance = Invoke-RestMethod -Uri "$Base/accounts/$($cash.id)/balance" -Headers $write
Assert-Equal 1500.0 ([decimal] $balance.balance) 'balance'
Write-Ok "normal side: $($balance.normalSide)"

Write-Step 9 'Reverse the entry, then read the balance again'
$reversal = Invoke-RestMethod -Method Post -Uri "$Base/journal-entries/$($firstEntry.id)/reversal" `
    -Headers ($write + @{ 'Idempotency-Key' = [guid]::NewGuid() }) -ContentType 'application/json' `
    -Body (New-Json @{ reference = "REV-$suffix" })
$original = Invoke-RestMethod -Uri "$Base/journal-entries/$($firstEntry.id)" -Headers $write
$after = Invoke-RestMethod -Uri "$Base/accounts/$($cash.id)/balance" -Headers $write
Assert-Equal 'Reversed' $original.status 'original entry status'
Assert-Equal $reversal.id $original.reversedByEntryId 'original points at its reversal'
Assert-Equal 0.0 ([decimal] $after.balance) 'balance after reversal'
Write-Ok 'the original entry still exists — nothing was deleted'

Write-Step 10 'Trial balance across every account'
$trial = Invoke-RestMethod -Uri "$Base/reports/trial-balance" -Headers $write
Assert-Equal ([decimal] $trial.totalDebits) ([decimal] $trial.totalCredits) 'total debits = total credits'

Write-Step 11 'Authorisation: a read-only token tries to write, then an anonymous read'
$readToken = (Invoke-RestMethod -Method Post -Uri "$Base/auth/token" -ContentType 'application/json' `
        -Body (New-Json @{ clientId = 'demo-reader'; clientSecret = 'demo-reader-secret' })).accessToken
$status = Get-FailureStatus {
    Invoke-RestMethod -Method Post -Uri "$Base/journal-entries" -Headers @{ Authorization = "Bearer $readToken"; 'Idempotency-Key' = [guid]::NewGuid() } `
        -ContentType 'application/json' -Body $sale
}
Assert-Equal 403 $status 'read-only token writing (expect 403 Forbidden)'
$status = Get-FailureStatus { Invoke-RestMethod -Uri "$Base/accounts" }
Assert-Equal 401 $status 'anonymous read (expect 401 Unauthorized)'

# ---------------------------------------------------------------------------

Write-Host ''
if ($script:Failures -eq 0) {
    Write-Host 'All checks passed against the running API.' -ForegroundColor Green
    exit 0
}

Write-Host "$($script:Failures) check(s) failed." -ForegroundColor Red
exit 1
