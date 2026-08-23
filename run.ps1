<#
.SYNOPSIS
    Builds the web app into the server's wwwroot, starts the server, and prints the link to share
    with the other players.

.DESCRIPTION
    One process serves everything. `ng build` writes the page into server/src/Mahjong.Api/wwwroot
    (see web/angular.json) and Kestrel serves it alongside /api and /hubs/game, so there is one
    port to open, one link to hand out and one thing to deploy.

    It listens on every interface so phones and tablets on the same wifi can reach it. With no
    wifi address to hand out it falls back to the Tailscale one, so a game still works when the
    four of you are in four different houses.

    Before starting anything it checks the things that otherwise fail confusingly: SQL Server not
    running, a port still held by a previous run, or the web app's dependencies never installed.

    Ctrl+C stops it. The whole process tree is killed, not just the launcher - `dotnet run` spawns
    the real process as a child, so killing only what was launched leaves the app still holding
    the port.

.PARAMETER Port
    The one port everything is served on: page, API and websocket.

.PARAMETER Address
    Override the detected network address, for when the guess is wrong.

.PARAMETER SkipWebBuild
    Start the server against whatever is already in wwwroot. For quick restarts when only the
    server changed.

.PARAMETER Watch
    Rebuild the page whenever a file under web/src changes. The browser does not reload itself -
    refresh the tab once the rebuild is logged.

.EXAMPLE
    .\run.ps1

.EXAMPLE
    .\run.ps1 -Watch

.EXAMPLE
    .\run.ps1 -Address 192.168.1.42
#>
[CmdletBinding()]
param(
    [int] $Port = 5080,
    [string] $Address,
    [switch] $SkipWebBuild,
    [switch] $Watch
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$webRoot = "$root\web"
$wwwroot = "$root\server\src\Mahjong.Api\wwwroot"

# ---------------------------------------------------------------- helpers

function Get-TailnetAddress {
    # Tailscale hands out addresses from 100.64.0.0/10, the shared range in RFC 6598. Matched on
    # the range rather than on the adapter name, so it still works if the interface has been
    # renamed, and so it never picks up some other VPN that happens to have Tailscale in its name.
    $ip = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -match '^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.' } |
        Select-Object -First 1

    if ($ip) { return $ip.IPAddress }
    return $null
}

function Get-LanAddress {
    # Prefer the interface carrying the default route: that is the one other devices can reach.
    # Virtual and VPN adapters are filtered out by name, because several of them also hold a
    # default route and handing out a VMware address produces a link that looks fine and silently
    # never connects.
    #
    # Tailscale is in that list too, but only as a second choice rather than a permanent exclusion:
    # on the same wifi the LAN address is the better link, and off it the tailnet address is the
    # only one that works at all. Preferring the LAN and falling back keeps both cases right.
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

    $tailnet = Get-TailnetAddress
    if ($tailnet) { return $tailnet }

    return 'localhost'
}

function Test-TailnetAddress {
    param([string] $Value)

    return $Value -match '^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.'
}

function Test-PortFree {
    param([int] $Value)

    $listening = Get-NetTCPConnection -LocalPort $Value -State Listen -ErrorAction SilentlyContinue
    if (-not $listening) { return }

    $owners = $listening.OwningProcess | Sort-Object -Unique | ForEach-Object {
        $p = Get-Process -Id $_ -ErrorAction SilentlyContinue
        if ($p) { "$($p.ProcessName) (pid $($p.Id))" } else { "pid $_" }
    }

    throw "Port $Value is already in use by $($owners -join ', '). Stop it, or run with a different port."
}

function Stop-Tree {
    param([System.Diagnostics.Process] $Process)

    if (-not $Process -or $Process.HasExited) { return }

    # /T takes the children with it. `dotnet run` and `npx` are launchers: the process actually
    # doing the work is a grandchild, and killing only the launcher orphans it.
    & taskkill.exe /PID $Process.Id /T /F 2>&1 | Out-Null
}

# ---------------------------------------------------------------- checks

if (-not $Address) { $Address = Get-LanAddress }

$sql = Get-Service -Name 'MSSQLSERVER' -ErrorAction SilentlyContinue
if (-not $sql) {
    throw 'SQL Server (MSSQLSERVER) is not installed on this machine. The server needs it.'
}
if ($sql.Status -ne 'Running') {
    throw "SQL Server is installed but $($sql.Status). Start it with: Start-Service MSSQLSERVER"
}

Test-PortFree -Value $Port

if ($SkipWebBuild) {
    if (-not (Test-Path "$wwwroot\index.html")) {
        throw '-SkipWebBuild was given but there is nothing in wwwroot to serve. Run without it once.'
    }
}
elseif (-not (Test-Path "$webRoot\node_modules")) {
    throw "The web app's dependencies are missing. Run: cd web; npm install"
}

# ---------------------------------------------------------------- build

$processes = @()

try {
    if (-not $SkipWebBuild) {
        if ($Watch) {
            # The watcher does the first build itself, so building once up front would be the same
            # work twice. Wait for it to land instead: the server starts fine against an empty
            # wwwroot, but the first player to load the page would get a 404.
            Write-Host ''
            Write-Host '  Building the web app (watching for changes)...' -ForegroundColor DarkGray

            $processes += Start-Process -PassThru -NoNewWindow -FilePath 'cmd.exe' `
                -ArgumentList '/c', 'npx ng build --watch --configuration development' `
                -WorkingDirectory $webRoot

            $deadline = (Get-Date).AddMinutes(5)
            while (-not (Test-Path "$wwwroot\index.html")) {
                if ($processes | Where-Object { $_.HasExited }) { throw 'The web app build failed. See the output above.' }
                if ((Get-Date) -gt $deadline) { throw 'The web app did not finish building within 5 minutes.' }
                Start-Sleep -Milliseconds 500
            }
        }
        else {
            Write-Host ''
            Write-Host '  Building the web app...' -ForegroundColor DarkGray

            # The globally installed Angular CLI is v14 and too old for this project, so the copy
            # in web/node_modules is used instead. cmd.exe is the launcher because npx is a shell
            # script.
            $build = Start-Process -PassThru -Wait -NoNewWindow -FilePath 'cmd.exe' `
                -ArgumentList '/c', 'npx ng build' `
                -WorkingDirectory $webRoot

            if ($build.ExitCode -ne 0) { throw "The web app build failed (exit $($build.ExitCode))." }
        }
    }

    # ------------------------------------------------------------ go

    Write-Host ''
    Write-Host '  Filipino Mahjong' -ForegroundColor Green
    Write-Host '  ----------------' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  Share this with the other three players:' -ForegroundColor DarkGray
    Write-Host "  http://${Address}:${Port}" -ForegroundColor Yellow
    Write-Host ''

    # When the game is running on wifi but somebody is playing from elsewhere, the LAN link above
    # is no use to them and the tailnet one is. Both are printed rather than making the player who
    # is out of the house know to ask for a flag.
    $tailnet = Get-TailnetAddress
    if ($tailnet -and -not (Test-TailnetAddress $Address)) {
        Write-Host '  Playing from somewhere else? Use this instead, if they are on your tailnet:' -ForegroundColor DarkGray
        Write-Host "  http://${tailnet}:${Port}" -ForegroundColor Yellow
        Write-Host ''
    }

    if (Test-TailnetAddress $Address) {
        # Over a tailnet none of the wifi advice applies: the other players can be anywhere, and
        # the traffic arrives on the Tailscale interface, which the Windows firewall rules the
        # open-firewall script writes have nothing to do with.
        Write-Host '  This is your Tailscale address, so they can be anywhere - but they do have' -ForegroundColor DarkGray
        Write-Host '  to be signed in to your tailnet. Run .\run.ps1 with no arguments on the same' -ForegroundColor DarkGray
        Write-Host '  wifi to get a plain LAN link instead.' -ForegroundColor DarkGray
    }
    else {
        Write-Host '  They need to be on the same wifi. If nothing loads on their phone, run' -ForegroundColor DarkGray
        Write-Host '  tools\open-firewall.ps1 once, as administrator.' -ForegroundColor DarkGray
    }
    Write-Host ''

    # -NoNewWindow matters for more than tidiness. Without it the child gets no console, so its
    # output goes nowhere and, worse, anything that reads stdin blocks forever with nothing on
    # screen to say why. Sharing this console means the logs appear inline and a prompt is
    # answerable.
    $processes += Start-Process -PassThru -NoNewWindow -FilePath 'dotnet' `
        -ArgumentList 'run', '--project', "$root\server\src\Mahjong.Api", '--urls', "http://0.0.0.0:$Port" `
        -WorkingDirectory "$root\server"

    Write-Host '  Running. Ctrl+C stops everything.' -ForegroundColor DarkGray
    Write-Host ''

    while ($processes | Where-Object { -not $_.HasExited }) {
        Start-Sleep -Milliseconds 500

        # If the server dies on its own, stop the watcher too rather than leaving it rebuilding
        # into a wwwroot nothing is serving.
        if ($processes | Where-Object { $_.HasExited }) { break }
    }
}
finally {
    Write-Host ''
    Write-Host '  Stopping...' -ForegroundColor DarkGray

    foreach ($process in $processes) { Stop-Tree -Process $process }

    # Belt and braces: if anything is somehow still holding the port, say so rather than leaving
    # the next run to fail with a confusing bind error.
    $left = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($left) {
        Write-Host "  warning: something is still listening on $Port (pid $($left.OwningProcess -join ', '))" -ForegroundColor Yellow
    }

    Write-Host '  Stopped.' -ForegroundColor DarkGray
    Write-Host ''
}
