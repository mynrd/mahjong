import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { Api } from '../core/api';
import { JoinForm } from './join-form';

/**
 * Where an invite link lands. The room code comes from the URL, so all the player has to supply is
 * their name and the table password.
 */
@Component({
  selector: 'mj-join',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [JoinForm],
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

      <mj-join-form [code]="code()" />

      @if (lookupError()) {
        <p class="error" data-testid="join-lookup-error">{{ lookupError() }}</p>
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
      font-size: 30px;
    }

    header p {
      margin: 8px 0 0;
    }

    .error {
      margin-top: 14px;
    }
  `,
})
export class JoinPage {
  /** Bound from the :code route parameter. */
  readonly code = input.required<string>();

  private readonly api = inject(Api);

  protected readonly roomName = signal<string | null>(null);
  protected readonly lookupError = signal<string | null>(null);

  constructor() {
    // Show the table's name so a player can tell they followed the right link, before they
    // commit to typing a password.
    queueMicrotask(async () => {
      try {
        const room = await this.api.getRoom(this.code());
        this.roomName.set(room.name);
      } catch {
        this.lookupError.set('No table with that code. Check the link.');
      }
    });
  }
}
