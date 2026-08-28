import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Account } from '../core/account';
import { Api } from '../core/api';
import { readError } from '../core/errors';
import { PlayedGameView, ProfileView } from '../core/models';

/**
 * One player's profile: who they are, and every finished hand they had a seat in.
 *
 * The list is assembled from the seats this account took rather than from anything written when a
 * hand ends, so it holds whatever the server still has - including hands played at a table that
 * has since been closed, which is most of them by the time anybody comes looking.
 *
 * Every hand that was recorded frame by frame links to its replay. A signed-in player does not
 * have to remember the table password to open one of their own: the server takes the account token
 * as proof for the rooms that account actually sat in.
 */
@Component({
  selector: 'mj-profile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, RouterLink],
  template: `
    <main class="wrap">
      @if (profile(); as me) {
        <header>
          <h1 data-testid="profile-username">{{ me.username }}</h1>
          <p class="muted">Playing since {{ since(me.createdAt) }}</p>
        </header>

        <section class="panel stats" data-testid="profile-stats">
          <div>
            <strong>{{ me.stats.handsPlayed }}</strong>
            <span class="muted">hands</span>
          </div>
          <div>
            <strong>{{ me.stats.handsWon }}</strong>
            <span class="muted">won</span>
          </div>
          <div>
            <strong [class.up]="me.stats.netUnits > 0" [class.down]="me.stats.netUnits < 0">
              {{ signed(me.stats.netUnits) }}
            </strong>
            <span class="muted">units</span>
          </div>
          <div>
            <strong>{{ me.stats.tables }}</strong>
            <span class="muted">{{ me.stats.tables === 1 ? 'table' : 'tables' }}</span>
          </div>
        </section>

        @if (me.games.length === 0) {
          <p class="muted empty" data-testid="profile-empty">
            No finished hands yet. Start or join a table while signed in and every hand played there
            shows up here.
          </p>
        } @else {
          <section class="panel">
            <h2>
              Your games
              <span class="muted">{{ me.games.length }}</span>
            </h2>

            <ul class="games" data-testid="profile-games">
              @for (game of me.games; track game.roomCode + '#' + game.handNumber) {
                <li [class.won]="game.youWon">
                  @if (game.canReplay) {
                    <a
                      [routerLink]="['/room', game.roomCode, 'replay', game.handNumber]"
                      [attr.data-testid]="'profile-game-' + game.roomCode + '-' + game.handNumber"
                    >
                      <ng-container *ngTemplateOutlet="row; context: { $implicit: game }" />
                    </a>
                  } @else {
                    <div class="norecord">
                      <ng-container *ngTemplateOutlet="row; context: { $implicit: game }" />
                    </div>
                  }
                </li>
              }
            </ul>
          </section>
        }
      } @else if (error()) {
        <p class="error" data-testid="profile-error">{{ error() }}</p>
      } @else {
        <p class="muted empty">Loading your games...</p>
      }

      <p class="back">
        <a routerLink="/">Back to the tables</a>
        @if (profile()) {
          <button type="button" class="link" (click)="signOut()" data-testid="profile-signout">
            Sign out
          </button>
        }
      </p>
    </main>

    <!-- One row, drawn the same whether it is a link to the replay or a dead entry for a hand from
         before frames were recorded. Two copies of this would drift apart the first time a column
         was added. -->
    <ng-template #row let-game>
      <span class="result" [class.win]="game.youWon">{{ game.youWon ? 'Won' : '' }}</span>

      <span class="detail">
        <span class="who">{{ describe(game) }}</span>
        <span class="when muted small">
          {{ game.roomName }} &middot; hand {{ game.handNumber }} &middot; {{ when(game) }}
          @if (!game.canReplay) {
            &middot; not recorded
          }
        </span>
      </span>

      <span class="units" [class.up]="game.yourDelta > 0" [class.down]="game.yourDelta < 0">
        {{ signed(game.yourDelta) }}
      </span>
    </ng-template>
  `,
  styles: `
    .wrap {
      max-width: 620px;
      margin: 0 auto;
      padding: 32px 20px 60px;
      display: grid;
      gap: 16px;
    }

    header {
      text-align: center;
    }

    h1 {
      font-size: 30px;
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

    .stats {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 8px;
      text-align: center;
    }

    .stats strong {
      display: block;
      font-size: 22px;
      font-weight: 700;
    }

    .stats span {
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: 0.06em;
    }

    .games {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      gap: 8px;
    }

    .games a,
    .games .norecord {
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

    .games .norecord {
      opacity: 0.72;
    }

    .games a:hover,
    .games a:focus-visible {
      border-color: var(--gold);
      background: rgba(255, 211, 92, 0.12);
    }

    .result {
      width: 44px;
      flex: 0 0 auto;
      font-size: 11px;
      font-weight: 700;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      color: var(--text-dim);
    }

    .result.win {
      color: var(--gold);
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
      color: var(--text-dim);
    }

    .up {
      color: var(--ok);
    }

    .down {
      color: var(--danger);
    }

    .empty,
    .back {
      text-align: center;
    }

    .back {
      display: flex;
      justify-content: center;
      gap: 18px;
    }

    /* A button, because it acts on this browser rather than going anywhere - but it belongs
       beside the link, so it is drawn as one. */
    .link {
      padding: 0;
      color: var(--text-dim);
      background: none;
      border: none;
      text-decoration: underline;
    }
  `,
})
export class ProfilePage {
  private readonly api = inject(Api);
  private readonly account = inject(Account);
  private readonly router = inject(Router);

  protected readonly profile = signal<ProfileView | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    queueMicrotask(() => this.load());
  }

  private async load(): Promise<void> {
    const token = this.account.token();

    if (!token) {
      await this.router.navigate(['/sign-in']);
      return;
    }

    try {
      this.profile.set(await this.api.profile(token));
    } catch (error: unknown) {
      // The token expired, or the account was removed under it. Either way this browser is not
      // signed in any more, so say so rather than showing an empty profile.
      this.account.clear();
      this.error.set(readError(error, 'Could not load your games.'));
    }
  }

  protected async signOut(): Promise<void> {
    const token = this.account.token();
    this.account.clear();

    try {
      if (token) await this.api.signOut(token);
    } catch {
      // The local copy is already gone, which is what signing out means to this browser.
    }

    await this.router.navigate(['/']);
  }

  protected describe(game: PlayedGameView): string {
    if (game.youWon) return 'You won';
    if (game.reason === 'WallExhausted') return 'Wall ran out, nobody won';
    if (game.winnerName) return `${game.winnerName} won`;
    if (game.winnerSeat !== null) return `Seat ${game.winnerSeat} won`;
    return game.reason;
  }

  protected when(game: PlayedGameView): string {
    return new Date(game.endedAt ?? game.startedAt).toLocaleDateString();
  }

  protected since(iso: string): string {
    return new Date(iso).toLocaleDateString();
  }

  /** A settlement reads as a movement, so the sign is always shown - including on a plain zero. */
  protected signed(units: number): string {
    return units > 0 ? `+${units}` : `${units}`;
  }
}
