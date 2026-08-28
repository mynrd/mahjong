import { ChangeDetectionStrategy, Component, HostListener, computed, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../core/api';
import {
  BONUS_LABELS,
  HandGroupKind,
  MeldView,
  ReplayFrameView,
  ReplaySeatView,
  ReplayView,
  TileView,
} from '../core/models';
import { Account } from '../core/account';
import { ReplaySession } from '../core/replay-session';
import { Tile, describe } from '../ui/tile';
import { readError } from '../core/errors';

const GROUP_WORD: Record<HandGroupKind, string> = {
  Kang: 'kang',
  Pung: 'pung',
  Chow: 'chow',
  Pair: 'pair',
  Partial: 'needs',
  Floater: 'spare',
};

/** One run of tiles drawn together. A single block holding the whole hand when Arrange is off. */
interface HandBlock {
  key: string;
  tiles: TileView[];
  /** Shown under the block. Empty for the one-block layout and for spare tiles. */
  label: string;
  ariaLabel: string;
}

/**
 * One finished hand, stepped through a move at a time.
 *
 * Every seat is face up, which is the whole reason this screen exists, so the layout is a flat row
 * of four seats in table order rather than the three-opponents-around-you shape the live table
 * uses. There is no viewer seat here to rotate around.
 */
@Component({
  selector: 'mj-replay',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Tile, RouterLink],
  template: `
    @if (frame(); as frame) {
      <main class="replay" data-testid="replay">
        <header class="status">
          <div class="left">
            <a class="chip" [routerLink]="['/room', code(), 'replay']">All hands</a>
            <span class="hand">Hand {{ handNumber() }}</span>
          </div>

          <div class="centre">
            @if (replay()?.joker; as joker) {
              <span class="joker" data-testid="replay-joker">
                <span class="sr-only">Joker this hand:</span>
                <mj-tile [code]="joker" />
              </span>
            }
          </div>

          <div class="right">
            <span class="chip" data-testid="replay-remaining">{{ frame.tilesRemaining }} left</span>
          </div>
        </header>

        <section class="seats" data-testid="replay-seats">
          @for (seat of frame.seats; track seat.seat) {
            <article
              class="seat"
              [class.active]="seat.seat === frame.currentSeat"
              [attr.data-testid]="'replay-seat-' + seat.seat"
            >
              <div class="who">
                <span class="wind">{{ windOf(seat.seat) }}</span>
                <span class="name">{{ seat.displayName ?? 'Empty' }}</span>
                @if (seat.isBot) {
                  <em class="tag">bot</em>
                }
                @if (seat.seat === replay()?.manoSeat) {
                  <em class="tag mano">mano</em>
                }
                <span class="balance" [class.down]="seat.balance < 0">
                  {{ seat.balance > 0 ? '+' : '' }}{{ seat.balance }}
                </span>
              </div>

              <div
                class="hand-tiles"
                [class.grouped]="arranged()"
                [attr.data-testid]="'replay-seat-' + seat.seat + '-hand'"
              >
                @for (block of blocksFor(seat); track block.key) {
                  <div class="group" [attr.aria-label]="block.ariaLabel || null">
                    <div class="group-tiles">
                      @for (tile of block.tiles; track tile.id) {
                        <mj-tile [code]="tile.code" [joker]="isJoker(tile.code)" />
                      }
                    </div>

                    @if (block.label) {
                      <span class="group-label">{{ block.label }}</span>
                    }
                  </div>
                }
              </div>

              @if (seat.melds.length) {
                <div class="melds">
                  @for (meld of seat.melds; track $index) {
                    <div class="meld">
                      @for (tile of meld.tiles; track tile.id) {
                        <mj-tile [code]="tile.code" [dimmed]="meld.concealed" [joker]="isJoker(tile.code)" />
                      }
                    </div>
                  }
                </div>
              }

              @if (seat.bonus.length) {
                <div class="bonus" [attr.data-testid]="'replay-seat-' + seat.seat + '-bonus'">
                  @for (tile of seat.bonus; track tile.id) {
                    <mj-tile [code]="tile.code" />
                  }
                </div>
              }
            </article>
          }
        </section>

        <section class="pool" data-testid="replay-discards">
          @if (frame.discards.length === 0) {
            <p class="muted empty">No tiles thrown yet.</p>
          } @else {
            <div class="discards">
              @for (discard of frame.discards; track discard.tile.id) {
                <mj-tile
                  [code]="discard.tile.code"
                  [dimmed]="discard.claimed"
                  [joker]="isJoker(discard.tile.code)"
                />
              }
            </div>
          }
        </section>

        @if (frame.outcome; as outcome) {
          <section class="outcome" data-testid="replay-outcome">
            <h2>{{ outcomeTitle(frame) }}</h2>

            @if (outcome.breakdown.length) {
              <ul class="breakdown">
                @for (line of outcome.breakdown; track line.name) {
                  <li>
                    <span>{{ bonusLabel(line.name) }}</span>
                    <span class="units">{{ line.units }}</span>
                  </li>
                }
              </ul>
            }

            <p class="total">{{ outcome.totalUnits }} units</p>
          </section>
        }

        <footer class="controls">
          <p class="caption" data-testid="replay-caption">
            <span class="position">{{ index() + 1 }}/{{ total() }}</span>
            {{ frame.caption }}
          </p>

          <div class="buttons">
            <button class="btn secondary" type="button" (click)="go(0)" [disabled]="atStart()" data-testid="replay-first">
              &laquo; First
            </button>
            <button class="btn secondary" type="button" (click)="step(-1)" [disabled]="atStart()" data-testid="replay-back">
              &lsaquo; Back
            </button>
            <button class="btn" type="button" (click)="step(1)" [disabled]="atEnd()" data-testid="replay-forward">
              Forward &rsaquo;
            </button>
            <button class="btn secondary" type="button" (click)="go(total() - 1)" [disabled]="atEnd()" data-testid="replay-last">
              Last &raquo;
            </button>
            @if (canArrange()) {
              <button
                class="btn secondary"
                type="button"
                (click)="toggleArrange()"
                [attr.aria-pressed]="arranged()"
                data-testid="replay-arrange"
              >
                {{ arranged() ? 'Draw order' : 'Arrange' }}
              </button>
            }
          </div>

          <input
            class="scrub"
            type="range"
            min="0"
            [max]="total() - 1"
            [value]="index()"
            (input)="scrub($event)"
            aria-label="Position in the hand"
            data-testid="replay-scrub"
          />

          <p class="muted small">Left and right arrow keys step too.</p>
        </footer>
      </main>
    } @else if (error()) {
      <main class="wrap">
        <p class="error" data-testid="replay-error">{{ error() }}</p>
        <p class="back"><a [routerLink]="['/room', code(), 'replay']">Back to the list</a></p>
      </main>
    } @else {
      <main class="wrap">
        <p class="muted">Loading the hand...</p>
      </main>
    }
  `,
  styles: `
    .wrap {
      max-width: 560px;
      margin: 0 auto;
      padding: 32px 20px 60px;
      display: grid;
      gap: 16px;
      text-align: center;
    }

    .replay {
      min-height: 100dvh;
      display: grid;
      grid-template-rows: auto 1fr auto auto;
      gap: 12px;
      padding: 10px 12px 14px;
    }

    .status {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }

    .status .left,
    .status .right {
      display: flex;
      align-items: center;
      gap: 8px;
    }

    .chip {
      padding: 5px 10px;
      font-size: 12px;
      font-weight: 600;
      color: inherit;
      text-decoration: none;
      background: rgba(0, 0, 0, 0.3);
      border: 1px solid var(--line);
      border-radius: 20px;
    }

    .hand {
      font-size: 13px;
      font-weight: 700;
    }

    .joker {
      --tile-w: 30px;
      display: block;
    }

    .sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
    }

    /* Four seats side by side. The live table rotates the other three around the viewer; a replay
       has no viewer, so table order is the honest layout and it keeps seat 2 in the same place on
       every frame. */
    .seats {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 8px;
      align-content: start;
    }

    .seat {
      --tile-w: 30px;
      /* Grid items default to min-width auto, so without this a row that will not wrap widens the
         column past its 1fr track instead of being clipped by it. */
      min-width: 0;
      display: grid;
      gap: 6px;
      align-content: start;
      padding: 8px;
      background: rgba(0, 0, 0, 0.22);
      border: 1px solid var(--line);
      border-radius: var(--radius-sm);
    }

    .seat.active {
      border-color: var(--gold);
      background: rgba(255, 211, 92, 0.12);
    }

    .who {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
    }

    .wind {
      font-weight: 700;
      color: var(--text-dim);
    }

    .name {
      flex: 1;
      min-width: 0;
      font-weight: 600;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .balance {
      font-weight: 700;
      color: var(--ok);
    }

    .balance.down {
      color: var(--danger);
    }

    .tag {
      padding: 2px 6px;
      font-size: 10px;
      font-style: normal;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      border-radius: 20px;
      background: rgba(255, 255, 255, 0.14);
    }

    .tag.mano {
      color: var(--text-dark);
      background: var(--gold);
    }

    .hand-tiles,
    .melds,
    .bonus,
    .discards {
      display: flex;
      flex-wrap: wrap;
      gap: 2px;
    }

    .group,
    .group-tiles {
      display: flex;
      gap: 2px;
    }

    .group {
      flex-direction: column;
      gap: 1px;
      /* A block has to be allowed to shrink below its tiles' width, or the one-block layout that
         holds a whole 17-tile hand pushes the seat card over the seat next to it. The live table
         pins its blocks instead, because there the hand is one row that scrolls sideways. */
      min-width: 0;
    }

    /* Only ever wraps for the one-block layout. A real block is three or four tiles and fits. */
    .group-tiles {
      flex-wrap: wrap;
    }

    /* Arrange puts a gap between blocks. Sized off the tile so it stays proportional when the seat
       columns narrow on a phone. */
    .hand-tiles.grouped {
      gap: calc(var(--tile-w, 30px) * 0.34);
    }

    .group-label {
      font-size: 9px;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-align: center;
      white-space: nowrap;
      color: var(--text-dim);
    }

    .melds {
      gap: 6px;
      padding-top: 4px;
      border-top: 1px dashed var(--line);
    }

    .meld {
      display: flex;
      gap: 1px;
    }

    .bonus {
      --tile-w: 24px;
    }

    .pool {
      --tile-w: 28px;
      padding: 8px;
      background: rgba(0, 0, 0, 0.28);
      border-radius: var(--radius-sm);
    }

    .empty {
      margin: 0;
      text-align: center;
      font-size: 13px;
    }

    .outcome {
      padding: 10px 12px;
      background: rgba(255, 211, 92, 0.14);
      border: 1px solid var(--gold);
      border-radius: var(--radius-sm);
    }

    .outcome h2 {
      margin: 0 0 8px;
      font-size: 15px;
    }

    .breakdown {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      gap: 3px;
      font-size: 13px;
    }

    .breakdown li {
      display: flex;
      justify-content: space-between;
    }

    .units {
      font-weight: 700;
    }

    .total {
      margin: 8px 0 0;
      font-weight: 700;
    }

    .controls {
      display: grid;
      gap: 8px;
      justify-items: center;
    }

    .caption {
      margin: 0;
      text-align: center;
      font-size: 14px;
    }

    .position {
      margin-right: 8px;
      font-variant-numeric: tabular-nums;
      color: var(--text-dim);
    }

    .buttons {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      justify-content: center;
    }

    .scrub {
      width: min(100%, 520px);
    }

    .small {
      margin: 0;
      font-size: 12px;
    }

    .back {
      text-align: center;
    }

    /* One column per seat is unreadable on a phone, so they stack two by two instead. */
    @media (max-width: 720px) {
      .seats {
        grid-template-columns: repeat(2, minmax(0, 1fr));
      }
    }
  `,
})
export class ReplayPage {
  readonly code = input.required<string>();
  readonly hand = input.required<string>();

  private readonly api = inject(Api);
  private readonly account = inject(Account);
  private readonly replays = inject(ReplaySession);
  private readonly router = inject(Router);

  protected readonly replay = signal<ReplayView | null>(null);
  protected readonly index = signal(0);
  protected readonly error = signal<string | null>(null);

  /**
   * Layout mode for every hand on the frame. Not sticky, unlike the live table's: the only frames
   * that carry groups are the ones where the hand has ended, so there is nothing to remember for
   * next time.
   */
  protected readonly arranged = signal(false);

  protected readonly handNumber = computed(() => Number(this.hand()));
  protected readonly total = computed(() => this.replay()?.frames.length ?? 0);
  protected readonly frame = computed<ReplayFrameView | null>(
    () => this.replay()?.frames[this.index()] ?? null,
  );

  protected readonly atStart = computed(() => this.index() === 0);
  protected readonly atEnd = computed(() => this.index() >= this.total() - 1);

  /** The server only groups hands once they are finished, so the toggle only exists there. */
  protected readonly canArrange = computed(
    () => this.frame()?.seats.some((seat) => seat.groups.length > 0) ?? false,
  );

  constructor() {
    queueMicrotask(() => this.load());
  }

  private async load(): Promise<void> {
    // The account token stands in for the unlock token on a table this account sat at, which is
    // how a hand opened from a profile skips the password it was never asked for.
    const token = this.replays.tokenFor(this.code()) ?? this.account.token();

    if (!token) {
      // The list page is where the password gets asked for, so send them there rather than growing
      // a second copy of the same form here.
      await this.router.navigate(['/room', this.code(), 'replay']);
      return;
    }

    try {
      const replay = await this.api.getReplay(this.code(), this.handNumber(), token);

      if (replay.frames.length === 0) {
        this.error.set('This hand was played before replays were recorded, so there is nothing to step through.');
        return;
      }

      this.replay.set(replay);
    } catch (error) {
      this.replays.clear(this.code());
      this.error.set(readError(error, 'Could not open that hand.'));
    }
  }

  // ------------------------------------------------------------------ stepping

  protected go(to: number): void {
    this.index.set(Math.min(Math.max(to, 0), Math.max(this.total() - 1, 0)));
  }

  protected step(by: number): void {
    this.go(this.index() + by);
  }

  protected scrub(event: Event): void {
    this.go(Number((event.target as HTMLInputElement).value));
  }

  @HostListener('window:keydown', ['$event'])
  protected onKey(event: KeyboardEvent): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;

    // The scrub slider handles the arrows itself when it has focus. Stealing them here would move
    // two steps at a time.
    if ((event.target as HTMLElement)?.tagName === 'INPUT') return;

    this.step(event.key === 'ArrowRight' ? 1 : -1);
    event.preventDefault();
  }

  // ------------------------------------------------------------------ arrange

  protected toggleArrange(): void {
    this.arranged.set(!this.arranged());
  }

  /**
   * One seat's concealed tiles as blocks. A single block of everything when Arrange is off, so the
   * template has one shape either way.
   */
  protected blocksFor(seat: ReplaySeatView): HandBlock[] {
    // Groups are empty on every frame before the hand ends, and on a view from a server built
    // before they were sent, so draw order is what is left in both cases.
    if (!this.arranged() || seat.groups.length === 0) {
      return [{ key: 'all', tiles: seat.concealed, label: '', ariaLabel: '' }];
    }

    return seat.groups.map((group, index) => {
      const spoken = group.tiles.map((tile) => this.spoken(tile.code)).join(', ');
      const word = GROUP_WORD[group.kind];

      const label =
        group.kind === 'Partial' ? `needs ${group.needs.join('/')}` : group.kind === 'Floater' ? '' : word;

      const ariaLabel =
        group.kind === 'Partial'
          ? `${spoken}, needs ${group.needs.map((code) => this.label(code)).join(' or ')}`
          : group.kind === 'Floater'
            ? `spare, ${spoken}`
            : `${word}, ${spoken}`;

      // Indexed, because two blocks can hold the same faces - two pairs of 5 dots, say.
      return {
        key: `${index}:${group.tiles.map((tile) => tile.id).join('-')}`,
        tiles: group.tiles,
        label,
        ariaLabel,
      };
    });
  }

  // ------------------------------------------------------------------ labels

  protected windOf(seat: number): string {
    return ['East', 'South', 'West', 'North'][seat] ?? '';
  }

  protected label(code: string): string {
    return describe(code);
  }

  /** Wild this hand, so the face drawn on it is not the face it is playing as. */
  protected isJoker(code: string): boolean {
    const joker = this.replay()?.joker;
    return !!joker && code === joker;
  }

  /**
   * A tile as it is read out. A joker is named as itself and then flagged, because the group it
   * sits in is spoken as the run or the set it completes, and "8 dots" in the middle of 1-2-3
   * bamboo would otherwise sound like the reading is wrong.
   */
  protected spoken(code: string): string {
    return this.isJoker(code) ? `${describe(code)} as joker` : describe(code);
  }

  protected bonusLabel(name: string): string {
    return BONUS_LABELS[name] ?? name;
  }

  protected meldTiles(meld: MeldView): string[] {
    return meld.tiles.map((tile) => tile.code);
  }

  protected outcomeTitle(frame: ReplayFrameView): string {
    const outcome = frame.outcome;
    if (!outcome) return '';

    if (outcome.reason === 'WallExhausted') return 'Drawn hand - the wall ran out';

    const winner: ReplaySeatView | undefined =
      outcome.winnerSeat !== null ? frame.seats[outcome.winnerSeat] : undefined;

    const name = winner?.displayName ?? 'Somebody';
    return outcome.reason === 'Bisaklat' ? `${name} was dealt it - bisaklat` : `${name} declared todas`;
  }
}
