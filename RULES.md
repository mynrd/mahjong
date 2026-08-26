# Filipino Mahjong - Ruleset the engine implements

Terminology is defined inline on first use. Values marked **(configurable)** live in the room's
`RulesJson` column and can be changed per room, because Filipino house rules differ on money.

Sources cross-checked (they agree on structure, disagree on peso amounts):
- https://mahjongpros.com/blogs/mahjong-rules-and-scoring-tables/official-filipino-mahjong-rules
- https://filipinomj.com/how-to-play.html
- http://gingsbrain.blogspot.com/2010/10/how-to-play-filipino-mahjong.html
- https://mahjlife.com/wiki/filipino-mahjong-article-213/

---

## 1. Tiles

144 tiles total, split into two groups that behave completely differently.

### 1.1 Playable tiles (108)

Only these can ever sit in a hand.

| Suit | Ranks | Copies | Count |
|---|---|---|---|
| Bulaklak / Balls (dots) | 1-9 | 4 | 36 |
| Kahoy / Sticks (bamboo) | 1-9 | 4 | 36 |
| Letra / Characters | 1-9 | 4 | 36 |

### 1.2 Bonus tiles (36) - "flowers"

In Filipino Mahjong the winds and dragons are **not** playable. They are lumped in with the
flowers and seasons as bonus tiles. A player who draws one exposes it immediately and draws a
replacement from the flower wall. They are never discarded and never form sets.

| Group | Tiles | Copies | Count |
|---|---|---|---|
| Winds | East, South, West, North | 4 | 16 |
| Dragons | Red, Green, White | 4 | 12 |
| Flowers | Plum, Orchid, Chrysanthemum, Bamboo | 1 | 4 |
| Seasons | Spring, Summer, Autumn, Winter | 1 | 4 |

This is the single biggest difference from Riichi and Hong Kong mahjong, and it is why the
win-detection code only ever deals with three numbered suits.

### 1.3 Joker **(configurable, default: on)**

At the start of each hand one playable tile is picked at random and named the joker. All 4
copies of that tile are wild and may stand in for any playable tile. A joker cannot be used as
the tile you win on when the win is claimed off a discard **(configurable)**.

---

## 2. Seats, deal, and walls

- 4 seats, fixed order, play proceeds counter-clockwise (seat 0 -> 1 -> 2 -> 3).
- Seat 0 of the first hand is the **mano** (dealer).
- All 144 tiles are shuffled into one wall.
- Each player is dealt **16 tiles**. The mano is dealt **17** and discards first.
- The remaining 79 tiles stay as one wall with two pointers:
  - a **front pointer**, which normal turn draws move forward,
  - a **back pointer**, which replacement draws (after a bonus tile or any kang) move backward
    from the tail.

  A fixed-size flower wall does not work here: there are 36 bonus tiles, every one of them has
  to be replaced when drawn, and a replacement can itself be a bonus tile, so a small reserve is
  exhausted routinely. Two pointers into one wall is both closer to how it is played and
  self-limiting - when the pointers meet, the wall is spent.
- After the deal, players expose dealt bonus tiles and take replacements, mano first, then
  seats 1, 2, 3, repeating until nobody holds a bonus tile.
- A hand is **drawn (no winner)** when the two pointers meet and the current player cannot win.
  Nobody pays anything.

A hand held by a player is always 16 tiles between turns, 17 at the moment of decision.

---

## 3. Turn flow

1. Current player draws 1 tile from the draw wall.
   - If it is a bonus tile: expose it, draw a replacement from the flower wall, repeat.
2. Current player may declare **todas** (win), **secret kang**, or **sagasa**.
3. Otherwise they discard 1 tile.
4. The discard is open for claims. **Every** discard is, including one nobody can use: the window
   opening is how the tile is put in front of the other three, so a window that only opened when
   somebody could claim would be announcing that somebody could.
5. Every other player answers it - claim, or Pass. The tile does not move until all three have
   answered, until a call already made on it takes it, or until the next seat draws.
6. Then the turn passes to the next seat, who takes a tile from the wall.

Nobody is ever timed for answering a discard. Step 5 also changes at a table with **Allow Helper**
off. See sections 3.1 to 3.3.

### 3.1 Who draws, and when

A player's own draw is a button press, never automatic. There is only ever one thing a player can
do at the start of their turn, so the tile used to arrive by itself - and it arrived in a sorted
hand while they were still looking at what the last player threw, which meant the one tile in the
game they are owed a good look at went past unseen. **Draw** sits on the action bar all game, live
only when the wall is theirs to take from.

Bots draw by themselves. Nobody is watching a bot's hand.

### 3.2 The claim window has no clock

**Nobody is timed for answering a discard.** Whoever threw it, whatever the helper is set to, a
thrown tile stays claimable until it is claimed or the next seat draws. Closing the dialog, looking
away, or looking at your own hand for a minute costs nothing.

This is the one rule here that is not the real game's. A six-second window used to run from the
throw, and a player who glanced away was out of the discard with nothing on screen to say why and
nothing but **Draw** left to press. At a table you can still call the tile as long as it is lying
there, so that is what the app does now.

**The tile in the pool is the way back in.** The last thrown tile is drawn with a halo for every
seat that has not answered, and pressing it opens the calls again. The halo says only that the tile
is unanswered - it is the same halo on every screen, and says nothing about what any hand holds.

Three things end a window, and none of them is a clock on answering:

| What ends it | When |
|---|---|
| Every other seat answers | Claim or Pass from all three |
| A **call already made** takes the tile | **6s (configurable)** after that call - see 3.2.1 |
| The **next seat draws** | Whenever that seat decides to pick up |

A bot sitting in the next seat waits **20 seconds (configurable)** from the discard and then draws.
Since nothing else paces a window nobody answers, that is the number that decides how long a table
with a bot in it sits on a thrown tile.

Seats holding nothing are not answered for. A bot is - there is no screen to show it the tile on -
but a person always answers for themselves, whatever they are holding. A window that closed by
itself the instant it opened would be the server saying "nothing here for you", which is the one
sentence 3.3 exists to stop it saying.

#### 3.2.1 Calling holds the tile

Calling is the one thing that does start a clock, and it starts it for the rest of the table rather
than for the caller.

- The first seat to make a **finished** call - pressed and paid for - holds the tile. The other
  three are told who holds it and what they called, the way a call at a table is shouted rather
  than whispered.
- They then have **6 seconds (configurable)** to call something that **beats** it (4.1). Nothing at
  or under it is worth pressing and the app says so on the buttons.
- When those seconds run out the tile goes to whoever is standing highest. Seats that never
  answered are read as having let it go: they heard the call and said nothing over it.
- The seat holding it can **let it go** at any point before then. The tile is back on the table,
  nothing is timing it, and every seat that was answered for on account of that call is waiting on
  the tile again. Without this a mis-tap owns the tile until it wins.
- A **half-made** call - pressed with the tiles not yet named, which only happens with the helper
  off - starts nothing and outlives everything. Nobody can take the tile while somebody is still
  counting their own tiles against it, including a finished call whose 6 seconds have run out. Only
  the seat that pressed can end it, by naming the tiles or letting the call go.

### 3.3 Allow Helper **(configurable, default: on)**

Chosen when the table is created and fixed for every hand played at it.

**On.** The server works out what each seat could do with a discard and says so: the claim window
arrives with the exact groups you could build, your own tiles are outlined by what they could be
used for, and Auto Arrange lays your hand out in blocks.

**Off.** The server says none of that. All three seats get the same buttons on every discard,
whether or not they can use it, and nobody is shown a grouping. Reading your own hand is the game.

The one thing the table still leaves out is **Chow**, and only where the rules could never allow one
whatever the hand held: a wind or a dragon, or a tile thrown by anyone other than the player
immediately before you (4.1). Both of those are on the table for all four players to see, so hiding
the button says nothing about anybody's tiles - and a chow that completes a hand is unaffected,
because that is claimed as **Todas**, which is always offered.

Because nobody is told what a discard is worth, a claim here is made in two acts:

- Pressing a button is only half a claim. The other half is tapping the tiles it costs.
- **Neither act is timed.** The press holds the tile, and the seat that made it counts its own
  tiles against the discard for as long as that takes. Pressing Pung and then finding nothing to
  pay with is an ordinary mistake at a table where nothing was spelled out, and it must not cost
  the discard.
- A press can be **switched** to a different call, or **taken back** outright. Taking it back puts
  the seat exactly where it was before pressing - free to call something else, to pass, or to draw
  - and hands the tile straight to anything ranked underneath.
- The next seat **cannot draw through** a press somebody is still paying for. Nothing bounds that
  wait, which is the trade this setting makes: the table waits for the person, not the clock.

Priority is unchanged and does not care who pressed first: a pung declared forty seconds late still
beats a chow that was completed at second nine. What being first does buy is the hold in 3.2.1 -
once a call is *finished*, the rest of the table has one beat to answer it. See section 4.1.

Bots claim the same way at both kinds of table: they work out the group and name its tiles in one
move, so there is no press for them to leave half finished. What is different is the waiting - see
the 20 seconds in 3.2. A bot does not draw through a call somebody is still paying for.

A human who walks away can still stall a table indefinitely, if the seat due to play next is
theirs. That is accepted: a human who never discards already stalls any table, and the game has no
turn clock.

---

## 4. Calls

| Call | Meaning (plain English) | Who can claim | Notes |
|---|---|---|---|
| **Chow** | 3 tiles in a run, same suit, e.g. 4-5-6 sticks | Only the player immediately **after** the discarder (i.e. you may only chow from your left-hand neighbour) | Exception: allowed from anyone if it completes a win |
| **Pung** | 3 identical tiles | Any player, out of turn | Skips the players in between |
| **Kang** | 4 identical tiles, completed off a discard | Any player who already holds the other 3 | Draw a replacement from the flower wall. Pays immediately |
| **Secret kang** | 4 identical tiles all drawn yourself | On your own turn | Kept face down. Pays immediately, more than an open kang |
| **Sagasa** | You draw the 4th tile matching a pung you already exposed | On your own turn | Extends the pung to a kang. Draw a replacement. Pays immediately |
| **Todas** | Declare a completed hand | Any player, out of turn | See section 5 |
| **Bunot** | Todas where the winning tile came from your own draw, not a discard | - | Doubles the payout |

### 4.1 Claim priority when two players claim the same discard

`Todas` > `Kang` = `Pung` > `Chow`. If two players both declare todas, the one nearer in turn
order after the discarder wins. **(configurable)**

---

## 5. Winning hands

A winning hand is 17 tiles.

| Shape | Composition |
|---|---|
| **Todas** (standard) | 5 bahay (sets: chow, pung, or kang) + 1 pair |
| **Siete pares** | 7 pairs + 1 bahay |

A kang counts as one bahay even though it is 4 tiles; the extra tile is offset by the
replacement draw, so the arithmetic stays at 17.

---

## 6. Scoring

Two separate money flows.

### 6.1 Ambitions - paid immediately, mid-hand, by all 3 other players

| Ambition | Trigger | Default units |
|---|---|---|
| `NoFlowers` | Dealt zero bonus tiles at the start of the hand | 1 |
| `ThirteenFlowers` | You accumulate 13 bonus tiles during the hand | 1 |
| `Kang` | Open kang declared | 1 |
| `SecretKang` | Concealed kang declared | 2 |
| `Sagasa` | Pung extended to kang by own draw | 2 |

### 6.2 Win scoring - paid at the end of the hand

Base **(configurable)**: `Todas = 2 units`.

Bonuses stack on top of the base:

| Bonus | Meaning | Default units |
|---|---|---|
| `Escalera` | A full 1-9 run in one suit (three chows: 123, 456, 789) | +4 |
| `SietePares` | Won with the 7 pairs + 1 set shape | +4 |
| `AllUp` (concealed) | Nothing was ever exposed | +1 |
| `AllDown` | Everything except the pair was exposed | +1 |
| `Flush` | Entire hand is a single suit | +2 |
| `AllPungs` | All 5 bahay are pungs or kangs | +2 |
| `AllChows` | All 5 bahay are chows | +1 |
| `QuickWin` | Won on or before the 5th discard of the hand | +1 |
| `Single` | Waiting on exactly one tile | +1 |
| `Paningit` | Waiting on the middle tile of a run, e.g. holding 4 and 6 | +1 |
| `BackToBack` | Waiting on two pairs, either one completes the win | +1 |
| `Bisaklat` | The mano's dealt hand is already complete before any discard | instant win, +20 |

Multipliers:
- `Bunot` (self-drawn win): the whole total is doubled.

Payment:
- **Won off a discard:** the discarder pays double the total, the other two pay the total each.
- **Bunot:** all three pay double the total each.

All of the above is stored as a JSON scoring profile on the room, so a table can change values
without a code change.

---

## 7. Known conflicts between sources

Recorded so they are not mistaken for bugs. Each is a config flag, defaulted to the first option.

1. **Claim priority** - mahjongpros puts Pung/Kang above Todas; the other two sources put
   Todas first. Default: Todas first.
2. **Siete pares shape** - "7 pairs + 1 set" (17 tiles) is used here. One source loosely says
   "seven pairs". Default: 7 pairs + 1 set.
3. **Joker** - not universal. Default: on, and it cannot be the tile you win on off a discard.
4. **Chow source** - one source says "any discard", two say "from your left only".
   Default: left only.
5. **Money values** - all three sources differ. Defaults above use "units", not pesos, and the
   room owner sets the unit value.
6. **Allow Helper** - not a rules conflict but a table setting, since a physical table has no
   helper at all. Default: on, because a first-time player cannot read a hand yet. See section 3.3.
