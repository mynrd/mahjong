import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api } from '../core/api';
import { readError } from '../core/errors';
import { Session } from '../core/session';

/** What the server generates. Six characters, no O/0 or I/1 lookalikes among them. */
const CODE_LENGTH = 6;

/** Mirrors RoomCode.Normalise on the server, so a code typed with spaces or in lower case matches. */
function normalise(input: string): string {
  return Array.from(input)
    .filter((c) => /[a-z0-9]/i.test(c))
    .join('')
    .toUpperCase();
}

/**
 * Taking a seat at a table that already exists.
 *
 * Used two ways. An invite link knows the table, so it passes the code in and the player only
 * fills in a name and the password. The start page does not, so it leaves the code unset and the
 * form asks for it as well - that is the whole difference between the two.
 */
@Component({
  selector: 'mj-join-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  template: `
    <form class="panel" (ngSubmit)="join()" data-testid="join-form">
      @if (!code()) {
        <label class="field">
          <span>Table code</span>
          <input
            name="tableCode"
            class="mono code"
            [(ngModel)]="typedCode"
            required
            maxlength="12"
            autocapitalize="characters"
            autocomplete="off"
            spellcheck="false"
            placeholder="ABC234"
            data-testid="join-code-input"
          />
          <small class="muted">The six characters the host reads off their screen.</small>
        </label>
      }

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
          placeholder="Ask whoever set the table up"
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
  `,
  styles: `
    small {
      display: block;
      margin-top: 6px;
      font-size: 12.5px;
    }

    .code {
      text-transform: uppercase;
    }
  `,
})
export class JoinForm {
  /** The table to sit at, when the link already names one. Left unset, the form asks for it. */
  readonly code = input<string | null>(null);

  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly router = inject(Router);

  protected typedCode = '';
  protected displayName = '';
  protected password = '';

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async join(): Promise<void> {
    if (this.busy()) return;

    const code = normalise(this.code() ?? this.typedCode);
    const displayName = this.displayName.trim();

    if (code.length !== CODE_LENGTH) {
      this.error.set('A table code is six letters and numbers.');
      return;
    }

    if (!displayName) {
      this.error.set('Put in a name so the others know who sat down.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    try {
      const seated = await this.api.joinRoom(code, { displayName, password: this.password });

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
