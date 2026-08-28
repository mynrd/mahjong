import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Account } from '../core/account';
import { Api } from '../core/api';
import { ReplayListItemView } from '../core/models';
import { ReplaySession } from '../core/replay-session';
import { readError } from '../core/errors';

/**
 * The finished hands of one table.
 *
 * Gated on the room password rather than on holding a seat: this link gets opened on a laptop that
 * never played, and the password is the only thing everyone at the table already knows.
 */
@Component({
  selector: 'mj-replay-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink],
  template: `
    <main class="wrap">
      <header>
        <h1>Replays</h1>
        <p class="muted">
          Table <strong class="mono" data-testid="replay-code">{{ code() }}</strong>
        </p>
      </header>

      @if (!unlocked()) {
        <section class="panel">
          <h2>Password</h2>
          <p class="muted small">The same password the table used to sit down.</p>

          <form (ngSubmit)="unlock()">
            <input
              class="field"
              type="password"
              name="password"
              autocomplete="current-password"
              placeholder="Table password"
              [(ngModel)]="password"
              [disabled]="busy()"
              data-testid="replay-password"
            />

            <button class="btn" type="submit" [disabled]="busy() || !password" data-testid="replay-unlock">
              {{ busy() ? 'Checking...' : 'Show replays' }}
            </button>
          </form>
        </section>
      } @else if (hands().length === 0) {
        <p class="muted empty" data-testid="replay-empty">
          No hand at this table has finished yet. A hand shows up here once somebody wins or the
          wall runs out.
        </p>
      } @else {
        <section class="panel">
          <h2>
            Finished hands
            <span class="muted">{{ hands().length }}</span>
          </h2>

          <ul class="hands" data-testid="replay-list">
            @for (hand of hands(); track hand.handNumber) {
              <li>
                <a [routerLink]="['/room', code(), 'replay', hand.handNumber]" [attr.data-testid]="'replay-hand-' + hand.handNumber">
                  <span class="number">{{ hand.handNumber }}</span>

                  <span class="detail">
                    <span class="who">{{ describe(hand) }}</span>
                    <span class="when muted small">{{ when(hand) }}</span>
                  </span>

                  @if (hand.frameCount === 0) {
                    <em class="tag warn">not recorded</em>
                  } @else {
                    <span class="units" [class.zero]="hand.totalUnits === 0">
                      {{ hand.totalUnits }} <span class="muted small">units</span>
                    </span>
                  }
                </a>
              </li>
            }
          </ul>
        </section>
      }

      @if (error()) {
        <p class="error" data-testid="replay-error">{{ error() }}</p>
      }

      <p class="back">
        <a [routerLink]="['/room', code()]">Back to the table</a>
      </p>
    </main>
  `,
  styles: `
    .wrap {
      max-width: 560px;
      margin: 0 auto;
      padding: 32px 20px 60px;
      display: grid;
      gap: 16px;
    }

    header {
      text-align: center;
    }

    h1 {
      font-size: 28px;
    }

    header p {
      margin: 6px 0 0;
    }

    h2 {
      display: flex;
      justify-content: space-between;
      align-items: baseline;
      margin-bottom: 14px;
      font-size: 16px;
    }

    h2 .muted {
      font-size: 13px;
      font-weight: 500;
    }

    .small {
      font-size: 13px;
    }

    form {
      display: grid;
      gap: 10px;
    }

    .hands {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      gap: 8px;
    }

    .hands a {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 12px 14px;
      color: inherit;
      text-decoration: none;
      background: rgba(255, 255, 255, 0.07);
      border: 1px solid var(--line);
      border-radius: var(--radius-sm);
    }

    .hands a:hover,
    .hands a:focus-visible {
      border-color: var(--gold);
      background: rgba(255, 211, 92, 0.12);
    }

    .number {
      width: 30px;
      height: 30px;
      display: grid;
      place-items: center;
      flex: 0 0 auto;
      font-size: 13px;
      font-weight: 700;
      background: rgba(0, 0, 0, 0.35);
      border-radius: 50%;
    }

    .detail {
      flex: 1;
      min-width: 0;
      display: grid;
    }

    .who {
      font-weight: 600;
    }

    .units {
      flex: 0 0 auto;
      font-weight: 700;
      color: var(--gold);
    }

    .units.zero {
      color: var(--text-dim);
      font-weight: 500;
    }

    .tag {
      padding: 3px 8px;
      font-size: 11px;
      font-style: normal;
      font-weight: 700;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      border-radius: 20px;
      background: rgba(255, 255, 255, 0.14);
    }

    .tag.warn {
      color: var(--text-dark);
      background: var(--gold-deep);
    }

    .empty,
    .back {
      text-align: center;
    }
  `,
})
export class ReplayListPage {
  readonly code = input.required<string>();

  private readonly api = inject(Api);
  private readonly account = inject(Account);
  private readonly replays = inject(ReplaySession);

  protected password = '';

  protected readonly hands = signal<ReplayListItemView[]>([]);
  protected readonly unlocked = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    queueMicrotask(() => this.resume());
  }

  /**
   * Opens the list without asking, when this browser already holds something that will do: the
   * unlock token from typing the password earlier in this tab, or an account token, which the
   * server accepts for the rooms that account actually sat in. Somebody who played the hand should
   * not have to remember the table password to read their own game back.
   */
  private async resume(): Promise<void> {
    const token = this.replays.tokenFor(this.code()) ?? this.account.token();
    if (!token) return;

    try {
      await this.load(token);
    } catch {
      // Expired, the password was changed under it, or this account never sat at this table.
      // Falls back to the form.
      this.replays.clear(this.code());
    }
  }

  protected async unlock(): Promise<void> {
    if (!this.password) return;

    this.busy.set(true);
    this.error.set(null);

    try {
      const { token } = await this.api.unlockReplays(this.code(), this.password);
      this.replays.save(this.code(), token);
      this.password = '';
      await this.load(token);
    } catch (error) {
      this.error.set(readError(error, 'Could not open the replays.'));
    } finally {
      this.busy.set(false);
    }
  }

  private async load(token: string): Promise<void> {
    this.hands.set(await this.api.listReplays(this.code(), token));
    this.unlocked.set(true);
  }

  protected describe(hand: ReplayListItemView): string {
    if (hand.reason === 'WallExhausted') return 'Wall ran out, nobody won';
    if (hand.winnerName) return `${hand.winnerName} won`;
    if (hand.winnerSeat !== null) return `Seat ${hand.winnerSeat} won`;
    return hand.reason;
  }

  protected when(hand: ReplayListItemView): string {
    const ended = hand.endedAt ?? hand.startedAt;
    return new Date(ended).toLocaleString();
  }
}
