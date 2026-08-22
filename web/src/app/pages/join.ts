import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api } from '../core/api';
import { Session } from '../core/session';
import { readError } from './create';

/**
 * Where an invite link lands. The room code comes from the URL, so all the player has to supply is
 * their name and the table password.
 */
@Component({
  selector: 'mj-join',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  template: `
    <main class="wrap">
      <header>
        <h1>Join the table</h1>
        <p class="muted">
          Table <strong class="mono" data-testid="join-code">{{ code().toUpperCase() }}</strong>
          @if (roomName()) {
            <span> &middot; {{ roomName() }}</span>
          }
        </p>
      </header>

      <form class="panel" (ngSubmit)="join()" data-testid="join-form">
        <label class="field">
          <span>Your name</span>
          <input
            name="displayName"
            [(ngModel)]="displayName"
            required
            maxlength="24"
            placeholder="Tito Ben"
            data-testid="join-name"
          />
        </label>

        <label class="field">
          <span>Table password</span>
          <input
            name="password"
            [(ngModel)]="password"
            required
            maxlength="64"
            placeholder="Ask whoever sent the link"
            data-testid="join-password"
          />
        </label>

        <button class="btn wide" type="submit" [disabled]="busy()" data-testid="join-submit">
          {{ busy() ? 'Sitting down...' : 'Take a seat' }}
        </button>

        @if (error()) {
          <p class="error" data-testid="join-error">{{ error() }}</p>
        }
      </form>
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
      font-size: 30px;
    }

    header p {
      margin: 8px 0 0;
    }
  `,
})
export class JoinPage {
  /** Bound from the :code route parameter. */
  readonly code = input.required<string>();

  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly router = inject(Router);

  protected displayName = '';
  protected password = '';

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly roomName = signal<string | null>(null);

  constructor() {
    // Show the table's name so a player can tell they followed the right link, before they
    // commit to typing a password.
    queueMicrotask(async () => {
      try {
        const room = await this.api.getRoom(this.code());
        this.roomName.set(room.name);
      } catch {
        this.error.set('No table with that code. Check the link.');
      }
    });
  }

  protected async join(): Promise<void> {
    if (this.busy()) return;

    const displayName = this.displayName.trim();
    if (!displayName) {
      this.error.set('Put in a name so the others know who sat down.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    try {
      const seated = await this.api.joinRoom(this.code(), { displayName, password: this.password });

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
      this.error.set(readError(error, 'Could not sit down at that table.'));
    } finally {
      this.busy.set(false);
    }
  }
}
