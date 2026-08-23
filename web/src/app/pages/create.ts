import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { Session } from '../core/session';

@Component({
  selector: 'mj-create',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink],
  template: `
    <main class="wrap">
      <header>
        <h1>Mahjong</h1>
        <p class="muted">Filipino rules. Four players, sixteen tiles, no accounts.</p>
      </header>

      <form class="panel" (ngSubmit)="create()" data-testid="create-form">
        <h2>Start a table</h2>

        <label class="field">
          <span>Table name</span>
          <input
            name="name"
            [(ngModel)]="name"
            required
            maxlength="60"
            placeholder="Sunday game"
            data-testid="room-name"
          />
        </label>

        <label class="field">
          <span>Password</span>
          <input
            name="password"
            [(ngModel)]="password"
            required
            minlength="4"
            maxlength="64"
            placeholder="At least 4 characters"
            data-testid="room-password"
          />
          <small class="muted">The other three players type this to sit down.</small>
        </label>

        <label class="field">
          <span>Your name</span>
          <input
            name="displayName"
            [(ngModel)]="displayName"
            required
            maxlength="24"
            placeholder="Mynard"
            data-testid="display-name"
          />
        </label>

        <!-- The one house rule worth asking about before the table exists, because it changes how
             the game is played rather than what it pays. On, the server points out every claim you
             could make and lays your hand out for you. Off, it says nothing and you call your own
             tiles, which is how it works on a real table. -->
        <div class="field toggle">
          <label class="switch">
            <input
              type="checkbox"
              name="allowHelper"
              [(ngModel)]="allowHelper"
              data-testid="allow-helper"
            />
            <span>Allow Helper</span>
          </label>
          <small class="muted">
            @if (allowHelper) {
              The table points out what you can pung, chow or kang, and sorts your hand for you.
            } @else {
              Nothing is pointed out and nothing is sorted. You press the call yourself and pick the
              tiles it costs. Press Pung or Kang and you get 10 seconds to name them.
            }
          </small>
        </div>

        <button class="btn wide" type="submit" [disabled]="busy()" data-testid="create-submit">
          {{ busy() ? 'Setting up...' : 'Create table' }}
        </button>

        @if (error()) {
          <p class="error" data-testid="create-error">{{ error() }}</p>
        }
      </form>

      @if (rejoin(); as seat) {
        <div class="panel rejoin" data-testid="rejoin">
          <p>
            You were sitting at table <strong class="mono">{{ seat.roomCode }}</strong> as
            {{ seat.displayName }}.
          </p>
          <a class="btn secondary" [routerLink]="['/room', seat.roomCode]">Go back to it</a>
        </div>
      }
    </main>
  `,
  styles: `
    .wrap {
      max-width: 460px;
      margin: 0 auto;
      padding: 40px 20px 60px;
    }

    header {
      margin-bottom: 26px;
      text-align: center;
    }

    h1 {
      font-size: 40px;
      letter-spacing: -0.02em;
    }

    header p {
      margin: 8px 0 0;
    }

    h2 {
      margin-bottom: 18px;
      font-size: 19px;
    }

    small {
      display: block;
      margin-top: 6px;
      font-size: 12.5px;
    }

    .toggle {
      display: block;
    }

    .switch {
      display: flex;
      align-items: center;
      gap: 10px;
      font-weight: 650;
      cursor: pointer;
    }

    .switch input {
      width: 18px;
      height: 18px;
      flex: 0 0 auto;
      accent-color: var(--gold);
    }

    .rejoin {
      margin-top: 18px;
    }

    .rejoin p {
      margin: 0 0 14px;
      font-size: 14px;
    }
  `,
})
export class CreatePage {
  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly router = inject(Router);

  protected name = 'Sunday game';
  protected password = '';
  protected displayName = '';

  /** Frozen into the table's rules on create and fixed for every hand played at it. */
  protected allowHelper = true;

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly rejoin = this.session.current;

  protected async create(): Promise<void> {
    if (this.busy()) return;

    const name = this.name.trim();
    const displayName = this.displayName.trim();

    if (!name || !displayName) {
      this.error.set('Give the table a name and tell us yours.');
      return;
    }

    if (this.password.length < 4) {
      this.error.set('The password needs at least 4 characters.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    try {
      const seated = await this.api.createRoom({
        name,
        password: this.password,
        displayName,
        assistEnabled: this.allowHelper,
      });

      this.session.save({
        roomCode: seated.roomCode,
        playerId: seated.playerId,
        seat: seated.seat,
        token: seated.playerToken,
        displayName,
        isHost: seated.isHost,
      });

      await this.router.navigate(['/room', seated.roomCode]);
    } catch (error: unknown) {
      this.error.set(readError(error, 'Could not create the table.'));
    } finally {
      this.busy.set(false);
    }
  }
}

/** Pulls the server's error code out of an HttpErrorResponse, falling back to a plain message. */
export function readError(error: unknown, fallback: string): string {
  const body = (error as { error?: { error?: string; detail?: string } })?.error;
  if (body?.detail) return body.detail;

  switch (body?.error) {
    case 'WrongPassword':
      return 'That password does not match this table.';
    case 'RoomNotFound':
      return 'No table with that code.';
    case 'RoomFull':
      return 'All four seats are taken.';
    case 'RoomClosed':
      return 'That table has been closed.';
    case 'NotEnoughPlayers':
      return 'All four seats have to be filled first.';
    case 'HostOnly':
      return 'Only the player who made the table can do that.';
    case 'HandInProgress':
      return 'A hand is already being played.';
    default:
      return body?.error ?? fallback;
  }
}
