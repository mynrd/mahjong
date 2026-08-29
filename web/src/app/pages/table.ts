import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Game } from '../core/game';
import {
  BONUS_LABELS,
  ClaimCandidateView,
  ClaimKind,
  ClaimPromptView,
  DiscardView,
  HandGroupKind,
  MeldView,
  PlayerGameView,
  SeatCallView,
  SeatStateView,
  TileView,
} from '../core/models';
import { Session } from '../core/session';
import { HintKind, Tile, describe } from '../ui/tile';

/** Suit order used when a hand is laid out, so tiles stay where the player expects them. */
const SUIT_ORDER: Record<string, number> = { D: 0, B: 1, C: 2, W: 3, R: 4, F: 5, S: 6 };

/** Highest first, matching the order the server resolves a contested discard in. */
const CLAIM_RANK: Record<ClaimKind, number> = { Todas: 3, Kang: 2, Pung: 1, Chow: 0 };

/** The letter in the corner of a hinted tile, so the outline colour is never the only signal. */
const CLAIM_BADGE: Record<ClaimKind, string> = { Todas: '!', Kang: 'K', Pung: 'P', Chow: 'C' };

const CLAIM_WORD: Record<ClaimKind, string> = {
  Todas: 'win',
  Kang: 'kang',
  Pung: 'pung',
  Chow: 'chow',
};

const GROUP_WORD: Record<HandGroupKind, string> = {
  Kang: 'kang',
  Pung: 'pung',
  Chow: 'chow',
  Pair: 'pair',
  Partial: 'needs',
  Floater: 'spare',
};

/**
 * One run of tiles drawn together. `all` is the single block that holds a hand nobody has grouped;
 * `manual` is a group the player pushed together themselves.
 */
export interface HandBlock {
  key: string;
  kind: HandGroupKind | 'all' | 'manual';
  tiles: TileView[];
  /** Shown under the block. Empty for the one-block layout. */
  label: string;
  ariaLabel: string;
}

/**
 * One call offered on the action bar while a discard is open.
 *
 * The dialog draws the tiles a call would take; the bar cannot, so it carries the word and the
 * count instead. `options` is how many distinct ways the hand could make that call, which is what
 * decides whether pressing it takes the tile outright or opens the dialog to choose between them.
 * Zero at an unassisted table, where nothing has been read for this player at all.
 */
export interface BarCall {
  kind: ClaimKind;
  options: number;
  live: boolean;
  testId: string;
}

/**
 * The gap a dragged tile would drop into: a tile already in the hand, and which side of it. A slot
 * rather than a target tile, because a hand is arranged by putting a tile in a particular place -
 * "third in that run", not just "somewhere in that group".
 */
export interface DropSlot {
  id: number;
  /** True for the gap on the leading side of that tile, false for the one after it. */
  before: boolean;
}

/** One tile of the set a claim would build, drawn in the dialog so the shape is not just letters. */
export interface ComboTile {
  code: string;
  /** The discard itself, outlined so it is clear which tile is being taken. */
  thrown: boolean;
}

/**
 * A call the server refused, said in a way the table can draw: the sentence, and under it the
 * tiles from your own hand the call would have taken. More than one row means "or", which is what
 * a chow needs - a suited tile sits in up to three different runs.
 */
export interface ClaimRefusal {
  text: string;
  need: string[][];
}

/** One seat's line in the offer of another game. */
interface AgreementRow {
  seat: number;
  name: string | null;
  wind: string;
  isBot: boolean;
  isYou: boolean;
  empty: boolean;
  accepted: boolean;
}

/** A declaration you can make on your own turn, other than todas. */
export interface TurnMove {
  kind: 'SecretKang' | 'Sagasa';
  face: string;
  label: string;
  /** The four tiles the declaration puts down, so the dialog shows the set rather than a word. */
  tiles: string[];
  testId: string;
}

/** Where the local layout preferences are kept. Not game state, so they live with the browser. */
const ARRANGE_KEY = 'mj.arrange';
const HAND_OPEN_KEY = 'mj.handOpen';

/** How long a thrown tile flies into the pool, and how long a freshly drawn tile stays marked. */
const LANDING_MS = 380;
const ARRIVAL_MS = 420;

/**
 * How many thrown tiles it takes before the pool draws itself small.
 *
 * Chosen off the shape of the box rather than the shape of a hand: past about this many, the pile
 * runs to a fourth row on a wide screen, and the pool's height is whatever the rest of the table
 * did not want - on a short screen that fourth row is what starts the scrolling.
 */
const DENSE_DISCARDS = 40;

/** How far a pointer travels before a press on a tile counts as a drag rather than a tap. */
const DRAG_THRESHOLD_PX = 8;

/** Quiet period after the last regroup before the arrangement is written back to the server. */
const SAVE_DEBOUNCE_MS = 600;

@Component({
  selector: 'mj-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Tile, RouterLink],
  templateUrl: './table.html',
  styleUrl: './table.css',
  // Every tile is an image and every image on a phone is one long press away from "Save image" or
  // a preview that covers the table - and on a desktop, a right-click over a tile you meant to
  // pick up. Nothing on this page has a menu worth having, so the whole table refuses one.
  host: { '(contextmenu)': '$event.preventDefault()' },
})
export class TablePage implements OnDestroy {
  readonly code = input.required<string>();

  private readonly game = inject(Game);
  private readonly session = inject(Session);
  private readonly router = inject(Router);

  protected readonly view = this.game.view;
  protected readonly connection = this.game.connection;
  protected readonly lastError = this.game.lastError;
  protected readonly messages = this.game.messages;

  /**
   * Why this browser no longer holds a seat, once it no longer holds one. Set by the server rather
   * than guessed at from a failure: leaving and being removed both take the seat away, and the
   * player is owed which of the two happened to them.
   */
  protected readonly removedReason = this.game.removed;

  /**
   * Why the table is over, once the host has ended it. Separate from being removed: a freed seat
   * can be sat down in again and a closed table cannot, so the two screens offer different ways
   * out of the same dead end.
   */
  protected readonly closedReason = this.game.closed;

  /** The tile lifted out of the hand. A second tap on the same tile offers it up to be thrown. */
  protected readonly selected = signal<number | null>(null);

  /**
   * The tile a second tap has put up for confirmation, if any.
   *
   * The two taps that throw a tile are the same gesture twice in the same place, which is exactly
   * the shape of an accident - and a thrown tile is gone: the other three can claim it, and there
   * is no rule that gives it back. So the tap no longer throws anything. It asks.
   */
  private readonly confirming = signal<number | null>(null);

  /**
   * Tiles tapped to build a claim with, and the discard they were picked against. Storing the
   * discard alongside them is what makes the picks clear themselves: the moment a different tile
   * is up for claim, a pick made against the old one stops counting.
   */
  private readonly picks = signal<{ discard: number | null; ids: number[] }>({
    discard: null,
    ids: [],
  });

  /** Ticks once a second purely so the claim countdown re-renders. */
  private readonly now = signal(Date.now());
  private readonly clock = setInterval(() => this.now.set(Date.now()), 250);

  protected readonly showScores = signal(false);

  /** The host's own sheet: who is at the table, and whether the table goes on. */
  protected readonly showHost = signal(false);

  /**
   * Whether the result sheet has been put away for this hand.
   *
   * A finished hand is worth looking at: whose sets were down, what was left in the pool, which
   * tile the winner was waiting on. The sheet covers all of it, so it closes - and reopens from the
   * action bar, because a result nobody can get back to is a result nobody dares close. Reset on
   * every deal, so the next hand's sheet still comes up by itself.
   */
  private readonly outcomeDismissed = signal(false);

  protected readonly outcomeOpen = computed(
    () => !!this.view()?.outcome && !this.outcomeDismissed(),
  );

  /** The hand is finished. What the table is for now is looking at, not playing. */
  protected readonly handOver = computed(() => this.view()?.phase === 'HandOver');

  /** Your own hand is face up on the table. One way, and only for the hand just finished. */
  protected readonly iRevealed = computed(() => !!this.me()?.revealed);

  // ---------------------------------------------------------------- the next game

  /** The standing offer of another game, or null when nobody has called one. */
  protected readonly newGame = computed(() => this.view()?.newGame ?? null);

  /**
   * Whether this browser holds the seat that made the table. Read off the view rather than off the
   * stored session, so what the screen offers and what the server will accept cannot disagree.
   */
  protected readonly isHost = computed(() => {
    const view = this.view();
    return !!view && view.hostSeat !== null && view.hostSeat === view.yourSeat;
  });

  protected readonly iAgreed = computed(() => {
    const view = this.view();
    return !!view && !!view.newGame?.accepted.includes(view.yourSeat);
  });

  /**
   * Every seat as the offer sees it: who is sitting there, whether they have said yes, and whether
   * the chair is empty. One row per seat rather than a list of names, because an empty chair is
   * exactly as much of a reason the table has not dealt as somebody who has not answered.
   */
  protected readonly agreement = computed<AgreementRow[]>(() => {
    const view = this.view();
    if (!view) return [];

    // Built whether or not anybody has called a game. The rows are the table itself - who is
    // sitting where - and the host needs them between hands to free a chair, which is a thing to
    // do before calling the next game rather than only after.
    const offer = view.newGame;

    return view.seats.map((seat) => ({
      seat: seat.seat,
      name: seat.displayName,
      wind: this.windOf(seat.seat),
      isBot: seat.isBot,
      isYou: seat.seat === view.yourSeat,
      // Falsy rather than a null check. A seat nobody is sitting in has no name at all, and the
      // difference between null and absent is not one the table should have an opinion about.
      empty: !seat.displayName,
      accepted: !!offer?.accepted.includes(seat.seat),
    }));
  });

  /**
   * Whether the host may free a given chair right now.
   *
   * Only between hands: mid-hand that seat is holding tiles the rules are still counting, and the
   * server refuses it for the same reason. Never the host's own chair, and never one nobody is
   * sitting in. A bot counts - filling four seats with bots and then wanting one of them back out
   * is exactly the case this is for.
   */
  protected canRemove(row: AgreementRow): boolean {
    return this.isHost() && this.handOver() && !row.empty && row.seat !== this.view()?.hostSeat;
  }

  protected readonly emptySeats = computed(
    () => this.agreement().filter((row) => row.empty).length,
  );

  /** The link to hand to somebody to fill a seat that has been left empty. */
  protected readonly inviteUrl = computed(() => `${window.location.origin}/join/${this.code()}`);

  protected readonly copied = signal(false);

  /**
   * The seat whose sheet is open, or null. An opponent card is about 110px wide on a phone, which
   * is not enough to read a kang of characters off; tapping one blows that player's exposed tiles
   * up to hand size.
   */
  protected readonly zoomSeat = signal<number | null>(null);

  /**
   * Seats whose row of face-down tiles is being drawn, by seat number.
   *
   * Empty by default, which is the whole point. Sixteen backs at 15px wrap onto three or four rows
   * inside a card about a third of a phone wide, so three opponents were spending well over a
   * hundred pixels of a 700px screen saying something a two-digit count says exactly as well - and
   * that height came out of the bottom of the page, where the action bar lives. The eye on each
   * card puts them back for anyone who wants to look at the shape of a hand.
   */
  private readonly openBacks = signal<ReadonlySet<number>>(new Set());

  /**
   * Layout mode for your own hand. Sticky, because the one-shot alternative goes stale the moment
   * a tile is drawn and a stale grouping is worse than none.
   */
  private readonly arrangePreference = signal(readFlag(ARRANGE_KEY, false));

  /**
   * Whether the hand is actually being laid out for you. An unassisted table does not send the
   * grouping at all, so the preference is kept but ignored there rather than cleared: turning the
   * setting back on at the next table should not have cost you the toggle you had set.
   */
  protected readonly arranged = computed(() => this.assisted() && this.arrangePreference());

  /**
   * Whether your own box is lifted over the table to show every tile at once. Down by default: the
   * lifted box covers the discard pool, which is not where a hand should sit while you are only
   * watching the other three play.
   */
  protected readonly handOpen = signal(readFlag(HAND_OPEN_KEY, false));

  // ---------------------------------------------------------------- arranging by hand
  //
  // Groups the player pushed together themselves, as tile ids. Anything in the hand that is not
  // listed here is loose and sits in the last block. Deliberately unlabelled: an Auto Arrange
  // block says "pung" because the server worked that out under the same rules it scores by, and a
  // second opinion computed here would drift from it.

  private readonly manualGroups = signal<readonly (readonly number[])[]>([]);

  /** The tile picked up by a tap, waiting for a second tap to group it with. */
  protected readonly held = signal<number | null>(null);

  /**
   * The discard whose dialog the player closed. Kept as an id rather than a plain flag so the
   * dialog opens again by itself on the next discard instead of staying shut for the rest of the
   * hand, and does not spring back the moment it is dismissed.
   */
  private readonly dismissedClaim = signal<number | null>(null);

  /** The secret kang / sagasa sheet, opened from the action bar. */
  protected readonly showMoves = signal(false);

  /**
   * Whether a tap on one of your own tiles blows it up instead of playing it.
   *
   * A mode rather than a gesture, because every other reading of a tap is already spoken for: the
   * first tap lifts a tile to throw, a second throws it, and with Auto Arrange off a tap is how
   * groups are built. A tile big enough to read is worth having on a phone, but not at the price of
   * making any of those three ambiguous - so it is a switch you can see the state of, sitting next
   * to Sort where the other hand controls are.
   */
  protected readonly zoomTiles = signal(false);

  /** The tile blown up on screen, or null. Held as an id so a tile that leaves takes the sheet. */
  protected readonly zoomedId = signal<number | null>(null);

  /**
   * Whether the discard up for claim has been tapped to blow it up.
   *
   * A flag rather than a tile, because the discard is not in your hand and `zoomedTile` below
   * resolves out of `me().concealed` - it cannot find this one. Reading the face back off the live
   * claim also means a window that closes under the sheet takes the sheet with it.
   */
  protected readonly claimZoom = signal(false);

  /** The tile being dragged, what it is over, and where to draw the ghost. */
  protected readonly dragged = signal<number | null>(null);
  protected readonly dropTarget = signal<DropSlot | null>(null);
  protected readonly overPool = signal(false);
  protected readonly ghost = signal<{ x: number; y: number } | null>(null);

  private press: { x: number; y: number; id: number; pointerId: number; el: HTMLElement } | null =
    null;

  /** Set when a drag ends, so the click the browser fires afterwards does not also throw a tile. */
  private swallowClick = false;

  private saveTimer: ReturnType<typeof setTimeout> | undefined;

  // ---------------------------------------------------------------- what just happened
  //
  // The server pushes whole PlayerGameView snapshots and never says "seat 2 threw a tile", so
  // everything that needs to be animated is worked out by diffing one snapshot against the last.
  // All of it lives in the single effect below, so there is one place that knows what changed.

  /** The discard now flying into the pool, and which side of the table it came from. */
  protected readonly landing = signal<{ id: number; from: number } | null>(null);

  /** Tiles that were not in your hand a moment ago: a draw, or the spoils of a claim. */
  protected readonly arriving = signal<ReadonlySet<number>>(new Set());

  /** The tile you have just thrown, still on screen until the server snapshot removes it. */
  protected readonly throwing = signal<number | null>(null);

  private lastDiscardId: number | null = null;
  private myTileIds: ReadonlySet<number> = new Set();
  private lastHand = -1;
  private offerStanding = false;
  private landingTimer: ReturnType<typeof setTimeout> | undefined;
  private arrivalTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    queueMicrotask(() => this.begin());
    effect(() => this.noticeChanges(this.view()));

    // The seat is gone: hang up, and forget the token, which the server has already stopped
    // honouring. The page stays put and says so rather than bouncing somewhere - being dropped onto
    // the home screen with no explanation is how a player concludes the app crashed.
    effect(() => {
      if (!this.removedReason()) return;

      untracked(() => {
        void this.game.disconnect();
        this.session.clear();
      });
    });

    // The table is over. Hang up, because nothing more is coming down that connection - but keep
    // the token: unlike a freed seat, the seat is still this browser's, and the room and its
    // finished hands are still there to look back at.
    effect(() => {
      if (!this.closedReason()) return;
      untracked(() => void this.game.disconnect());
    });
  }

  ngOnDestroy(): void {
    clearInterval(this.clock);
    clearTimeout(this.landingTimer);
    clearTimeout(this.arrivalTimer);
    clearTimeout(this.saveTimer);
    void this.game.disconnect();
  }

  /**
   * Works out what moved between the last snapshot and this one.
   *
   * Two things it deliberately does not animate. A reconnect mid-hand arrives with twenty discards
   * already in the pool, and replaying all of them would be a mess - so the first snapshot only
   * records where things stand. A new hand replaces all sixteen of your tiles at once, which is a
   * deal, not sixteen draws.
   */
  private noticeChanges(view: PlayerGameView | null): void {
    if (!view) return;

    // An offer of another game is a question put to this player, and it is asked on the table
    // behind the result sheet. So the sheet gets out of the way when one arrives - once, on the
    // change, so somebody who reopens the result to read it again is not fighting the screen.
    const offered = !!view.newGame;
    if (offered && !this.offerStanding) this.outcomeDismissed.set(true);
    this.offerStanding = offered;

    const last = view.discards[view.discards.length - 1];
    const discardId = last?.tile.id ?? null;
    const ids = new Set((view.seats[view.yourSeat]?.concealed ?? []).map((t) => t.id));

    const known = this.lastHand !== -1;
    const fresh = view.handNumber !== this.lastHand;
    this.lastHand = view.handNumber;

    if (fresh) {
      this.lastDiscardId = discardId;
      this.myTileIds = ids;

      // Tile ids only mean anything inside one hand, so the grouping from the last one is
      // meaningless. Only on a hand we have actually seen end, though: the very first snapshot
      // after connecting also looks new, and clearing there would race the arrangement `begin`
      // is fetching from the server and sometimes wipe it on reload.
      if (known) {
        this.manualGroups.set([]);
        this.held.set(null);
        this.confirming.set(null);
        this.selected.set(null);
      }

      // A new deal is a new result to wait for, so last hand's dismissal does not carry over.
      this.outcomeDismissed.set(false);

      return;
    }

    this.reconcileGroups(ids);

    if (discardId !== null && discardId !== this.lastDiscardId) {
      // Play runs counter-clockwise from your seat, so the same offset that decides where an
      // opponent's card is drawn decides which edge their discard flies in from.
      this.landing.set({ id: discardId, from: (last!.seat - view.yourSeat + 4) % 4 });
      clearTimeout(this.landingTimer);
      this.landingTimer = setTimeout(() => this.landing.set(null), LANDING_MS);
    }
    this.lastDiscardId = discardId;

    const gained = [...ids].filter((id) => !this.myTileIds.has(id));
    if (gained.length) {
      this.arriving.set(new Set(gained));
      clearTimeout(this.arrivalTimer);
      this.arrivalTimer = setTimeout(() => this.arriving.set(new Set()), ARRIVAL_MS);
    }
    this.myTileIds = ids;

    // Read untracked: writing a signal this effect also reads would schedule itself again.
    const thrown = untracked(this.throwing);
    if (thrown !== null && !ids.has(thrown)) this.throwing.set(null);

    // A tile lifted or put up for confirmation belongs to the discard step it was tapped in. Left
    // standing, it would come back on the next one: the dialog springing open by itself, on a tile
    // the player chose a turn ago.
    if (!untracked(this.canThrowNow)) {
      this.confirming.set(null);
      this.selected.set(null);
    }
  }

  /**
   * Drops tiles that have left the hand out of the groups they were in. A group down to one tile
   * is dissolved, because a group of one is just a loose tile drawn with a gap around it.
   *
   * Not saved back: the server row still lists the thrown tile, and it is filtered on load the same
   * way it is filtered here. Writing on every snapshot would mean a database round trip per move
   * for something nobody asked to change.
   */
  private reconcileGroups(ids: ReadonlySet<number>): void {
    const groups = untracked(this.manualGroups);
    if (!groups.length) return;

    const kept = groups
      .map((group) => group.filter((id) => ids.has(id)))
      .filter((group) => group.length > 1);

    const same =
      kept.length === groups.length && kept.every((g, i) => g.length === groups[i].length);

    if (!same) this.manualGroups.set(kept);

    const held = untracked(this.held);
    if (held !== null && !ids.has(held)) this.held.set(null);
  }

  private async begin(): Promise<void> {
    const seat = this.session.forRoom(this.code());

    if (!seat) {
      await this.router.navigate(['/join', this.code()]);
      return;
    }

    try {
      await this.game.connect(seat.token);
      // After the connection, not before: the hub method needs a live connection, and the hand it
      // belongs to is whichever one the server says is in progress.
      this.manualGroups.set(await this.game.getArrangement());
    } catch {
      this.game.lastError.set('Could not reach the table. Is the server still running?');
    }
  }

  // ---------------------------------------------------------------- seats around the table

  /**
   * Play runs counter-clockwise, so the seat that plays after you sits on your right and the seat
   * that plays before you sits on your left. That ordering is not cosmetic: a chow may only be
   * claimed from the player immediately before you, which is the one shown on the left.
   */
  protected readonly me = computed(() => this.seatAt(0));
  protected readonly rightSeat = computed(() => this.seatAt(1));
  protected readonly acrossSeat = computed(() => this.seatAt(2));
  protected readonly leftSeat = computed(() => this.seatAt(3));

  /**
   * The seat the sheet is showing. Read back out of the live view every time rather than stored,
   * so a meld claimed while the sheet is open appears in it.
   */
  protected readonly zoomedSeat = computed<SeatStateView | null>(() => {
    const seat = this.zoomSeat();
    return seat === null ? null : (this.view()?.seats[seat] ?? null);
  });

  protected backsOpen(seat: number): boolean {
    return this.openBacks().has(seat);
  }

  protected toggleBacks(seat: number): void {
    const open = new Set(this.openBacks());
    if (!open.delete(seat)) open.add(seat);
    this.openBacks.set(open);
  }

  private seatAt(offset: number): SeatStateView | null {
    const view = this.view();
    if (!view) return null;
    return view.seats[(view.yourSeat + offset) % 4] ?? null;
  }

  /** Your own tiles, sorted so the hand reads in a stable order rather than draw order. */
  protected readonly myTiles = computed<TileView[]>(() => {
    const tiles = this.me()?.concealed ?? [];
    return [...tiles].sort((a, b) => compareCodes(a.code, b.code));
  });

  protected readonly isMyTurn = computed(() => {
    const view = this.view();
    return !!view && view.currentSeat === view.yourSeat && view.phase !== 'HandOver';
  });

  /**
   * Whether the server is helping at this table. Off, no claim is spelled out and no hand is laid
   * out, so the claim strip is four bare buttons and Auto Arrange is gone.
   */
  protected readonly assisted = computed(() => this.view()?.assisted !== false);

  /**
   * The deadline the countdown is running against, or null when nothing is on a clock - which is
   * how a window starts and how it stays until somebody calls.
   *
   * Nobody is timed for answering a discard at either kind of table. The one thing a clock still
   * means here is how long the rest of the table has to call over a call that has been made.
   */
  private readonly liveDeadline = computed(() => {
    const at = this.view()?.claim?.deadlineUtc;
    return at ? new Date(at).getTime() : null;
  });

  /** Seconds left before the standing call takes the tile. Zero when nothing is on a clock. */
  protected readonly claimSeconds = computed(() => {
    const deadline = this.liveDeadline();
    if (deadline === null) return 0;

    return Math.max(0, Math.ceil((deadline - this.now()) / 1000));
  });

  /** Whether anything is actually counting down, so the template can leave the number out. */
  protected readonly claimTimed = computed(() => this.liveDeadline() !== null);

  /**
   * How much of the claim window is left, 0 to 1, for the bar under the claim buttons.
   *
   * Off the window the server actually used, not a constant. This was hardcoded to 6, which is the
   * default; a table that sets ClaimWindowSeconds to anything longer got a bar that sat full until
   * the last six seconds and then emptied all at once.
   */
  protected readonly claimFraction = computed(() => {
    const claim = this.view()?.claim;
    if (!claim || !this.claimTimed()) return 0;

    const window = Math.max(1, claim.windowSeconds || 0);

    return Math.min(1, Math.max(0, this.claimSeconds() / window));
  });

  /** The last tile thrown, which is the only one still claimable. */
  protected readonly liveDiscard = computed(() => {
    const discards = this.view()?.discards ?? [];
    const last = discards[discards.length - 1];
    return last && !last.claimed ? last : null;
  });

  /**
   * The thrown tile you can still do something about, or null.
   *
   * Drawn with a live outline in the pool and clickable, which is the whole of the answer to a
   * dialog closed too early. The window is not on a clock, so closing it or looking away is not an
   * answer and costs nothing: the tile is still on the table, and tapping it opens the calls again.
   * The outline is the only thing that says so, and it says nothing about what is in your hand -
   * every seat that has not answered sees it on the same tile.
   */
  protected readonly claimableDiscard = computed(() => {
    const claim = this.view()?.claim;
    if (!claim || claim.youAnswered) return null;

    const live = this.liveDiscard();
    return live && live.tile.id === claim.tile.id ? live : null;
  });

  /** True once the pile is deep enough to be worth drawing smaller. See DENSE_DISCARDS. */
  protected readonly denseDiscards = computed(
    () => (this.view()?.discards.length ?? 0) >= DENSE_DISCARDS,
  );

  /**
   * Reopens the calls off the tile in the pool. Same door as the Options button on the bar.
   *
   * Every tile in the pool is wired to this, and only the one still up for a claim opens anything.
   * The pile is a scroll box of small tiles, so a press landing on a neighbour of the live one is
   * ordinary; making that press do nothing is better than making the live tile a smaller target.
   */
  protected takeLiveDiscard(discard: DiscardView): void {
    if (this.claimableDiscard()?.tile.id === discard.tile.id) this.openClaimDialog();
  }

  // ---------------------------------------------------------------- the claim window

  /** The open claim window, but only while this player still has an answer to give. */
  protected readonly pendingClaim = computed(() => {
    const claim = this.view()?.claim;
    return claim && !claim.youAnswered ? claim : null;
  });

  protected readonly candidates = computed<ClaimCandidateView[]>(
    () => this.pendingClaim()?.candidates ?? [],
  );

  /**
   * Your own call after a stronger one took the tile off you. The window answered for you, so the
   * strip above has nothing left to offer - but going quiet at that moment is exactly what made a
   * chow look like it had been eaten, so the strip says what beat it instead.
   */
  protected readonly outrankedClaim = computed<ClaimPromptView | null>(() => {
    const claim = this.view()?.claim;
    return claim?.outranked ? claim : null;
  });

  /** What the other two answering seats have said. Empty when no window is open. */
  protected readonly calls = computed<SeatCallView[]>(() => this.view()?.claim?.calls ?? []);

  /**
   * The call the rest of the table is now answering to. A finished one first, because that is the
   * one that has actually taken the tile; only one seat can ever pung or kang a given face, so
   * there is never a second finished call to weigh this one against.
   */
  protected readonly standingCall = computed<SeatCallView | null>(
    () =>
      this.calls().find((c) => c.state === 'Called') ??
      this.calls().find((c) => c.state === 'Calling') ??
      null,
  );

  /**
   * One line saying what has happened to the tile and who has still to answer.
   *
   * This is the whole fix for a window that used to resolve out of nowhere: a call was being held
   * silently until the window closed, so a seat could spend a minute building a chow against a
   * pung that had been made one second after the discard.
   */
  protected readonly callLine = computed<string | null>(() => {
    const standing = this.standingCall();

    if (standing) {
      const who = this.seatName(standing.seat);

      return standing.state === 'Calling'
        ? `${who} is calling ${standing.called} - waiting for them to name their tiles`
        : `${who} called ${standing.called}`;
    }

    const waiting = this.calls()
      .filter((c) => c.state === 'Waiting')
      .map((c) => this.seatName(c.seat));

    return waiting.length ? `Waiting for ${waiting.join(' and ')}` : null;
  });

  /** A seat's name for a sentence, falling back to the chair for one nobody is sitting in. */
  protected seatName(seat: number): string {
    return this.view()?.seats[seat]?.displayName ?? `Seat ${seat + 1}`;
  }

  /**
   * Whether a call is still worth pressing. The server works this out from the calls already made
   * - never from what your hand holds - and refuses anything under them, so a button it would
   * refuse is shown dead rather than left to fail on the press.
   */
  protected kindLive(kind: ClaimKind): boolean {
    const live = this.pendingClaim()?.liveKinds;

    // No roster at all means a server older than this build. Every call stays live there, which is
    // how it behaved before any of this existed.
    return live ? live.includes(kind) : true;
  }

  /** The seat that threw the tile now up for claim, so the dialog can name it. */
  protected readonly claimFrom = computed<SeatStateView | null>(() => {
    const claim = this.pendingClaim();
    return claim ? (this.view()?.seats[claim.fromSeat] ?? null) : null;
  });

  /** Tiles tapped so far, dropped as soon as a different discard is up. */
  protected readonly claimPicks = computed<number[]>(() => {
    const state = this.picks();
    return state.discard === (this.pendingClaim()?.tile.id ?? null) ? state.ids : [];
  });

  /**
   * Which candidate, if any, the current pick is. Matched on faces rather than ids: holding three
   * 5-bamboo, which two the player happened to tap for a pung is not a decision the rules have an
   * opinion about, and the server matches the same way.
   */
  protected readonly pickMatch = computed<ClaimCandidateView | null>(() => {
    const picked = this.claimPicks();
    if (picked.length === 0) return null;

    const key = this.codesOf(picked).sort().join(',');

    const match = this.candidates().find(
      (c) => c.tileIds.length === picked.length && this.codesOf(c.tileIds).sort().join(',') === key,
    );

    return match ?? null;
  });

  // ---------------------------------------------------------------- claiming by hand
  //
  // With assist off the server says nothing about what the discard is worth, so the dialog cannot
  // offer options. It offers the four calls instead, and pressing one is only half an answer: the
  // second half is tapping the tiles it costs. Everything below is that second half.

  /** The four calls, in the order they outrank each other. Only drawn when assist is off. */
  private static readonly MANUAL_KINDS: readonly ClaimKind[] = ['Chow', 'Pung', 'Kang', 'Todas'];

  /**
   * The calls worth putting in front of the player on this discard.
   *
   * Chow comes off the list for a tile nobody in this chair could chow whatever they were holding:
   * a wind or a dragon, or a tile thrown by anyone other than the player immediately before you.
   * That is not about the hand - it is on the table for all four to see - so hiding the button
   * gives nothing away, and leaving it there would be worse than useless: with assist off, a
   * refusal is the only answer the player ever gets, and "not from that seat" and "not with those
   * tiles" would arrive looking exactly alike.
   */
  protected readonly callKinds = computed<readonly ClaimKind[]>(() => {
    const chow = this.pendingClaim()?.chowPossible ?? false;
    return TablePage.MANUAL_KINDS.filter((kind) => kind !== 'Chow' || chow);
  });

  /**
   * The calls to put on the action bar itself, rather than only inside the dialog.
   *
   * Everything a player does now happens on one row at the bottom of the screen, within reach of
   * the thumb already resting there: the dialog draws the tiles and is worth opening when there is
   * a choice to make, but nobody should have to open anything to pung a tile.
   *
   * Empty once a call has been pressed and not yet paid for - the row is then Take and Cancel, and
   * offering four more calls on top of the one being named would be offering to start again.
   */
  protected readonly barCalls = computed<BarCall[]>(() => {
    const claim = this.pendingClaim();
    if (!claim || claim.pressedKind) return [];

    return barCallsFor(this.candidates(), this.callKinds(), this.assisted(), (kind) =>
      this.kindLive(kind),
    );
  });

  /** What a bar call says. The count only appears when there is actually a choice behind it. */
  protected barCallWord(call: BarCall): string {
    const word = call.kind === 'Todas' ? 'Todas!' : call.kind;
    return call.options > 1 ? `${word} (${call.options})` : word;
  }

  /**
   * Presses a call from the bar.
   *
   * One tap takes the tile whenever there is only one way to take it, which is the overwhelming
   * majority of calls. More than one way and it opens the dialog instead: which two of three
   * bamboos a chow eats is a decision, and guessing at it on the player's behalf would cost them
   * tiles they were keeping. With assist off nothing has been read at all, so the press is only
   * half an answer and the tiles it costs are tapped out of the hand afterwards.
   */
  protected pressBarCall(call: BarCall): void {
    if (!call.live) return;

    if (!this.assisted()) {
      void this.pressCall(call.kind);
      return;
    }

    if (call.options > 1) {
      this.openClaimDialog();
      return;
    }

    const candidate = this.candidates().find((c) => c.kind === call.kind);
    if (candidate) void this.claimWith(candidate);
  }

  /** What you have pressed and still owe tiles for, or null. */
  protected readonly pressedKind = computed<ClaimKind | null>(
    () => this.pendingClaim()?.pressedKind ?? null,
  );

  /**
   * Your own finished call, while the window is still open on the rest of the table.
   *
   * Calling first holds the tile: everybody else is shown who holds it and can only take it with
   * something that beats it. That has to come with a way out, or a mis-tap owns the tile until the
   * beat runs out - so the bar keeps offering to let it go for as long as the window is open.
   */
  protected readonly yourStandingCall = computed<ClaimPromptView | null>(() => {
    const claim = this.view()?.claim;
    return claim?.youClaimed && claim.youAnswered && !claim.outranked ? claim : null;
  });

  /** What you called, for the line that says the tile is yours unless somebody calls over it. */
  protected readonly yourCallKind = computed<ClaimKind | null>(
    () => this.view()?.claim?.yourCall ?? null,
  );

  /** How many of your own tiles each call costs. A todas names none: the win is the whole hand. */
  private static readonly COST: Record<ClaimKind, number> = { Chow: 2, Pung: 2, Kang: 3, Todas: 0 };

  /**
   * Whether the tiles picked so far are the right number for the call that was pressed. That is as
   * far as the client can check without being told what the hand can do, which is the whole point:
   * whether they actually make the set is the server's answer to give, and a wrong guess comes back
   * as an error rather than being greyed out in advance.
   */
  protected readonly pickReady = computed(() => {
    const kind = this.pressedKind();
    if (!kind) return false;

    return this.claimPicks().length === TablePage.COST[kind];
  });

  /** Presses a call without naming any tiles. The dialog then waits for the tiles. */
  protected async pressCall(kind: ClaimKind): Promise<void> {
    const claim = this.pendingClaim();
    if (!claim) return;

    this.clearRefusal();

    // A todas costs nothing out of hand, so there is no second half and it resolves on the press.
    if (!(await this.game.claim(kind, []))) this.refuse(kind, claim.tile.id);
  }

  /**
   * Lets go of a call, leaving the discard exactly as it was: still there to call something else
   * on, to pass on, or to draw through - and now reachable by everybody else at the table.
   *
   * Two ways in. With assist off, pressing a call is a guess made before counting your own tiles,
   * because nothing on screen said what the tile was worth; finding nothing to pay with must not
   * cost you the discard. And at any table, a call holds the tile against the other three until it
   * resolves, so letting go has to be a thing you can do - otherwise a mistaken call locks the tile
   * up until it wins.
   */
  protected async cancelPress(): Promise<void> {
    this.clearRefusal();
    this.clearPicks();

    await this.game.withdraw();
  }

  /** Hands over the tiles for the call already pressed. */
  protected async confirmPress(): Promise<void> {
    const kind = this.pressedKind();
    const claim = this.pendingClaim();

    if (!kind || !claim || !this.pickReady()) return;

    this.clearRefusal();

    if (!(await this.game.claim(kind, this.claimPicks()))) this.refuse(kind, claim.tile.id);
  }

  // ---------------------------------------------------------------- a refusal you can look at
  //
  // "Seat 0 cannot declare Pung on C9#107" is the server talking to itself. What the player needs
  // is the tile they called on and the tiles the call would have taken, drawn the same size as the
  // ones in their hand, so the mistake is visible rather than described.

  /** The last refusal, held against the discard it was about so it dies with that discard. */
  private readonly refusedPress = signal<{ discard: number | null; refusal: ClaimRefusal | null }>({
    discard: null,
    refusal: null,
  });

  protected readonly refusal = computed<ClaimRefusal | null>(() => {
    const held = this.refusedPress();
    return held.discard === (this.pendingClaim()?.tile.id ?? null) ? held.refusal : null;
  });

  private clearRefusal(): void {
    this.refusedPress.set({ discard: null, refusal: null });
  }

  /**
   * Turns the server's no into something drawable, when the server named a rule the client can
   * illustrate. Anything else is left to the floating message, which still carries the sentence.
   */
  private refuse(kind: ClaimKind, discard: number): void {
    const claim = this.pendingClaim();
    const failure = this.game.lastFailure();

    if (!claim || failure?.code !== 'CannotClaim') return;

    const tile = claim.tile.code;
    const named = this.label(tile);

    const refusal: ClaimRefusal =
      kind === 'Todas'
        ? { text: `${named} does not finish your hand.`, need: [] }
        : {
            text: `You cannot ${kind.toLowerCase()} ${named}. That needs ${TablePage.NEED_WORDS[kind]}:`,
            need: needFor(kind, tile),
          };

    this.refusedPress.set({ discard, refusal });
  }

  /** What each call costs, said in words above the tiles that say it in pictures. */
  private static readonly NEED_WORDS: Record<ClaimKind, string> = {
    Chow: 'two tiles that run with it, in your hand',
    Pung: 'two more of the same tile in your hand',
    Kang: 'three more of the same tile in your hand',
    Todas: '',
  };

  /** Null when the picked tiles make a legal group, otherwise why they do not. */
  protected readonly pickError = computed<string | null>(() => {
    const claim = this.pendingClaim();
    const picked = this.claimPicks();

    if (!claim || picked.length === 0) return null;

    // Nothing to say at an unassisted table: the client was not told what the hand can make, so it
    // has no grounds to call a pick wrong. The server does that when the tiles are sent.
    if (!this.assisted()) return null;

    if (this.pickMatch()) return null;

    const thrown = this.label(claim.tile.code);

    if (picked.length === 1 && this.candidates().some((c) => c.tileIds.includes(picked[0]))) {
      const one = this.label(this.codesOf(picked)[0]);
      return `Pick one more tile - ${one} on its own does not make a set with ${thrown}.`;
    }

    const names = this.codesOf(picked)
      .map((code) => this.label(code))
      .join(' and ');
    return `That is not a valid move. ${names} cannot make a chow, pung or kang with ${thrown}.`;
  });

  /**
   * Tile id to how it could be used, counting how many distinct candidates each tile appears in.
   * One way is a straight yes; two or more means the player has a choice to make and the outline
   * says so.
   */
  protected readonly claimHints = computed<Map<number, HintKind>>(() => {
    const counts = new Map<number, number>();

    for (const candidate of this.candidates())
      for (const id of candidate.tileIds) counts.set(id, (counts.get(id) ?? 0) + 1);

    return new Map([...counts].map(([id, n]) => [id, n > 1 ? 'multi' : 'single'] as const));
  });

  /** The kinds each tile could be part of, highest-ranked first. Drives the badge and the label. */
  private readonly claimKinds = computed<Map<number, ClaimKind[]>>(() => {
    const kinds = new Map<number, ClaimKind[]>();

    for (const candidate of this.candidates())
      for (const id of candidate.tileIds) {
        const found = kinds.get(id) ?? [];
        if (!found.includes(candidate.kind)) found.push(candidate.kind);
        kinds.set(id, found);
      }

    for (const list of kinds.values()) list.sort((a, b) => CLAIM_RANK[b] - CLAIM_RANK[a]);
    return kinds;
  });

  /** True when the discard finishes this hand, which is what the red pulse on it means. */
  protected readonly claimWins = computed(() => this.candidates().some((c) => c.kind === 'Todas'));

  protected hintFor(tileId: number): HintKind {
    return this.claimHints().get(tileId) ?? 'none';
  }

  protected badgeFor(tileId: number): string | null {
    const kinds = this.claimKinds().get(tileId);
    return kinds?.length ? CLAIM_BADGE[kinds[0]] : null;
  }

  protected isPicked(tileId: number): boolean {
    return this.claimPicks().includes(tileId);
  }

  /** Spoken form of a hand tile, with what it could do added while a claim window is open. */
  protected tileLabel(tile: TileView): string {
    const kinds = this.claimKinds().get(tile.id);
    if (!kinds?.length) return this.label(tile.code);

    const words = kinds.map((k) => CLAIM_WORD[k]);
    const can =
      words.length === 1 ? words[0] : `${words.slice(0, -1).join(', ')} or ${words.at(-1)}`;

    return `${this.label(tile.code)}, can ${can}${this.isPicked(tile.id) ? ', picked' : ''}`;
  }

  /** Distinguishes "Chow B3-B4-B5" from "Chow B5-B6-B7" for the specs without reading the label. */
  protected candidateTestId(index: number): string {
    const kind = this.candidates()[index].kind;
    const nth = this.candidates()
      .slice(0, index)
      .filter((c) => c.kind === kind).length;

    return nth === 0 ? `claim-${kind}` : `claim-${kind}-${nth + 1}`;
  }

  private codesOf(tileIds: readonly number[]): string[] {
    const mine = this.me()?.concealed ?? [];
    return tileIds.map((id) => mine.find((t) => t.id === id)?.code ?? '?');
  }

  // ---------------------------------------------------------------- the claim dialog
  //
  // "Pung B5" is only readable to somebody who already knows the notation, and the action bar has
  // no room to draw the set. The dialog does: the thrown tile at the top, and under it one row per
  // option showing the actual tiles the set would be made of.

  protected readonly claimDialogOpen = computed(() => {
    const claim = this.pendingClaim();
    return !!claim && this.dismissedClaim() !== claim.tile.id;
  });

  protected dismissClaimDialog(): void {
    const claim = this.pendingClaim();
    if (claim) this.dismissedClaim.set(claim.tile.id);
  }

  protected openClaimDialog(): void {
    this.dismissedClaim.set(null);
  }

  /** The whole set a candidate would build: your tiles plus the discard, in the order it reads. */
  protected combinationOf(candidate: ClaimCandidateView): ComboTile[] {
    const thrown = this.pendingClaim()?.tile.code;

    const tiles: ComboTile[] = this.codesOf(candidate.tileIds).map((code) => ({
      code,
      thrown: false,
    }));

    // A todas names no tiles from your hand, so the row is the discard on its own.
    if (thrown) tiles.push({ code: thrown, thrown: true });

    return tiles.sort((a, b) => compareCodes(a.code, b.code));
  }

  /** Heading for one option. The kind on its own, because the tiles under it say the rest. */
  protected candidateKind(candidate: ClaimCandidateView): string {
    return candidate.kind === 'Todas' ? 'Todas!' : candidate.kind;
  }

  // ---------------------------------------------------------------- declarations on your turn
  //
  // Secret kang and sagasa are the same problem as a claim: "Secret kang 5 dots" is a sentence
  // about four tiles nobody can see. They live behind one button in the action bar so the bar keeps
  // its height however many faces qualify, and the sheet draws the set.

  protected readonly extraMoves = computed<TurnMove[]>(() => {
    const turn = this.view()?.yourTurn;
    if (!turn) return [];

    const moves: TurnMove[] = [];

    for (const face of turn.secretKangFaces)
      moves.push({
        kind: 'SecretKang',
        face,
        label: `Secret kang ${this.label(face)}`,
        tiles: [face, face, face, face],
        testId: 'declare-secret-kang',
      });

    for (const face of turn.sagasaFaces)
      moves.push({
        kind: 'Sagasa',
        face,
        label: `Sagasa ${this.label(face)}`,
        tiles: [face, face, face, face],
        testId: 'declare-sagasa',
      });

    return moves;
  });

  protected runMove(move: TurnMove): void {
    this.showMoves.set(false);

    // The tiles are about to leave the hand, so a tile still lifted for a throw is lifted over
    // nothing. Clearing it here covers all three routes into a declaration at once.
    this.selected.set(null);

    if (move.kind === 'SecretKang') this.secretKang(move.face);
    else this.sagasa(move.face);
  }

  /** The declaration this face would make on your own turn, or null when it makes none. */
  private moveForFace(code: string): TurnMove | null {
    return this.extraMoves().find((move) => move.face === code) ?? null;
  }

  /**
   * The declaration the lifted tile would make. The Moves button is the complete list, but it is a
   * list you have to remember to open: forget the fourth tile arrived and the hand just plays on
   * three tiles short. Lifting a tile is what a player does when they are thinking about it
   * anyway, so the offer is put there too - the same tile that says "tap again to throw" says
   * "or show all four".
   */
  protected readonly liftedMove = computed<TurnMove | null>(() => {
    const id = this.selected();
    if (id === null || !this.canThrowNow()) return null;

    const tile = (this.me()?.concealed ?? []).find((t) => t.id === id);
    return tile ? this.moveForFace(tile.code) : null;
  });

  /**
   * The declaration a whole block would make: the block holds exactly the four tiles it puts
   * down, so the group you built by hand - or the one Auto Arrange built for you - is itself the
   * button. Sagasa never comes through here; three of its four are already on the table.
   */
  protected blockMove(block: HandBlock): TurnMove | null {
    if (block.tiles.length !== 4) return null;

    const face = block.tiles[0].code;
    if (block.tiles.some((tile) => tile.code !== face)) return null;

    const move = this.moveForFace(face);
    return move?.kind === 'SecretKang' ? move : null;
  }

  /** What a declaration button says. Short, because the tiles under it are the rest of it. */
  protected moveWord(move: TurnMove): string {
    return move.kind === 'SecretKang' ? 'Show all four' : 'Sagasa';
  }

  // ---------------------------------------------------------------- actions

  protected tapTile(tile: TileView): void {
    // A drag ends in a click the browser fires by itself. Without this, every regroup would also
    // throw a tile.
    if (this.swallowClick) {
      this.swallowClick = false;
      return;
    }

    // Reading the tile comes before playing it. The switch is on screen with its state showing, so
    // this is never a surprise - and the sheet it opens offers the throw as well, so turning it on
    // is not a mode you have to leave again to play your turn.
    if (this.zoomTiles()) {
      this.zoomedId.set(tile.id);
      return;
    }

    // While somebody else's discard is up, a tap picks the tile out to claim with instead of
    // lifting it to be thrown. Every tile is tappable, including ones that cannot help: refusing
    // the tap would leave the player guessing why, which is what the message is for.
    if (this.pendingClaim()) {
      this.togglePick(tile.id);
      return;
    }

    if (this.canThrowNow()) {
      // First tap lifts the tile so it can be checked, second tap puts it up to be thrown. The
      // throw itself waits on the dialog: two taps in the same place is a gesture a thumb makes by
      // accident, and there is no taking a tile back once the other three have seen it.
      if (this.selected() === tile.id) {
        this.confirming.set(tile.id);
        return;
      }

      this.selected.set(tile.id);
      return;
    }

    // Nothing else a tap could mean right now, so it groups. This is what the Arrange toggle used
    // to switch on: there is no mode to enter any more, because the two readings of a tap never
    // overlap - you cannot throw a tile on somebody else's turn, and you are not grouping during
    // your own discard.
    if (this.canRegroup()) this.tapArrange(tile);
  }

  /**
   * The tile the confirm dialog is asking about, or null when it is not up.
   *
   * Resolved out of the hand rather than held as a copy, so a tile that leaves some other way -
   * a sagasa, a hand that ends under it - takes the dialog with it instead of leaving it asking
   * about something that is no longer there.
   */
  protected readonly confirmingTile = computed<TileView | null>(() => {
    const id = this.confirming();
    if (id === null || !this.canThrowNow()) return null;
    return (this.me()?.concealed ?? []).find((tile) => tile.id === id) ?? null;
  });

  /** Puts the tile back down. The discard step is exactly where it was: nothing has been sent. */
  protected cancelDiscard(): void {
    this.confirming.set(null);
    this.selected.set(null);
  }

  /** The answer that actually throws it. */
  protected confirmDiscard(): void {
    const id = this.confirming();
    if (id !== null) this.throwTile(id);
  }

  /** The one way a tile leaves your hand, whether it was confirmed or dragged into the pool. */
  private throwTile(tileId: number): void {
    this.confirming.set(null);
    this.selected.set(null);
    this.showMoves.set(false);

    // Marked before the call, not after: the snapshot that removes the tile can arrive before the
    // promise settles, and by then there is nothing left on screen to animate.
    this.throwing.set(tileId);

    // And put back if the server says no - losing a race for the turn is ordinary. The mark drives
    // an animation that ends at opacity 0, so a refused throw would otherwise leave the tile in
    // your hand but invisible.
    void this.game.discard(tileId).then((thrown) => {
      if (!thrown && untracked(this.throwing) === tileId) this.throwing.set(null);
    });
  }

  private clearPicks(): void {
    this.picks.set({ discard: null, ids: [] });
  }

  private togglePick(tileId: number): void {
    const discard = this.pendingClaim()?.tile.id ?? null;
    const current = this.claimPicks();

    this.picks.set({
      discard,
      ids: current.includes(tileId) ? current.filter((id) => id !== tileId) : [...current, tileId],
    });
  }

  /** Takes the discard with exactly the tiles the candidate names. */
  protected async claimWith(candidate: ClaimCandidateView): Promise<void> {
    const claim = this.pendingClaim();
    if (!claim) return;

    this.clearRefusal();

    if (!(await this.game.claim(candidate.kind, [...candidate.tileIds])))
      this.refuse(candidate.kind, claim.tile.id);
  }

  /** Takes the discard with the tiles the player picked by hand. */
  protected async takePick(): Promise<void> {
    const match = this.pickMatch();
    const claim = this.pendingClaim();

    if (!match || !claim) return;

    this.clearRefusal();

    if (!(await this.game.claim(match.kind, this.claimPicks())))
      this.refuse(match.kind, claim.tile.id);
  }

  protected pass(): void {
    void this.game.pass();
  }

  // ---------------------------------------------------------------- taking a tile off the wall
  //
  // Drawing is a button now, and the button is always on the bar. It used to happen by itself the
  // moment the turn came round, on the reasoning that there is only ever one thing you can do -
  // which is true of the rules and false of the person: the tile landed in a sorted hand while
  // they were still looking at what the last player threw, and the one card in the game they are
  // owed a good look at went past unseen. Bots still draw by themselves, because nobody is
  // watching a bot's hand.

  /** The ordinary draw: your turn has come round and the wall is where your next tile is. */
  protected readonly myDrawTurn = computed(() => {
    const view = this.view();
    return !!view && view.phase === 'AwaitingDraw' && view.currentSeat === view.yourSeat;
  });

  /**
   * Whether this seat can end an open claim window by taking its turn early.
   *
   * Only where nothing else would end it - a window with no deadline - and only for the seat due
   * to play next, which is who picks up when the tile goes dead. Drawing gives up whatever you
   * have called, so it is not offered to somebody who has called something: pass first.
   */
  protected readonly canDrawThrough = computed(() => {
    const view = this.view();
    if (!view?.claim || view.claim.deadlineUtc || view.claim.youClaimed) return false;

    // Somebody else is part way through a call. The server refuses a draw that would cut across
    // it, so the button must not appear to offer one either. Read defensively: a server that has
    // not been restarted onto this build sends no roster at all, and the button going dead is a
    // better failure there than the whole view throwing.
    if (this.calls().some((c) => c.state === 'Calling')) return false;

    return (view.currentSeat + 1) % 4 === view.yourSeat;
  });

  /** Whether the Draw button does anything right now. It is on the bar either way. */
  protected readonly canDraw = computed(() => this.myDrawTurn() || this.canDrawThrough());

  /**
   * What the button means this instant. Three answers, not two: the button sits there dead for
   * most of the game, and a dead button whose label explains the one thing it is not doing right
   * now is worse than one that says why it is dead.
   */
  protected readonly drawHint = computed(() => {
    if (this.myDrawTurn()) return 'Take your tile from the wall';

    if (this.canDrawThrough())
      return 'Give up on that discard and take a tile from the wall instead';

    const view = this.view();

    if (view?.claim) {
      if (view.claim.youClaimed) return 'You have called on that tile - take the call back first';

      const naming = this.calls().find((c) => c.state === 'Calling');
      if (naming) return `${this.seatName(naming.seat)} is still naming their tiles`;

      return `${this.seatName((view.currentSeat + 1) % 4)} is the one who can draw through this`;
    }

    return view ? `${this.seatName(view.currentSeat)} is playing` : 'Not yours to take yet';
  });

  protected draw(): void {
    if (!this.canDraw()) return;
    void this.game.draw();
  }

  protected secretKang(face: string): void {
    void this.game.declareSecretKang(face);
  }

  protected sagasa(face: string): void {
    void this.game.declareSagasa(face);
  }

  protected todas(): void {
    void this.game.declareTodas();
  }

  /**
   * Turns your hand face up for the other three. Offered only once the hand is over, and only
   * once: the server refuses a second press, and there is nothing to undo it with.
   */
  protected reveal(): void {
    if (!this.handOver() || this.iRevealed()) return;
    void this.game.reveal();
  }

  protected proposeNewGame(): void {
    void this.game.proposeNewGame();
  }

  protected cancelNewGame(): void {
    void this.game.cancelNewGame();
  }

  protected acceptNewGame(): void {
    void this.game.acceptNewGame();
  }

  /**
   * Says no, which frees this seat and takes this browser off the table. The server answers before
   * the token stops working, and the push it sends is what actually moves the page - so nothing
   * here navigates, and a refusal leaves the player exactly where they were.
   */
  protected declineNewGame(): void {
    void this.game.declineNewGame();
  }

  protected removeSeat(seat: number): void {
    void this.game.removeSeat(seat);
  }

  /**
   * The host's second press on Close. Ending a table takes everybody with it and cannot be undone,
   * so it is never one tap - and the confirmation is inline rather than a sheet, because the thing
   * being ended is the screen behind it.
   */
  protected readonly closing = signal(false);

  protected closeTable(): void {
    this.closing.set(false);
    this.showHost.set(false);
    void this.game.closeTable();
  }

  /**
   * Puts the host sheet away, and takes the Close confirmation down with it. A half-pressed Close
   * left standing behind a closed sheet is one tap away from ending the table the next time it is
   * opened, which is not what the person who put the sheet away meant.
   */
  protected closeHostSheet(): void {
    this.showHost.set(false);
    this.closing.set(false);
  }

  protected fillWithBots(): void {
    void this.game.fillWithBots();
  }

  protected async copyInvite(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.inviteUrl());
    } catch {
      // Clipboard access needs a secure context, which plain http on a LAN is not. The link is on
      // screen to be copied by hand, so this is a nicety failing, not the feature failing.
    }

    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 1800);
  }

  protected closeOutcome(): void {
    this.outcomeDismissed.set(true);
  }

  protected reopenOutcome(): void {
    this.outcomeDismissed.set(false);
  }

  protected async backToLobby(): Promise<void> {
    await this.game.disconnect();
    await this.router.navigate(['/room', this.code()]);
  }

  // ---------------------------------------------------------------- auto arrange

  /**
   * The hand as blocks. One block of everything when the toggle is off, so the template has one
   * shape either way.
   */
  protected readonly handBlocks = computed<HandBlock[]>(() => {
    const groups = this.me()?.groups;

    // Falls back to the plain sort rather than an empty hand, so an older server or a view built
    // before this landed still shows tiles.
    if (!this.arranged() || !groups?.length) {
      return this.manualBlocks();
    }

    return groups.map((group, index) => {
      const spoken = group.tiles.map((t) => this.label(t.code)).join(', ');
      const word = GROUP_WORD[group.kind];

      const label =
        group.kind === 'Partial'
          ? `needs ${group.needs.join('/')}`
          : group.kind === 'Floater'
            ? ''
            : word;

      const ariaLabel =
        group.kind === 'Partial'
          ? `${spoken}, needs ${group.needs.map((code) => this.label(code)).join(' or ')}`
          : group.kind === 'Floater'
            ? `spare, ${spoken}`
            : `${word}, ${spoken}`;

      return {
        // Indexed, because two blocks can hold the same faces - two pairs of 5 dots, say.
        key: `${index}:${group.tiles.map((t) => t.id).join('-')}`,
        kind: group.kind,
        tiles: group.tiles,
        label,
        ariaLabel,
      };
    });
  });

  /**
   * Your hand as you arranged it: one block per group you built, then one holding everything still
   * loose. With nothing grouped that is a single block of the whole hand, which is exactly what it
   * was before any of this existed.
   */
  private manualBlocks(): HandBlock[] {
    const tiles = this.myTiles();
    const groups = this.manualGroups();

    if (!groups.length) {
      return [{ key: 'all', kind: 'all', tiles, label: '', ariaLabel: '' }];
    }

    const byId = new Map(tiles.map((tile) => [tile.id, tile]));
    const grouped = new Set<number>();
    const blocks: HandBlock[] = [];

    groups.forEach((ids, index) => {
      const found = ids.map((id) => byId.get(id)).filter((tile): tile is TileView => !!tile);
      if (found.length < 2) return;

      for (const tile of found) grouped.add(tile.id);

      blocks.push({
        // Indexed as well as keyed by tile, because two groups can hold the same faces.
        key: `m${index}:${found.map((t) => t.id).join('-')}`,
        kind: 'manual',
        tiles: found,
        label: '',
        ariaLabel: `group, ${found.map((t) => this.label(t.code)).join(', ')}`,
      });
    });

    const loose = tiles.filter((tile) => !grouped.has(tile.id));
    if (loose.length)
      blocks.push({ key: 'all', kind: 'all', tiles: loose, label: '', ariaLabel: '' });

    return blocks;
  }

  /**
   * Whether the hand is yours to rearrange. Only Auto Arrange takes it away: with the server
   * laying the hand out, dragging a tile somewhere else would be overwritten by the next snapshot.
   * Everything else - hand up or down, your turn or not, a claim window open - can be rearranged
   * through, because "let me tidy my hand while I wait" is when anybody would want to.
   */
  protected readonly canRegroup = computed(() => !this.arranged());

  /** Whether a tap on a tile groups rather than throws or picks. Drives the dimming, and the hint. */
  protected readonly tapGroups = computed(
    () => this.canRegroup() && !this.pendingClaim() && !this.canThrowNow(),
  );

  protected readonly draggedTile = computed<TileView | null>(() => {
    const id = this.dragged();
    return id === null ? null : ((this.me()?.concealed ?? []).find((t) => t.id === id) ?? null);
  });

  /** Whether throwing a tile is legal this instant. Both routes to a throw check this one thing. */
  protected readonly canThrowNow = computed(
    () => !this.pendingClaim() && this.isMyTurn() && this.view()?.phase === 'AwaitingDiscard',
  );

  /**
   * Tiles you cannot act on right now. This used to be the `disabled` attribute, but a disabled
   * button receives no pointer events at all, which would leave the hand rearrangeable only during
   * your own discard step - the one moment nobody is grouping. `aria-disabled` says the same thing
   * to a screen reader and still lets the tile be dragged.
   */
  protected readonly tileInert = computed(
    () =>
      !this.zoomTiles() && !this.pendingClaim() && !this.tapGroups() && !this.canThrowNow(),
  );

  protected isGrouped(tileId: number): boolean {
    return this.manualGroups().some((group) => group.includes(tileId));
  }

  /** Which side of this tile the drop would land on, so the template can draw the gap opening. */
  protected dropSide(tileId: number): 'before' | 'after' | null {
    const slot = this.dropTarget();
    if (!slot || slot.id !== tileId) return null;
    return slot.before ? 'before' : 'after';
  }

  /**
   * Puts a tile in the gap on one side of another tile: into that tile's group at that exact
   * position, or starting a new group of the two when the target is loose. Taking the tile out of
   * wherever it was first is what makes a move inside one group work - drop the third tile of a run
   * between the first two and the group is rebuilt around the gap, not appended to.
   */
  private insertNear(movedId: number, targetId: number, before: boolean): void {
    if (movedId === targetId) return;

    const without = this.manualGroups().map((group) => group.filter((id) => id !== movedId));
    const at = without.findIndex((group) => group.includes(targetId));

    if (at < 0) {
      this.setGroups([...without, before ? [movedId, targetId] : [targetId, movedId]]);
      return;
    }

    const group = [...without[at]];
    const index = group.indexOf(targetId);
    group.splice(before ? index : index + 1, 0, movedId);

    this.setGroups(without.map((existing, i) => (i === at ? group : existing)));
  }

  private ungroup(tileId: number): void {
    if (!this.isGrouped(tileId)) return;
    this.setGroups(this.manualGroups().map((group) => group.filter((id) => id !== tileId)));
  }

  private setGroups(next: readonly (readonly number[])[]): void {
    this.manualGroups.set(next.filter((group) => group.length > 1));

    // Debounced, because dragging three tiles into a group in quick succession is one decision,
    // not three, and each one would otherwise be its own round trip.
    clearTimeout(this.saveTimer);
    this.saveTimer = setTimeout(
      () => void this.game.saveArrangement(this.manualGroups()),
      SAVE_DEBOUNCE_MS,
    );
  }

  // ---------------------------------------------------------------- dragging a tile

  protected onTilePointerDown(event: PointerEvent, tile: TileView): void {
    this.swallowClick = false;
    // Armed for either of the two things a drag can do, so dragging a tile into the middle to
    // throw it works even with Auto Arrange on, where there is nothing to regroup.
    if (!this.canRegroup() && !this.canThrowNow()) return;
    if (event.pointerType === 'mouse' && event.button !== 0) return;

    const el = event.currentTarget as HTMLElement;
    this.press = {
      x: event.clientX,
      y: event.clientY,
      id: tile.id,
      pointerId: event.pointerId,
      el,
    };

    // Captured here rather than once the drag arms, so the whole gesture is guaranteed to arrive
    // on this tile. Waiting for the threshold means a quick flick that leaves the tile before the
    // first move event lands never arms at all. Safe to do unconditionally: a press only gets this
    // far while the hand is lifted, where nothing underneath it scrolls.
    el.setPointerCapture(event.pointerId);
  }

  protected onTilePointerMove(event: PointerEvent): void {
    const press = this.press;
    if (!press || press.pointerId !== event.pointerId) return;

    if (this.dragged() === null) {
      const moved = Math.hypot(event.clientX - press.x, event.clientY - press.y);
      if (moved < DRAG_THRESHOLD_PX) return;

      this.dragged.set(press.id);
    }

    this.ghost.set({ x: event.clientX, y: event.clientY });

    const over = this.dropAt(event.clientX, event.clientY, press.id);
    this.overPool.set(over.pool);
    this.dropTarget.set(over.tile);
  }

  protected onTilePointerUp(event: PointerEvent): void {
    const press = this.press;
    if (!press || press.pointerId !== event.pointerId) return;

    if (this.dragged() !== null) {
      const target = this.dropTarget();

      if (this.overPool()) {
        // Dropped in the middle. Nothing happens when a throw is not legal - a tile disappearing
        // because it was let go over a pool that could not take it would be worse than nothing.
        if (this.canThrowNow()) this.throwTile(press.id);
      } else if (this.canRegroup()) {
        // Onto a gap beside another tile drops it there; let go anywhere else in your own area
        // takes it out of the group it was in.
        if (target) this.insertNear(press.id, target.id, target.before);
        else this.ungroup(press.id);
      }

      // The browser fires a click after the pointer sequence, and that click would throw the tile.
      this.swallowClick = true;
      setTimeout(() => (this.swallowClick = false));
    }

    this.endDrag();
  }

  protected onTilePointerCancel(): void {
    this.endDrag();
  }

  private endDrag(): void {
    this.press = null;
    this.dragged.set(null);
    this.dropTarget.set(null);
    this.overPool.set(false);
    this.ghost.set(null);
  }

  /**
   * Where a dragged tile would land if it were let go right now. The ghost never answers: it is
   * `pointer-events: none`, so the thing under the pointer is the thing the player is aiming at.
   */
  private dropAt(x: number, y: number, movedId: number): { pool: boolean; tile: DropSlot | null } {
    const el = document.elementFromPoint(x, y);
    if (!el) return { pool: false, tile: null };

    if (el.closest('.pool')) return { pool: true, tile: null };

    const button = el.closest<HTMLElement>('.tile-button[data-tile-id]');
    if (!button) return { pool: false, tile: null };

    const id = Number(button.dataset['tileId']);
    if (!Number.isFinite(id) || id === movedId) return { pool: false, tile: null };

    // Which half of the tile the pointer is over decides which of the two gaps beside it is meant.
    // Halves rather than a narrower edge strip: every point over a tile has to answer, or there
    // would be dead middles where a drag says nothing at all.
    const box = button.getBoundingClientRect();
    return { pool: false, tile: { id, before: x < box.left + box.width / 2 } };
  }

  // ---------------------------------------------------------------- grouping by tap

  private tapArrange(tile: TileView): void {
    const held = this.held();

    if (held === null) {
      this.held.set(tile.id);
      return;
    }

    if (held === tile.id) {
      // Tapping the same tile twice puts it down, and takes it out of its group on the way if it
      // was in one. That is the only gesture that ungroups without a drag.
      this.ungroup(tile.id);
      this.held.set(null);
      return;
    }

    // A tap has no side to it, so it lands the tile after the one that was tapped - the end of
    // that group when the tap was on its last tile, which is what "group these two" reads as.
    this.insertNear(held, tile.id, false);
    this.held.set(null);
  }

  /**
   * Puts the hand back in plain suit-and-rank order with nothing grouped.
   *
   * The tiles are always drawn in that order, so what this actually undoes is the grouping: the
   * blocks you pushed together by hand, and Auto Arrange if it was doing the pushing. One button
   * for "start again from a tidy hand", which is what a player means when they say sort.
   */
  protected sortHand(): void {
    this.held.set(null);
    this.endDrag();
    this.setGroups([]);

    if (this.arrangePreference()) {
      this.arrangePreference.set(false);
      writeFlag(ARRANGE_KEY, false);
    }
  }

  protected toggleArrange(): void {
    const next = !this.arrangePreference();
    this.arrangePreference.set(next);
    writeFlag(ARRANGE_KEY, next);

    // The server is laying the hand out now, so a tile half-picked up for grouping has nothing
    // left to be grouped into.
    if (next) {
      this.held.set(null);
      this.endDrag();
    }
  }

  // ---------------------------------------------------------------- looking at a tile

  /**
   * The tile blown up on screen, or null when nothing is.
   *
   * Resolved out of the hand each time rather than kept as a copy, so a tile that leaves some other
   * way - thrown, taken into a kang, a hand that ends under it - takes the sheet with it instead of
   * leaving it showing something that is no longer there.
   */
  protected readonly zoomedTile = computed<TileView | null>(() => {
    const id = this.zoomedId();
    if (id === null) return null;

    return (this.me()?.concealed ?? []).find((tile) => tile.id === id) ?? null;
  });

  protected closeZoom(): void {
    this.zoomedId.set(null);
  }

  /**
   * The thrown tile the player has tapped to enlarge, or null.
   *
   * At 30px on the bar a 4-dots and a 5-dots are a counting exercise on a phone, and that tile is
   * the whole question the window is asking. One tap blows it up; nothing else about the window
   * moves, and the calls stay where the thumb left them.
   */
  protected readonly zoomedDiscard = computed<TileView | null>(() =>
    this.claimZoom() ? (this.pendingClaim()?.tile ?? null) : null,
  );

  protected openClaimZoom(): void {
    this.claimZoom.set(true);
  }

  protected closeClaimZoom(): void {
    this.claimZoom.set(false);
  }

  protected toggleZoom(): void {
    const next = !this.zoomTiles();
    this.zoomTiles.set(next);

    // Turning it on takes over what a tap means, so anything half-started by one is put back: a
    // tile lifted to be thrown, and a tile picked up to be grouped with another.
    if (next) {
      this.selected.set(null);
      this.held.set(null);
      this.endDrag();
    } else {
      this.closeZoom();
    }
  }

  /**
   * Throws the tile the sheet is showing.
   *
   * The sheet is already the second look at it - one tap to blow it up, one to throw it - so there
   * is no third question. That is the same two-tap shape as the confirm dialog it stands in for.
   */
  protected discardZoomed(): void {
    const tile = this.zoomedTile();
    if (!tile || !this.canThrowNow()) return;

    this.closeZoom();
    this.throwTile(tile.id);
  }

  /** Lifts your box up over the discard pool so the whole hand is on screen, or drops it back. */
  protected toggleHandOpen(): void {
    const next = !this.handOpen();
    this.handOpen.set(next);
    writeFlag(HAND_OPEN_KEY, next);

    // The tiles are about to move a long way; a drag still in flight would be pointing at a tile
    // that is no longer under the pointer.
    this.endDrag();
  }

  // ---------------------------------------------------------------- display helpers

  protected windOf(seat: number): string {
    return ['East', 'South', 'West', 'North'][seat] ?? '';
  }

  /** Wild this hand, so the face drawn on the tile is not the face it is playing as. */
  protected isJoker(code: string): boolean {
    const joker = this.view()?.joker;
    return !!joker && code === joker;
  }

  protected label(code: string): string {
    return this.isJoker(code) ? `${describe(code)} as joker` : describe(code);
  }

  protected bonusLabel(name: string): string {
    return BONUS_LABELS[name] ?? name;
  }

  /** A secret kang is shown face down to everyone except the player who declared it. */
  protected meldFaces(meld: MeldView, isMine: boolean): string[] {
    if (meld.concealed && !isMine) return meld.tiles.map(() => 'back');
    return meld.tiles.map((t) => t.code);
  }

  protected repeat(count: number): number[] {
    return Array.from({ length: Math.max(0, count) }, (_, i) => i);
  }

  protected outcomeTitle(view: PlayerGameView): string {
    const outcome = view.outcome;
    if (!outcome) return '';

    if (outcome.reason === 'WallExhausted') return 'Drawn hand - the wall ran out';
    if (outcome.winnerSeat === view.yourSeat)
      return outcome.reason === 'Bisaklat' ? 'Bisaklat!' : 'Todas! You win';

    const name =
      outcome.winnerSeat !== null ? view.seats[outcome.winnerSeat].displayName : 'Somebody';
    return `${name} declared todas`;
  }
}

/**
 * The calls to put on the action bar, in the order they outrank each other.
 *
 * Two shapes in, one out. With the helper on, the server has already worked out every legal way
 * this hand could take the tile, so the bar is built from those: one button per kind, carrying how
 * many distinct shapes are behind it. With it off nothing has been read for this player at all, so
 * the bar offers the bare calls the rules could allow off the tile and pressing one is only half an
 * answer - which is why `options` is zero there rather than one. Either way, a call the table has
 * already heard something better than comes through dead rather than missing: a button that
 * vanished mid-window is a button the thumb was already on its way to.
 *
 * Free of the component on purpose - it reads its four arguments and nothing else - because the
 * ordering and the counting are the whole of what makes one tap enough to pung a tile.
 */
export function barCallsFor(
  candidates: readonly ClaimCandidateView[],
  kinds: readonly ClaimKind[],
  assisted: boolean,
  isLive: (kind: ClaimKind) => boolean,
): BarCall[] {
  const call = (kind: ClaimKind, options: number): BarCall => ({
    kind,
    options,
    live: isLive(kind),
    testId: `bar-call-${kind}`,
  });

  if (!assisted) return kinds.map((kind) => call(kind, 0));

  const counts = new Map<ClaimKind, number>();
  for (const candidate of candidates)
    counts.set(candidate.kind, (counts.get(candidate.kind) ?? 0) + 1);

  return [...counts]
    .sort(([a], [b]) => CLAIM_RANK[b] - CLAIM_RANK[a])
    .map(([kind, options]) => call(kind, options));
}

/** Suit then rank, so a hand and a claim combination both read in the order players expect. */
/**
 * The tiles from your own hand a call would have taken, as rows: one row for a pung or a kang,
 * one row per run for a chow, because a suited tile sits in up to three different runs and which
 * of them the player was missing is exactly what they need to see.
 */
function needFor(kind: ClaimKind, tile: string): string[][] {
  if (kind === 'Pung') return [[tile, tile]];
  if (kind === 'Kang') return [[tile, tile, tile]];
  if (kind !== 'Chow') return [];

  const suit = tile[0];
  const rank = Number(tile.slice(1));

  if (!SUITED.includes(suit) || !Number.isFinite(rank)) return [];

  // The three windows a run can put this tile in: at the top, in the middle, at the bottom.
  return [
    [rank - 2, rank - 1],
    [rank - 1, rank + 1],
    [rank + 1, rank + 2],
  ]
    .filter((pair) => pair.every((r) => r >= 1 && r <= 9))
    .map((pair) => pair.map((r) => `${suit}${r}`));
}

/** Suit letters that make runs. Winds and dragons do not, which is why they never offer a chow. */
const SUITED = 'DBC';

function compareCodes(a: string, b: string): number {
  const suit = (SUIT_ORDER[a[0]] ?? 9) - (SUIT_ORDER[b[0]] ?? 9);
  return suit !== 0 ? suit : Number(a.slice(1)) - Number(b.slice(1));
}

function readFlag(key: string, fallback: boolean): boolean {
  try {
    const stored = localStorage.getItem(key);
    return stored === null ? fallback : stored === '1';
  } catch {
    return fallback;
  }
}

function writeFlag(key: string, value: boolean): void {
  try {
    localStorage.setItem(key, value ? '1' : '0');
  } catch {
    // A browser with storage blocked still gets the toggle, it just will not remember it.
  }
}
