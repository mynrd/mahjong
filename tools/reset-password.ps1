<#
.SYNOPSIS
    Sets a room's password to something you know.

.DESCRIPTION
    There is no account recovery in this game and no admin screen: a room's password is stored as a
    PBKDF2 hash and nothing can read it back. When the password to an old room is forgotten - and
    the replay viewer asks for it before it will show anything - this is how you get back in.

    The hash is written exactly the way PasswordHasher in Mahjong.Infrastructure writes it:
    PBKDF2-SHA256, 210,000 iterations, a fresh 16-byte salt, a 32-byte hash. Anyone still holding a
    seat token stays seated; only joining and unlocking replays go through the password.

    Existing replay tokens for the room are deleted, so a browser that unlocked the replays under
    the old password has to type the new one.

.PARAMETER RoomId
    Which room. Takes a room Id, a room code such as 2XAJZ9, or the Id of any game played in the
    room - whichever you happen to have to hand.

.PARAMETER Password
    The new password. Four characters minimum, the same floor the create-room endpoint enforces.

.PARAMETER ConnectionString
    Overrides the connection string. Read from server/src/Mahjong.Api/appsettings.json otherwise.

.EXAMPLE
    .\tools\reset-password.ps1 -RoomId 83C285AB-EAAC-42F6-8B06-7FF4DB88471B -Password "Test"

.EXAMPLE
    .\tools\reset-password.ps1 -RoomId 2XAJZ9 -Password "Test"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RoomId,

    [Parameter(Mandatory)]
    [string] $Password,

    [string] $ConnectionString
)

$ErrorActionPreference = 'Stop'

# Must match PasswordHasher in server/src/Mahjong.Infrastructure/Security.cs. If those constants
# ever change, the rooms this script touches stop accepting their password.
$iterations = 210000
$saltBytes = 16
$hashBytes = 32

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

# ------------------------------------------------------------------ find the room

if ($Password.Length -lt 4) {
    throw 'Use at least 4 characters. That is the floor the create-room endpoint enforces.'
}

$code = ($RoomId.ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) }) -join ''
$code = $code.ToUpperInvariant()
$guid = [guid]::Empty
$isGuid = [guid]::TryParse($RoomId, [ref] $guid)

# A room Id, a game Id and a room code all get accepted, because whichever one is on screen when
# you need this is the one you will paste in.
$lookup = if ($isGuid) {
    @"
SELECT TOP 1 CONVERT(nvarchar(36), r.Id), r.Code, r.Name
FROM Rooms r
WHERE r.Id = '$guid' OR r.Id = (SELECT g.RoomId FROM Games g WHERE g.Id = '$guid');
"@
}
else {
    "SELECT TOP 1 CONVERT(nvarchar(36), r.Id), r.Code, r.Name FROM Rooms r WHERE r.Code = '$($code -replace "'", "''")';"
}

# @() at the call site, not just inside the function: returning a one-element array from a function
# unrolls it to a bare string, and indexing that gives a character rather than the row.
$found = @(Invoke-Sql -Query $lookup)

if ($found.Count -eq 0) {
    throw "No room matched '$RoomId'. Try the room code, the room Id, or the Id of a game played there."
}

$parts = $found[0] -split '\|'
$roomGuid = $parts[0].Trim()
$roomCode = $parts[1].Trim()
$roomName = $parts[2].Trim()

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

Invoke-Sql -Query @"
UPDATE Rooms
SET PasswordHash = $hashHex, PasswordSalt = $saltHex, PasswordIterations = $iterations
WHERE Id = '$roomGuid';
DELETE FROM ReplayTokens WHERE RoomId = '$roomGuid';
"@ | Out-Null

Write-Host ''
Write-Host "  Password reset for $roomCode ($roomName)" -ForegroundColor Green
Write-Host "  Room Id  $roomGuid"
Write-Host "  Replays  /room/$roomCode/replay"
Write-Host ''
