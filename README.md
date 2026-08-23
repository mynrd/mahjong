# Filipino Mahjong

Four-player Filipino Mahjong over the local network. No accounts: one player makes a table, shares
a link, and the other three type the table password to sit down.

- [PLAN.md](./PLAN.md) - architecture and build order
- [RULES.md](./RULES.md) - the ruleset the engine implements, and where published sources disagree

---

## Running it

Everything runs on one Windows machine. SQL Server must be running on the default instance (`.`);
the database `MahjongDb` is created automatically on first start.

```powershell
# once, as administrator, so phones on the same wifi can connect
.\tools\open-firewall.ps1

# every time
.\run.ps1
```

`run.ps1` builds the web app, starts the server and prints the address to share. It works out
which network adapter to advertise, which matters on a machine with VPN and virtual adapters
alongside the real card, and it checks the things that otherwise fail confusingly first: SQL
Server not running, a port still held by a previous run, or `npm install` never having been run.

Ctrl+C stops it, including the child processes `dotnet run` and `npx` spawn - killing only the
launcher leaves the real process still holding the port.

One process serves everything on port 5080: the page, the REST endpoints and the websocket.
`ng build` writes the page into `server/src/Mahjong.Api/wwwroot` (see `web/angular.json`) and
Kestrel serves it from there, so there is one port to open, one link to hand out and one thing to
deploy. The web app calls the API on the origin it was opened from, so nothing needs configuring
per device.

While working on the page, `-Watch` rebuilds it on every save. The browser does not reload itself;
refresh the tab once the rebuild is logged. `-SkipWebBuild` starts against whatever is already in
`wwwroot`, for restarts where only the server changed.

```powershell
.\run.ps1 -Watch
.\run.ps1 -SkipWebBuild
```

By hand, without the script:

```powershell
cd web; npx ng build
dotnet run --project server\src\Mahjong.Api --urls http://0.0.0.0:5080
```

To deploy, `dotnet publish` builds the web app into the output as part of the same command:

```powershell
dotnet publish server\src\Mahjong.Api -c Release -o publish
.\publish\Mahjong.Api.exe --urls http://0.0.0.0:5080
```

---

## Playing

1. The host opens the printed address, names the table, sets a password and puts in their name.
2. The lobby shows an invite link and a QR code. The other three open it on their own phone or
   laptop, on the same wifi, and type the password.
3. Empty seats can be filled with bots, so a table of two or three still works.
4. The host deals. From then on the game runs itself.

Tap a tile to lift it, tap again to throw it. When somebody else throws a tile you can use, a bar
appears with what you may call and a countdown. Refreshing, closing the tab or losing wifi does not
cost the seat: the same browser comes back to the same hand holding the same tiles.

---

## Layout

```
server/
  src/Mahjong.Domain/          the ruleset. No dependencies at all, so it is testable on its own
  src/Mahjong.Infrastructure/  EF Core entities, migrations, password and token handling
  src/Mahjong.Api/             REST for setup, SignalR for play, bots, the game clock
  tests/                       109 tests over the rules, scoring and the redaction guarantee
web/                           Angular 22, standalone components and signals
  public/tiles/                42 tile faces plus the back
e2e/                           Playwright specs and screenshots
run.ps1                        builds the page, starts the server, prints the link to share
tools/                         firewall rules
```

---

## Tests

```powershell
dotnet test server                    # 109 tests: rules, scoring, snapshots, redaction
cd e2e; npx playwright test           # 16 specs across desktop, tablet and phone viewports
cd e2e; node smoke/play-hand.mjs      # plays six whole hands against the API, no browser
cd e2e; node smoke/tile-sheet.mjs     # renders every tile face to screenshots/tiles.png
```

The Playwright suite needs the server running (`.\run.ps1`) and goes against the real API and the
real database. Point it at a network address to test as a phone would:

```powershell
cd e2e
$env:WEB_URL = 'http://192.168.254.100:5080'; npx playwright test
```

### The one test worth knowing about

`server/tests/Mahjong.Api.Tests/RedactionTests.cs` takes every tile id in a serialised game view
and requires it to be one that player is entitled to see. The server holds every hand and the exact
order of the wall; if any of that ever reaches a client, the game is cheatable by opening devtools.
That test is what stops a convenience property or a reused DTO quietly making it so.

---

## Choices worth knowing

**Only 108 of the 144 tiles are playable.** In Filipino Mahjong the winds and dragons are bonus
tiles, like the flowers and seasons: exposed when drawn, replaced, never part of a hand. Hands are
16 tiles, 17 to win. This is the biggest difference from Riichi or Hong Kong mahjong and it is why
the win detection only ever deals with three numbered suits.

**House rules are data, not code.** Published Filipino rules disagree on nearly every money value
and on several mechanics. The disagreements are listed in RULES.md section 7 and each one is a flag
on the room, stored as JSON. Changing what a table pays needs no code change.

**One wall, two pointers.** Turn draws come off the front, replacement draws off the tail, and the
hand is drawn when they meet. A fixed-size reserve does not work: there are 36 bonus tiles, every
one needs replacing, and a replacement can itself be a bonus tile.

**Moves are serialised per table.** Two players can tap Pung in the same millisecond. Every change
to a hand goes through one gate per room, so claims resolve in a defined order instead of corrupting
the hand.

**Both a log and a snapshot.** Every action is appended to `GameActions`, and the whole state is
snapshotted on `Games.StateJson` after each one. The log makes a disputed hand reconstructible; the
snapshot makes a reconnect a single read.

---

## Tile artwork

The 34 suit, wind and dragon faces come from
[FluffyStuff/riichi-mahjong-tiles](https://github.com/FluffyStuff/riichi-mahjong-tiles), public
domain (CC0). A riichi set has no flowers or seasons, so those eight were drawn for this project;
so was the white dragon, whose original is a blank face that reads on screen as a tile that failed
to load. See `web/public/tiles/ATTRIBUTION.txt`.

---

## Not done

- No spectator mode.
- Finished rooms are never cleaned up.
- Money is a plain unit count. No currency, no rounding rules.
- The bots are deliberately weak: they take a win if they see one and otherwise throw their least
  useful tile. They never claim discards.
