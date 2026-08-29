import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Account } from '../core/account';
import { Api } from '../core/api';
import { readError } from '../core/errors';

/** Which half of the same form is showing. Bound from the route, so /register opens on register. */
export type AccountMode = 'register' | 'signIn';

/** Mirrors UserName.IsWellFormed on the server, so the same names are refused on both sides. */
const USERNAME = /^[A-Za-z0-9._-]{3,24}$/;

/** Mirrors UserName.MinPasswordLength. Longer than a table password: this one outlives the evening. */
const MIN_PASSWORD = 8;

/**
 * Registering an account, and signing back in to one.
 *
 * Registering is optional and always will be: a table still works with nobody signed in, and this
 * page is reached from a link on the start page rather than standing in front of it. What it buys
 * is that the hands you play are recorded against a name that outlives the table, so they can be
 * read back afterwards from one profile instead of from six-character room codes nobody kept.
 *
 * Usernames are first come, first served. The server settles that on a unique index, so the answer
 * to two people claiming the same name in the same second is decided there and shown here.
 */
@Component({
  selector: 'mj-account',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink],
  template: `
    <main class="wrap">
      <header>
        <h1>{{ registering() ? 'Register' : 'Sign in' }}</h1>
        <p class="muted">
          @if (registering()) {
            Pick a name and keep your games. Usernames are first come, first served.
          } @else {
            Welcome back. Your hands are where you left them.
          }
        </p>
      </header>

      <div class="modes" role="tablist" aria-label="Register or sign in">
        <button
          type="button"
          role="tab"
          [attr.aria-selected]="registering()"
          [class.on]="registering()"
          (click)="switchTo('register')"
          data-testid="mode-register"
        >
          Register
        </button>
        <button
          type="button"
          role="tab"
          [attr.aria-selected]="!registering()"
          [class.on]="!registering()"
          (click)="switchTo('signIn')"
          data-testid="mode-signin"
        >
          Sign in
        </button>
      </div>

      <form class="panel" (ngSubmit)="submit()" data-testid="account-form">
        <label class="field">
          <span>Username</span>
          <input
            name="username"
            [(ngModel)]="username"
            required
            maxlength="24"
            autocapitalize="none"
            autocomplete="username"
            spellcheck="false"
            placeholder="mynard"
            data-testid="account-username"
          />
          @if (registering()) {
            <small class="muted">3 to 24 characters. Letters, numbers, and . _ - only.</small>
          }
        </label>

        <label class="field">
          <span>Password</span>
          <div class="reveal">
            <input
              [type]="shown() ? 'text' : 'password'"
              name="password"
              [(ngModel)]="password"
              required
              maxlength="128"
              [attr.autocomplete]="registering() ? 'new-password' : 'current-password'"
              placeholder="{{ registering() ? 'At least 8 characters' : 'Your password' }}"
              data-testid="account-password"
            />
            <button
              type="button"
              (click)="toggleShown()"
              [attr.aria-pressed]="shown()"
              [attr.aria-label]="shown() ? 'Hide password' : 'Show password'"
              [attr.title]="shown() ? 'Hide password' : 'Show password'"
              data-testid="account-password-reveal"
            >
              @if (shown()) {
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path
                    d="M2 12s3.8-6.5 10-6.5c2 0 3.7.7 5.1 1.6M22 12s-3.8 6.5-10 6.5c-2.1 0-3.9-.7-5.3-1.7"
                  />
                  <path d="M9.9 9.9a3 3 0 0 0 4.2 4.2" />
                  <path d="m3.5 3.5 17 17" />
                </svg>
              } @else {
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M2 12s3.8-6.5 10-6.5S22 12 22 12s-3.8 6.5-10 6.5S2 12 2 12Z" />
                  <circle cx="12" cy="12" r="2.8" />
                </svg>
              }
            </button>
          </div>
        </label>

        <button class="btn wide" type="submit" [disabled]="busy()" data-testid="account-submit">
          @if (busy()) {
            {{ registering() ? 'Claiming the name...' : 'Signing in...' }}
          } @else {
            {{ registering() ? 'Create account' : 'Sign in' }}
          }
        </button>

        @if (error()) {
          <p class="error" data-testid="account-error">{{ error() }}</p>
        }
      </form>

      <p class="back">
        <a routerLink="/">Back to the tables</a>
      </p>
    </main>
  `,
  styles: `
    .wrap {
      max-width: 460px;
      margin: 0 auto;
      padding: 40px 20px 60px;
    }

    header {
      margin-bottom: 22px;
      text-align: center;
    }

    h1 {
      font-size: 32px;
    }

    header p {
      margin: 8px 0 0;
    }

    /* Two halves of one control, matching the start page: a choice between them, not two
       buttons that each go somewhere. */
    .modes {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 4px;
      margin-bottom: 16px;
      padding: 4px;
      background: rgba(0, 0, 0, 0.28);
      border: 1px solid var(--line);
      border-radius: var(--radius);
    }

    .modes button {
      min-height: 44px;
      padding: 10px 12px;
      font-size: 15px;
      font-weight: 650;
      color: var(--text-dim);
      background: none;
      border: none;
      border-radius: var(--radius-sm);
    }

    .modes button.on {
      color: var(--text-dark);
      background: linear-gradient(var(--gold), var(--gold-deep));
    }

    .modes button:focus-visible {
      outline: 2px solid var(--gold);
      outline-offset: 1px;
    }

    /* The field keeps its full width; the eye sits over its right end rather than stealing
       columns from it, so the password box is the same size as the username box above it. */
    .reveal {
      position: relative;
    }

    .reveal input {
      padding-right: 52px;
    }

    .reveal button {
      position: absolute;
      top: 0;
      right: 0;
      display: grid;
      place-items: center;
      width: 48px;
      height: 100%;
      padding: 0;
      color: var(--text-dim);
      background: none;
      border: none;
      border-radius: var(--radius-sm);
    }

    .reveal button:hover {
      color: var(--text);
    }

    .reveal button:focus-visible {
      outline: 2px solid var(--gold);
      outline-offset: -2px;
    }

    .reveal svg {
      width: 20px;
      height: 20px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.7;
      stroke-linecap: round;
      stroke-linejoin: round;
    }

    small {
      display: block;
      margin-top: 6px;
      font-size: 12.5px;
    }

    .back {
      margin-top: 18px;
      text-align: center;
    }
  `,
})
export class AccountPage {
  /** Bound from the route's data, so /register and /sign-in are the same page opened two ways. */
  readonly mode = input<AccountMode>('register');

  private readonly api = inject(Api);
  private readonly account = inject(Account);
  private readonly router = inject(Router);

  /**
   * Starts from the route and is then owned by the tabs, so switching does not need a navigation
   * and does not lose what has already been typed into the two fields both halves share.
   */
  private readonly chosen = signal<AccountMode | null>(null);

  protected username = '';
  protected password = '';

  /** Whether the password is being shown as text. Off again the moment the form is submitted. */
  protected readonly shown = signal(false);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected registering(): boolean {
    return (this.chosen() ?? this.mode()) === 'register';
  }

  protected toggleShown(): void {
    this.shown.update((shown) => !shown);
  }

  protected switchTo(mode: AccountMode): void {
    this.chosen.set(mode);
    this.error.set(null);
  }

  protected async submit(): Promise<void> {
    if (this.busy()) return;

    const username = this.username.trim();
    const registering = this.registering();

    if (!username) {
      this.error.set('Put in a username.');
      return;
    }

    // Checked here only to save a round trip on the obvious cases. The server checks the same
    // things again, and it is the one that decides.
    if (registering && !USERNAME.test(username)) {
      this.error.set('Between 3 and 24 characters: letters, numbers, and . _ - only.');
      return;
    }

    if (registering && this.password.length < MIN_PASSWORD) {
      this.error.set(`The password needs at least ${MIN_PASSWORD} characters.`);
      return;
    }

    if (!this.password) {
      this.error.set('Put in your password.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    try {
      const body = { username, password: this.password };

      this.account.save(registering ? await this.api.register(body) : await this.api.signIn(body));
      this.password = '';
      this.shown.set(false);

      await this.router.navigate(['/me']);
    } catch (error: unknown) {
      this.error.set(
        readError(error, registering ? 'Could not create the account.' : 'Could not sign in.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
