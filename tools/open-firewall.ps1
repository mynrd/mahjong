<#
.SYNOPSIS
    Lets other devices on the local network reach the game. Run once, as administrator.

.DESCRIPTION
    Windows blocks inbound connections to ports 5080 and 4200 by default, which is why a phone on
    the same wifi gets a page that never loads. This adds the two inbound rules.

    The rules are scoped to the Private network profile only. On a public network - a cafe, an
    airport - the ports stay shut, because a game with no accounts and a four-character password
    is not something to expose to strangers on the same access point.

.PARAMETER Remove
    Takes the rules back out again.

.EXAMPLE
    .\tools\open-firewall.ps1

.EXAMPLE
    .\tools\open-firewall.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent())
    .IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host ''
    Write-Host '  This needs an elevated PowerShell. Right-click PowerShell, Run as administrator,' -ForegroundColor Yellow
    Write-Host '  then run this script again.' -ForegroundColor Yellow
    Write-Host ''
    exit 1
}

$rules = @(
    @{ Name = 'Mahjong API (5080)'; Port = 5080 },
    @{ Name = 'Mahjong web (4200)'; Port = 4200 }
)

foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue

    if ($Remove) {
        if ($existing) {
            $existing | Remove-NetFirewallRule
            Write-Host "  removed  $($rule.Name)" -ForegroundColor DarkGray
        }
        else {
            Write-Host "  not present  $($rule.Name)" -ForegroundColor DarkGray
        }
        continue
    }

    if ($existing) {
        Write-Host "  already there  $($rule.Name)" -ForegroundColor DarkGray
        continue
    }

    New-NetFirewallRule `
        -DisplayName $rule.Name `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $rule.Port `
        -Profile Private `
        -Description 'Local network Mahjong game. Private networks only.' | Out-Null

    Write-Host "  opened  $($rule.Name)  (private networks only)" -ForegroundColor Green
}

Write-Host ''
Write-Host '  Done. Start the game with .\run.ps1 from the project folder.' -ForegroundColor DarkGray
Write-Host ''
