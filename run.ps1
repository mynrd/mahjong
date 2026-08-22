<#
.SYNOPSIS
    Starts the Mahjong server and web app, and prints the link to share with the other players.

.DESCRIPTION
    Both halves listen on every interface so phones and tablets on the same wifi can reach them.

    Before starting anything it checks the things that otherwise fail confusingly: SQL Server not
    running, a port still held by a previous run, or the web app's dependencies never installed.

    Ctrl+C stops both. The whole process tree is killed, not just the launcher - `dotnet run` and
    `npx` each spawn the real process as a child, so killing only what was launched leaves the app
    still holding the port.

.PARAMETER ApiPort
    Port for the .NET API. The web app looks for the API on this port, so changing it means
    changing apiBaseUrl() in web/src/app/core/api.ts too.

.PARAMETER WebPort
    Port for the Angular dev server.

.PARAMETER Address
    Override the detected network address, for when the guess is wrong.

.PARAMETER ApiOnly
    Start only the API. Useful when running the Playwright suite against a separate web server.

.PARAMETER WebOnly
    Start only the web app.

.EXAMPLE
    .\run.ps1

.EXAMPLE
    .\run.ps1 -Address 192.168.1.42
#>
[CmdletBinding()]
param(
    [int] $ApiPort = 5080,
    [int] $WebPort = 4200,
    [string] $Address,
    [switch] $ApiOnly,
    [switch] $WebOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# ---------------------------------------------------------------- helpers

function Get-LanAddress {
    # Prefer the interface carrying the default route: that is the one other devices can reach.
    # Virtual and VPN adapters are filtered out by name, because several of them also hold a
    # default route and handing out a Tailscale or VMware address produces a link that looks fine
    # and silently never connects.
    $excluded = 'Loopback|VMware|Hyper-V|vEthernet|Tailscale|NordLynx|VirtualBox|Bluetooth'

    $candidates = Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object {
            $_.InterfaceAlias -notmatch $excluded -and
            $_.IPAddress -notlike '127.*' -and
            $_.IPAddress -notlike '169.254.*'
        }

    $routed = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric, InterfaceMetric

    foreach ($route in $routed) {
        $match = $candidates | Where-Object InterfaceIndex -eq $route.InterfaceIndex | Select-Object -First 1
        if ($match) { return $match.IPAddress }
    }

    $fallback = $candidates | Select-Object -First 1
    if ($fallback) { return $fallback.IPAddress }

    return 'localhost'
}

function Test-PortFree {
    param([int] $Port)

    $listening = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if (-not $listening) { return }

    $owners = $listening.OwningProcess | Sort-Object -Unique | ForEach-Object {
        $p = Get-Process -Id $_ -ErrorAction SilentlyContinue
        if ($p) { "$($p.ProcessName) (pid $($p.Id))" } else { "pid $_" }
    }

    throw "Port $Port is already in use by $($owners -join ', '). Stop it, or run with a different port."
}

function Stop-Tree {
    param([System.Diagnostics.Process] $Process)

    if (-not $Process -or $Process.HasExited) { return }

    # /T takes the children with it. `dotnet run` and `npx` are launchers: the process actually
    # holding the port is a grandchild, and killing only the launcher orphans it.
    & taskkill.exe /PID $Process.Id /T /F 2>&1 | Out-Null
}

# ---------------------------------------------------------------- checks

if (-not $Address) { $Address = Get-LanAddress }

$startApi = -not $WebOnly
$startWeb = -not $ApiOnly

if ($startApi) {
    $sql = Get-Service -Name 'MSSQLSERVER' -ErrorAction SilentlyContinue
    if (-not $sql) {
        throw 'SQL Server (MSSQLSERVER) is not installed on this machine. The API needs it.'
    }
    if ($sql.Status -ne 'Running') {
        throw "SQL Server is installed but $($sql.Status). Start it with: Start-Service MSSQLSERVER"
    }

    Test-PortFree -Port $ApiPort
}

if ($startWeb) {
    if (-not (Test-Path "$root\web\node_modules")) {
        throw "The web app's dependencies are missing. Run: cd web; npm install"
    }

    Test-PortFree -Port $WebPort
}

# ---------------------------------------------------------------- go

Write-Host ''
Write-Host '  Filipino Mahjong' -ForegroundColor Green
Write-Host '  ----------------' -ForegroundColor DarkGray

if ($startApi) { Write-Host "  API   http://${Address}:${ApiPort}" }
if ($startWeb) { Write-Host "  Web   http://${Address}:${WebPort}" }

Write-Host ''

if ($startWeb) {
    Write-Host '  Share this with the other three players:' -ForegroundColor DarkGray
    Write-Host "  http://${Address}:${WebPort}" -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  They need to be on the same wifi. If nothing loads on their phone, run' -ForegroundColor DarkGray
    Write-Host '  tools\open-firewall.ps1 once, as administrator.' -ForegroundColor DarkGray
    Write-Host ''
}

$processes = @()

try {
    # -NoNewWindow matters for more than tidiness. Without it the child gets no console, so its
    # output goes nowhere and, worse, anything that reads stdin blocks forever with nothing on
    # screen to say why: the Angular CLI sat there for minutes having used half a second of CPU,
    # waiting on a prompt nobody could see. Sharing this console means their logs appear inline
    # and a prompt is answerable.
    if ($startApi) {
        $processes += Start-Process -PassThru -NoNewWindow -FilePath 'dotnet' `
            -ArgumentList 'run', '--project', "$root\server\src\Mahjong.Api", '--urls', "http://0.0.0.0:$ApiPort" `
            -WorkingDirectory "$root\server"
    }

    if ($startWeb) {
        # The globally installed Angular CLI is v14 and too old for this project, so the copy in
        # web/node_modules is used instead. cmd.exe is the launcher because npx is a shell script.
        $processes += Start-Process -PassThru -NoNewWindow -FilePath 'cmd.exe' `
            -ArgumentList '/c', "npx ng serve --host 0.0.0.0 --port $WebPort --allowed-hosts" `
            -WorkingDirectory "$root\web"
    }

    Write-Host "  Running. Ctrl+C stops everything." -ForegroundColor DarkGray
    Write-Host ''

    while ($processes | Where-Object { -not $_.HasExited }) {
        Start-Sleep -Milliseconds 500

        # If one half dies on its own, take the other down too rather than leaving half a game up.
        if ($processes | Where-Object { $_.HasExited }) { break }
    }
}
finally {
    Write-Host ''
    Write-Host '  Stopping...' -ForegroundColor DarkGray

    foreach ($process in $processes) { Stop-Tree -Process $process }

    # Belt and braces: if anything is somehow still holding a port, say so rather than leaving the
    # next run to fail with a confusing bind error.
    foreach ($port in @($ApiPort, $WebPort)) {
        $left = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        if ($left) {
            Write-Host "  warning: something is still listening on $port (pid $($left.OwningProcess -join ', '))" -ForegroundColor Yellow
        }
    }

    Write-Host '  Stopped.' -ForegroundColor DarkGray
    Write-Host ''
}
