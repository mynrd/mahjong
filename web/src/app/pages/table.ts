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
  HandGroupKind,
  MeldView,
  PlayerGameView,
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

/** One tile of the set a claim would build, drawn in the dialog so the shape is not just letters. */
export interface ComboTile {
  code: string;
  /** The discard itself, outlined so it is clear which tile is being taken. */
  thrown: boolean;
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

  /** The tile lifted out of the hand. A second tap on the same tile throws it. */
  protected readonly selected = signal<number | null>(null);

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

  /**
   * The seat whose sheet is open, or null. An opponent card is about 110px wide on a phone, which
   * is not enough to read a kang of characters off; tapping one blows that player's exposed tiles
   * up to hand size.
   */
  protected readonly zoomSeat = signal<number | null>(null);

  /**
   * Layout mode for your own hand. Sticky, because the one-shot alternative goes stale the moment
   * a tile is drawn and a stale grouping is worse than none.
   */
  protected readonly arranged = signal(readFlag(ARRANGE_KEY, false));

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

  /** The tile being dragged, what it is over, and where to draw the ghost. */
  protected readonly dragged = signal<number | null>(null);
  protected readonly dropTarget = signal<number | null>(null);
  protected readonly overPool = signal(false);
  protected readonly ghost = signal<{ x: number; y: number } | null>(null);

  private press: { x: number; y: number; id: number; pointerId: number; el: HTMLElement } | null = null;

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
  private landingTimer: ReturnType<typeof setTimeout> | undefined;
  private arrivalTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    queueMicrotask(() => this.begin());
    effect(() => this.noticeChanges(this.view()));
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
      }

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

  /** Seconds left to answer a discard, floored at zero. */
  protected readonly claimSeconds = computed(() => {
    const claim = this.view()?.claim;
    if (!claim) return 0;

    const remaining = new Date(claim.deadlineUtc).getTime() - this.now();
    return Math.max(0, Math.ceil(remaining / 1000));
  });

  /**
   * How much of the claim window is left, 0 to 1, for the bar under the claim buttons.
   *
   * Off the window the server actually used, not a constant. This was hardcoded to 6, which is the
   * default; a table that sets ClaimWindowSeconds to anything longer got a bar that sat full until
   * the last six seconds and then emptied all at once.
   */
  protected readonly claimFraction = computed(() => {
    const claim = this.view()?.claim;
    if (!claim) return 0;

    const window = Math.max(1, claim.windowSeconds || 0);
    return Math.min(1, Math.max(0, this.claimSeconds() / window));
  });

  /** The last tile thrown, which is the only one still claimable. */
  protected readonly liveDiscard = computed(() => {
    const discards = this.view()?.discards ?? [];
    const last = discards[discards.length - 1];
    return last && !last.claimed ? last : null;
  });

  // ---------------------------------------------------------------- the claim window

  /** The open claim window, but only while this player still has an answer to give. */
  protected readonly pendingClaim = computed(() => {
    const claim = this.view()?.claim;
    return claim && !claim.youAnswered ? claim : null;
  });

  protected readonly candidates = computed<ClaimCandidateView[]>(
    () => this.pendingClaim()?.candidates ?? [],
  );

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

  /** Null when the picked tiles make a legal group, otherwise why they do not. */
  protected readonly pickError = computed<string | null>(() => {
    const claim = this.pendingClaim();
    const picked = this.claimPicks();

    if (!claim || picked.length === 0) return null;
    if (this.pickMatch()) return null;

    const thrown = this.label(claim.tile.code);

    if (picked.length === 1 && this.candidates().some((c) => c.tileIds.includes(picked[0]))) {
      const one = this.label(this.codesOf(picked)[0]);
      return `Pick one more tile - ${one} on its own does not make a set with ${thrown}.`;
    }

    const names = this.codesOf(picked).map((code) => this.label(code)).join(' and ');
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
    const nth = this.candidates().slice(0, index).filter((c) => c.kind === kind).length;

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
    if (move.kind === 'SecretKang') this.secretKang(move.face);
    else this.sagasa(move.face);
  }

  // ---------------------------------------------------------------- actions

  protected tapTile(tile: TileView): void {
    // A drag ends in a click the browser fires by itself. Without this, every regroup would also
    // throw a tile.
    if (this.swallowClick) {
      this.swallowClick = false;
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
      // First tap lifts the tile so it can be checked, second tap throws it. On a phone there is no
      // hover and no room for a confirm dialog, and a mis-thrown tile cannot be taken back.
      if (this.selected() === tile.id) {
        this.throwTile(tile.id);
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

  /** The one way a tile leaves your hand, whether it was tapped twice or dragged into the pool. */
  private throwTile(tileId: number): void {
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

  private togglePick(tileId: number): void {
    const discard = this.pendingClaim()?.tile.id ?? null;
    const current = this.claimPicks();

    this.picks.set({
      discard,
      ids: current.includes(tileId) ? current.filter((id) => id !== tileId) : [...current, tileId],
    });
  }

  /** Takes the discard with exactly the tiles the candidate names. */
  protected claimWith(candidate: ClaimCandidateView): void {
    void this.game.claim(candidate.kind, [...candidate.tileIds]);
  }

  /** Takes the discard with the tiles the player picked by hand. */
  protected takePick(): void {
    const match = this.pickMatch();
    if (!match) return;

    void this.game.claim(match.kind, this.claimPicks());
  }

  protected pass(): void {
    void this.game.pass();
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
        group.kind === 'Partial' ? `needs ${group.needs.join('/')}` : group.kind === 'Floater' ? '' : word;

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
    if (loose.length) blocks.push({ key: 'all', kind: 'all', tiles: loose, label: '', ariaLabel: '' });

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
    return id === null ? null : (this.me()?.concealed ?? []).find((t) => t.id === id) ?? null;
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
    () => !this.pendingClaim() && !this.tapGroups() && !this.canThrowNow(),
  );

  protected isGrouped(tileId: number): boolean {
    return this.manualGroups().some((group) => group.includes(tileId));
  }

  /** Moves a tile into the group the target is in, starting one if the target is loose. */
  private joinInto(movedId: number, targetId: number): void {
    if (movedId === targetId) return;

    const without = this.manualGroups().map((group) => group.filter((id) => id !== movedId));
    const at = without.findIndex((group) => group.includes(targetId));

    this.setGroups(
      at >= 0
        ? without.map((group, i) => (i === at ? [...group, movedId] : group))
        : [...without, [targetId, movedId]],
    );
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
    this.press = { x: event.clientX, y: event.clientY, id: tile.id, pointerId: event.pointerId, el };

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
        // Onto another tile joins its group; let go anywhere else in your own area takes it out of
        // the one it was in.
        if (target !== null) this.joinInto(press.id, target);
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
  private dropAt(x: number, y: number, movedId: number): { pool: boolean; tile: number | null } {
    const el = document.elementFromPoint(x, y);
    if (!el) return { pool: false, tile: null };

    if (el.closest('.pool')) return { pool: true, tile: null };

    const button = el.closest<HTMLElement>('.tile-button[data-tile-id]');
    if (!button) return { pool: false, tile: null };

    const id = Number(button.dataset['tileId']);
    return { pool: false, tile: Number.isFinite(id) && id !== movedId ? id : null };
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

    this.joinInto(held, tile.id);
    this.held.set(null);
  }

  protected toggleArrange(): void {
    const next = !this.arranged();
    this.arranged.set(next);
    writeFlag(ARRANGE_KEY, next);

    // The server is laying the hand out now, so a tile half-picked up for grouping has nothing
    // left to be grouped into.
    if (next) {
      this.held.set(null);
      this.endDrag();
    }
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
    if (outcome.winnerSeat === view.yourSeat) return outcome.reason === 'Bisaklat' ? 'Bisaklat!' : 'Todas! You win';

    const name = outcome.winnerSeat !== null ? view.seats[outcome.winnerSeat].displayName : 'Somebody';
    return `${name} declared todas`;
  }
}

/** Suit then rank, so a hand and a claim combination both read in the order players expect. */
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
