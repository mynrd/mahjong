import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  computed,
  inject,
  input,
  signal,
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

/** One run of tiles drawn together. A single block holding everything when Auto Arrange is off. */
export interface HandBlock {
  key: string;
  kind: HandGroupKind | 'all';
  tiles: TileView[];
  /** Shown under the block. Empty for the one-block layout. */
  label: string;
  ariaLabel: string;
}

/** Where the local claim preference is kept. Not game state, so it lives with the browser. */
const ARRANGE_KEY = 'mj.arrange';

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
   * Layout mode for your own hand. Sticky, because the one-shot alternative goes stale the moment
   * a tile is drawn and a stale grouping is worse than none.
   */
  protected readonly arranged = signal(readArrangePreference());

  constructor() {
    queueMicrotask(() => this.begin());
  }

  ngOnDestroy(): void {
    clearInterval(this.clock);
    void this.game.disconnect();
  }

  private async begin(): Promise<void> {
    const seat = this.session.forRoom(this.code());

    if (!seat) {
      await this.router.navigate(['/join', this.code()]);
      return;
    }

    try {
      await this.game.connect(seat.token);
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

  private seatAt(offset: number): SeatStateView | null {
    const view = this.view();
    if (!view) return null;
    return view.seats[(view.yourSeat + offset) % 4] ?? null;
  }

  /** Your own tiles, sorted so the hand reads in a stable order rather than draw order. */
  protected readonly myTiles = computed<TileView[]>(() => {
    const tiles = this.me()?.concealed ?? [];

    return [...tiles].sort((a, b) => {
      const suit = (SUIT_ORDER[a.code[0]] ?? 9) - (SUIT_ORDER[b.code[0]] ?? 9);
      return suit !== 0 ? suit : Number(a.code.slice(1)) - Number(b.code.slice(1));
    });
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

  protected readonly claimFraction = computed(() => {
    const view = this.view();
    if (!view?.claim) return 0;
    return Math.min(1, Math.max(0, this.claimSeconds() / 6));
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

  // ---------------------------------------------------------------- actions

  protected tapTile(tile: TileView): void {
    // While somebody else's discard is up, a tap picks the tile out to claim with instead of
    // lifting it to be thrown. Every tile is tappable, including ones that cannot help: refusing
    // the tap would leave the player guessing why, which is what the message is for.
    if (this.pendingClaim()) {
      this.togglePick(tile.id);
      return;
    }

    if (!this.isMyTurn() || this.view()?.phase !== 'AwaitingDiscard') return;

    // First tap lifts the tile so it can be checked, second tap throws it. On a phone there is no
    // hover and no room for a confirm dialog, and a mis-thrown tile cannot be taken back.
    if (this.selected() === tile.id) {
      this.selected.set(null);
      void this.game.discard(tile.id);
      return;
    }

    this.selected.set(tile.id);
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
      return [{ key: 'all', kind: 'all', tiles: this.myTiles(), label: '', ariaLabel: '' }];
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

  protected toggleArrange(): void {
    const next = !this.arranged();
    this.arranged.set(next);

    try {
      localStorage.setItem(ARRANGE_KEY, next ? '1' : '0');
    } catch {
      // A browser with storage blocked still gets the toggle, it just will not remember it.
    }
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

function readArrangePreference(): boolean {
  try {
    return localStorage.getItem(ARRANGE_KEY) === '1';
  } catch {
    return false;
  }
}
