<#
.SYNOPSIS
    Sets an account's password to something you know.

.DESCRIPTION
    There is no password reset in the app and no admin screen: an account's password is stored as a
    PBKDF2 hash and nothing can read it back. When somebody forgets theirs - and the account is the
    only thing tying a night's hands to a name - this is how they get back in.

    The hash is written exactly the way PasswordHasher in Mahjong.Infrastructure writes it:
    PBKDF2-SHA256, 210,000 iterations, a fresh 16-byte salt, a 32-byte hash. Nothing else about the
    account changes: the games played under it stay attached to it, because a seat points at the
    account id and this touches neither.

    Existing sessions for the account are deleted, so a phone that was still signed in under the old
    password has to sign in again. That is the point rather than a side effect - a password is reset
    when somebody else may know the old one.

    This is the account version of reset-password.ps1, which does the same for a room password.

.PARAMETER Username
    Whose account. Case does not matter, the same way it does not at sign-in. An account Id is
    accepted too, for when that is what you have to hand.

.PARAMETER Password
    The new password. Signing up asks for 8 characters; anything shorter still works here and says
    so, because a password set by hand for somebody locked out is meant to be changed after.

.PARAMETER ConnectionString
    Overrides the connection string. Read from server/src/Mahjong.Api/appsettings.json otherwise.

.EXAMPLE
    .\tools\reset-pw.ps1 alice "pass"

.EXAMPLE
    .\tools\reset-pw.ps1 -Username alice -Password "correct horse battery"

.EXAMPLE
    .\tools\reset-pw.ps1 5B0E1E6A-9E5B-4A0E-9A7C-2C0C0F2A11D4 "pass"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string] $Username,

    [Parameter(Mandatory, Position = 1)]
    [string] $Password,

    [string] $ConnectionString
)

$ErrorActionPreference = 'Stop'

# Must match PasswordHasher in server/src/Mahjong.Infrastructure/Security.cs. If those constants
# ever change, the accounts this script touches stop accepting their password.
$iterations = 210000
$saltBytes = 16
$hashBytes = 32

# UserName in the same file. Only the ceiling is enforced below: the floor is what signing up asks
# for, and refusing to unlock somebody's account over it helps nobody.
$maxPasswordLength = 128
$minPasswordLength = 8

# ------------------------------------------------------------------ where the database is

function Get-MahjongConnection {
    param([string] $Override)

    if ($Override) { return $Override }

    $settings = Join-Path $PSScriptRoot '..\server\src\Mahjong.Api\appsettings.json'
    if (-not (Test-Path $settings)) {
        throw "Could not find appsettings.json at $settings. Pass -ConnectionString instead."
    }

    $value = (Get-Content $settings -Raw | ConvertFrom-Json).ConnectionStrings.Mahjong
    if (-not $value) { throw 'ConnectionStrings:Mahjong is not set in appsettings.json.' }

    return $value
}

function Get-ConnectionPart {
    param([string] $Connection, [string[]] $Keys, [string] $Default)

    foreach ($pair in $Connection -split ';') {
        $split = $pair -split '=', 2
        if ($split.Count -eq 2 -and $Keys -contains $split[0].Trim()) { return $split[1].Trim() }
    }

    return $Default
}

$connection = Get-MahjongConnection -Override $ConnectionString
$server = Get-ConnectionPart -Connection $connection -Keys @('Server', 'Data Source') -Default '.'
$database = Get-ConnectionPart -Connection $connection -Keys @('Database', 'Initial Catalog') -Default 'MahjongDb'

$sqlcmd = (Get-Command sqlcmd -ErrorAction SilentlyContinue).Source
if (-not $sqlcmd) { throw 'sqlcmd is not on PATH. It ships with SQL Server; install the command line tools.' }

function Invoke-Sql {
    param([string] $Query)

    # -b makes sqlcmd exit non-zero on a SQL error, which $ErrorActionPreference then turns into a
    # thrown terminating error rather than a silently wrong result.
    $output = & $sqlcmd -S $server -d $database -E -C -b -h -1 -W -s '|' -Q "SET NOCOUNT ON; $Query" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed: $output" }

    return @($output | Where-Object { $_ -and $_.ToString().Trim() -ne '' })
}

# ------------------------------------------------------------------ find the account

if ($Password.Length -eq 0) { throw 'Give a password.' }

if ($Password.Length -gt $maxPasswordLength) {
    throw "Use at most $maxPasswordLength characters. That is the ceiling the register endpoint enforces."
}

$guid = [guid]::Empty
$isGuid = [guid]::TryParse($Username, [ref] $guid)

# Matched on UsernameKey rather than Username, because that is the column the unique index is on
# and folding case is exactly what makes "Alice" and "alice" one account at sign-in.
$key = $Username.Trim().ToLowerInvariant() -replace "'", "''"

$lookup = if ($isGuid) {
    "SELECT TOP 1 CONVERT(nvarchar(36), u.Id), u.Username FROM Users u WHERE u.Id = '$guid';"
}
else {
    "SELECT TOP 1 CONVERT(nvarchar(36), u.Id), u.Username FROM Users u WHERE u.UsernameKey = '$key';"
}

# @() at the call site, not just inside the function: returning a one-element array from a function
# unrolls it to a bare string, and indexing that gives a character rather than the row.
$found = @(Invoke-Sql -Query $lookup)

if ($found.Count -eq 0) {
    throw "No account matched '$Username'. Registering is the only thing that creates one - this only resets a password."
}

$parts = $found[0] -split '\|'
$userGuid = $parts[0].Trim()
$name = $parts[1].Trim()

# ------------------------------------------------------------------ hash and write

$salt = [byte[]]::new($saltBytes)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($salt)

$hash = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
    [System.Text.Encoding]::UTF8.GetBytes($Password),
    $salt,
    $iterations,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    $hashBytes)

$hashHex = '0x' + [Convert]::ToHexString($hash)
$saltHex = '0x' + [Convert]::ToHexString($salt)

# Counted before the delete, so the line printed below is what was actually signed out rather than
# the zero rows left afterwards.
$sessions = @(Invoke-Sql -Query "SELECT COUNT(*) FROM UserSessions WHERE UserId = '$userGuid';")
$signedOut = [int] ($sessions[0].Trim())

Invoke-Sql -Query @"
UPDATE Users
SET PasswordHash = $hashHex, PasswordSalt = $saltHex, PasswordIterations = $iterations
WHERE Id = '$userGuid';
DELETE FROM UserSessions WHERE UserId = '$userGuid';
"@ | Out-Null

Write-Host ''
Write-Host "  Password reset for $name" -ForegroundColor Green
Write-Host "  Account  $userGuid"
Write-Host "  Signed out  $signedOut $(if ($signedOut -eq 1) { 'session' } else { 'sessions' })"

if ($Password.Length -lt $minPasswordLength) {
    Write-Host "  Shorter than the $minPasswordLength characters signing up asks for. Change it once you are back in." -ForegroundColor Yellow
}

Write-Host ''
