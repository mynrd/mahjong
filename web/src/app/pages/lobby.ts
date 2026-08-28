import { ChangeDetectionStrategy, Component, OnDestroy, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import QRCode from 'qrcode';
import { Api, apiBaseUrl } from '../core/api';
import { RoomView } from '../core/models';
import { Session } from '../core/session';
import { readError } from '../core/errors';

@Component({
  selector: 'mj-lobby',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <main class="wrap">
      <!-- Two ways this browser stops belonging here, said plainly and in place rather than by
           bouncing somebody to the home screen with no explanation. -->
      @if (gone(); as reason) {
        <section class="panel gone" data-testid="lobby-gone">
          <h1>{{ reason.title }}</h1>
          <p class="muted">{{ reason.detail }}</p>
          @if (reason.canRejoin) {
            <a class="btn" [routerLink]="['/join', code()]" data-testid="lobby-rejoin">Sit down again</a>
          }
          <a class="btn secondary" routerLink="/">Home</a>
        </section>
      } @else if (room(); as room) {
        <header>
          <h1>{{ room.name }}</h1>
          <p class="muted">
            Table <strong class="mono" data-testid="lobby-code">{{ room.code }}</strong>
            @if (room.handsPlayed > 0) {
              <span> &middot; {{ room.handsPlayed }} hand{{ room.handsPlayed === 1 ? '' : 's' }} played</span>
            }
          </p>
        </header>

        <section class="panel">
          <h2>Seats <span class="muted">{{ room.takenSeats }} of 4</span></h2>

          <ul class="seats" data-testid="seat-list">
            @for (seat of room.seats; track seat.seat) {
              <li [class.empty]="!seat.displayName" [class.you]="seat.seat === mySeat()" [attr.data-seat]="seat.seat">
                <span class="wind">{{ windOf(seat.seat) }}</span>

                @if (seat.displayName) {
                  <span class="name" [attr.data-testid]="'seat-' + seat.seat + '-name'">{{ seat.displayName }}</span>
                  <span class="tags">
                    @if (seat.isHost) {
                      <em class="tag host">host</em>
                    }
                    @if (seat.isBot) {
                      <em class="tag bot">bot</em>
                    }
                    @if (seat.seat === mySeat()) {
                      <em class="tag you">you</em>
                    }
                  </span>

                  <!-- The host's way of undoing a seat: a bot that was filled in and is not wanted
                       after all, or somebody who sat down at the wrong table. Never on the host's
                       own chair - the seat that can free chairs cannot free itself, or the table
                       would be left with nobody able to deal it. -->
                  @if (isHost() && !seat.isHost) {
                    <button
                      class="btn secondary tiny"
                      type="button"
                      (click)="remove(room, seat.seat)"
                      [disabled]="busy()"
                      [attr.aria-label]="'Remove ' + seat.displayName + ' from the table'"
                      [attr.data-testid]="'lobby-remove-' + seat.seat"
                    >
                      Remove
                    </button>
                  }
                } @else {
                  <span class="name muted" [attr.data-testid]="'seat-' + seat.seat + '-name'">waiting...</span>
                }
              </li>
            }
          </ul>
        </section>

        <section class="panel">
          <h2>Invite the others</h2>
          <p class="muted small">
            They open this on their own phone or laptop, on the same wifi, and type the password.
          </p>

          <div class="invite">
            <code class="link" data-testid="invite-url">{{ inviteUrl(room) }}</code>
            <button class="btn secondary" type="button" (click)="copy(room)" data-testid="copy-invite">
              {{ copied() ? 'Copied' : 'Copy' }}
            </button>
          </div>

          @if (qr(); as qr) {
            <img class="qr" [src]="qr" alt="QR code for the invite link" data-testid="invite-qr" />
          }
        </section>

        @if (isHost()) {
          <section class="panel">
            <h2>Host controls</h2>

            <div class="actions">
              <button
                class="btn secondary"
                type="button"
                (click)="fillWithBots(room)"
                [disabled]="room.takenSeats === 4 || busy()"
                data-testid="add-bots"
              >
                Fill empty seats with bots
              </button>

              <button
                class="btn"
                type="button"
                (click)="start(room)"
                [disabled]="!room.canStart || busy()"
                data-testid="start-hand"
              >
                {{ room.handsPlayed > 0 ? 'Deal next hand' : 'Deal' }}
              </button>
            </div>

            @if (!room.canStart) {
              <p class="muted small">All four seats have to be filled before dealing.</p>
            }

            <!-- Ending the table, from the one screen where nothing is at stake yet. Asks first:
                 it cannot be undone, everybody else is dropped with it, and it sits one row under
                 the button that deals. -->
            <div class="close-table">
              @if (closing()) {
                <p class="muted small">
                  Closing ends this table for everybody. The hands already played stay readable.
                </p>
                <div class="actions">
                  <button
                    class="btn secondary"
                    type="button"
                    (click)="closing.set(false)"
                    data-testid="lobby-close-cancel"
                  >
                    Keep the table
                  </button>
                  <button
                    class="btn danger"
                    type="button"
                    (click)="close(room)"
                    [disabled]="busy()"
                    data-testid="lobby-close-confirm"
                  >
                    Close it
                  </button>
                </div>
              } @else {
                <button
                  class="btn secondary"
                  type="button"
                  (click)="closing.set(true)"
                  data-testid="lobby-close"
                >
                  Close the table
                </button>
              }
            </div>
          </section>
        } @else {
          <p class="muted waiting" data-testid="waiting-for-host">
            Waiting for the host to deal.
          </p>
        }

        @if (error()) {
          <p class="error" data-testid="lobby-error">{{ error() }}</p>
        }
      } @else if (error()) {
        <p class="error">{{ error() }}</p>
      } @else {
        <p class="muted">Loading table...</p>
      }
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

    .seats {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      gap: 8px;
    }

    .seats li {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 12px 14px;
      background: rgba(255, 255, 255, 0.07);
      border: 1px solid var(--line);
      border-radius: var(--radius-sm);
    }

    .seats li.you {
      border-color: var(--gold);
      background: rgba(255, 211, 92, 0.12);
    }

    .seats li.empty {
      border-style: dashed;
      background: transparent;
    }

    .wind {
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

    .name {
      flex: 1;
      font-weight: 600;
    }

    .tags {
      display: flex;
      gap: 6px;
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

    .tag.you {
      color: var(--text-dark);
      background: var(--gold);
    }

    .invite {
      display: flex;
      gap: 10px;
      align-items: stretch;
      margin-top: 12px;
    }

    .link {
      flex: 1;
      min-width: 0;
      padding: 12px 13px;
      font-size: 13px;
      word-break: break-all;
      background: rgba(0, 0, 0, 0.3);
      border: 1px solid var(--line);
      border-radius: var(--radius-sm);
    }

    .qr {
      display: block;
      width: 168px;
      height: 168px;
      margin: 16px auto 0;
      border-radius: 10px;
      background: #fff;
      padding: 8px;
    }

    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
    }

    .actions .btn {
      flex: 1 1 180px;
    }

    .waiting {
      text-align: center;
      font-size: 14px;
    }

    /* Small enough to sit on a seat row without pushing the name off it, and quiet enough not to
       compete with the two buttons that actually run the table. */
    .btn.tiny {
      flex: 0 0 auto;
      padding: 5px 10px;
      font-size: 12px;
    }

    /* Under a divider rather than in the row above: closing a table is not one of the two things
       the host does every few minutes, and it must not sit next to Deal. */
    .close-table {
      display: grid;
      gap: 10px;
      margin-top: 16px;
      padding-top: 14px;
      border-top: 1px solid var(--line);
    }

    .gone {
      display: grid;
      gap: 12px;
      text-align: center;
    }

    .gone h1 {
      font-size: 22px;
    }
  `,
})
export class LobbyPage implements OnDestroy {
  readonly code = input.required<string>();

  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly router = inject(Router);

  protected readonly room = signal<RoomView | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly copied = signal(false);
  protected readonly qr = signal<string | null>(null);

  protected readonly mySeat = signal<number | null>(null);
  protected readonly isHost = signal(false);

  /** The close button's second press. Ending a table is not something to do on one tap. */
  protected readonly closing = signal(false);

  /**
   * Why this browser is no longer at this table, or null while it still is.
   *
   * Two causes and one panel: the host freed this seat, or the host closed the whole table. Both
   * are noticed by the poll rather than pushed - the lobby holds no live connection - which is why
   * the seat check below runs on every refresh rather than only when something looks wrong.
   */
  protected readonly gone = signal<GoneReason | null>(null);

  private timer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    queueMicrotask(() => this.begin());
  }

  ngOnDestroy(): void {
    this.stop();
  }

  private async begin(): Promise<void> {
    const seat = this.session.forRoom(this.code());

    if (!seat) {
      // No token for this table, so this browser has not sat down yet.
      await this.router.navigate(['/join', this.code()]);
      return;
    }

    this.mySeat.set(seat.seat);

    await this.refresh();
    await this.makeQr();

    // The lobby only changes when somebody joins or the host deals, so a short poll is enough.
    // The realtime connection is opened at the table, not here.
    this.timer = setInterval(() => void this.refresh(), 1500);
  }

  /**
   * Reads the table back, through the endpoint that answers for the token rather than the public
   * one.
   *
   * The difference matters now that a seat can be taken away: the public view of a room says
   * nothing about whether this browser is still in it, and a seat freed and then filled by
   * somebody else looks from the outside exactly like a seat that was never touched. Asking as
   * this token instead makes "you are not at this table any more" a 401 rather than a guess.
   */
  private async refresh(): Promise<void> {
    const seat = this.session.forRoom(this.code());
    if (!seat) return;

    try {
      const me = await this.api.whoAmI(this.code(), seat.token);

      this.room.set(me.room);
      this.mySeat.set(me.seat);
      this.isHost.set(me.isHost);

      // Closing does not take anybody's seat away, so the token still resolves and nothing above
      // has failed. The table is simply over, and staying on a lobby that can never deal is worse
      // than saying so.
      if (me.room.status === 'Closed') {
        this.stop();
        this.gone.set({
          title: 'This table has been closed',
          detail: 'The player who made it ended the table.',
          canRejoin: false,
        });
        return;
      }

      // The host deals for everybody, so the other three are moved to the table by this poll.
      if (me.room.status === 'Playing') {
        this.stop();
        await this.router.navigate(['/room', this.code(), 'table']);
      }
    } catch (error: unknown) {
      if (isUnauthorised(error)) {
        this.stop();
        this.session.clear();
        this.gone.set({
          title: 'You are no longer at this table',
          detail: 'The host freed your seat. It may still be open.',
          canRejoin: true,
        });
        return;
      }

      this.error.set(readError(error, 'Lost track of that table.'));
    }
  }

  private stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  /**
   * The invite URL the server builds uses whatever host it was configured with. If this page was
   * opened on a different address than that, the server's version would send other players
   * somewhere they cannot reach, so the link is rebuilt from the address actually in use.
   */
  protected inviteUrl(room: RoomView): string {
    return `${window.location.origin}/join/${room.code}`;
  }

  protected async copy(room: RoomView): Promise<void> {
    const url = this.inviteUrl(room);

    try {
      await navigator.clipboard.writeText(url);
    } catch {
      // Clipboard access needs a secure context, which plain http on a LAN is not. The link is on
      // screen to be copied by hand, so this is a nicety failing, not the feature failing.
    }

    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 1800);
  }

  private async makeQr(): Promise<void> {
    const room = this.room();
    if (!room) return;

    try {
      this.qr.set(
        await QRCode.toDataURL(this.inviteUrl(room), { margin: 1, width: 320, errorCorrectionLevel: 'M' }),
      );
    } catch {
      this.qr.set(null);
    }
  }

  protected async fillWithBots(room: RoomView): Promise<void> {
    const seat = this.session.forRoom(room.code);
    if (!seat) return;

    this.busy.set(true);
    this.error.set(null);

    try {
      this.room.set(await this.api.addBots(room.code, seat.token));
    } catch (error: unknown) {
      this.error.set(readError(error, 'Could not add bots.'));
    } finally {
      this.busy.set(false);
    }
  }

  /** Frees one seat: a bot that is not wanted after all, or somebody at the wrong table. */
  protected async remove(room: RoomView, seat: number): Promise<void> {
    const mine = this.session.forRoom(room.code);
    if (!mine) return;

    this.busy.set(true);
    this.error.set(null);

    try {
      this.room.set(await this.api.removeSeat(room.code, mine.token, seat));
    } catch (error: unknown) {
      this.error.set(readError(error, 'Could not free that seat.'));
    } finally {
      this.busy.set(false);
    }
  }

  /** Ends the table for everybody. The next poll is what puts the closed panel on screen. */
  protected async close(room: RoomView): Promise<void> {
    const mine = this.session.forRoom(room.code);
    if (!mine) return;

    this.busy.set(true);
    this.error.set(null);

    try {
      await this.api.closeRoom(room.code, mine.token);
      this.closing.set(false);
      await this.refresh();
    } catch (error: unknown) {
      this.error.set(readError(error, 'Could not close the table.'));
    } finally {
      this.busy.set(false);
    }
  }

  protected async start(room: RoomView): Promise<void> {
    const seat = this.session.forRoom(room.code);
    if (!seat) return;

    this.busy.set(true);
    this.error.set(null);

    try {
      await this.api.startHand(room.code, seat.token);
      await this.router.navigate(['/room', room.code, 'table']);
    } catch (error: unknown) {
      this.error.set(readError(error, 'Could not deal.'));
    } finally {
      this.busy.set(false);
    }
  }

  /** Seat 0 is East and play runs counter-clockwise, which is how a table names its places. */
  protected windOf(seat: number): string {
    return ['E', 'S', 'W', 'N'][seat] ?? '?';
  }
}

/** Why this browser is no longer at the table, and whether there is a way back in. */
interface GoneReason {
  title: string;
  detail: string;
  canRejoin: boolean;
}

/**
 * Whether the server refused the token rather than failing some other way.
 *
 * Only a 401 means the seat is gone. A 404, a timeout or a dropped wifi connection all have to
 * leave the lobby where it is: telling somebody they have been removed because their phone lost
 * signal for two seconds would be worse than the silence it replaces.
 */
function isUnauthorised(error: unknown): boolean {
  return (error as { status?: number })?.status === 401;
}

/** Kept next to the lobby so the base URL is reachable for tests without importing the service. */
export const API_BASE = apiBaseUrl;
