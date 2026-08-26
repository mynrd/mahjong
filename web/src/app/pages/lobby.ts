import { ChangeDetectionStrategy, Component, OnDestroy, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import QRCode from 'qrcode';
import { Api, apiBaseUrl } from '../core/api';
import { RoomView } from '../core/models';
import { Session } from '../core/session';
import { readError } from '../core/errors';

@Component({
  selector: 'mj-lobby',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="wrap">
      @if (room(); as room) {
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

  private timer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    queueMicrotask(() => this.begin());
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
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

  private async refresh(): Promise<void> {
    try {
      const room = await this.api.getRoom(this.code());
      this.room.set(room);

      const seat = this.session.forRoom(this.code());
      const mine = room.seats.find((s) => s.seat === seat?.seat);
      this.isHost.set(mine?.isHost ?? false);

      // The host deals for everybody, so the other three are moved to the table by this poll.
      if (room.status === 'Playing') {
        if (this.timer) clearInterval(this.timer);
        await this.router.navigate(['/room', this.code(), 'table']);
      }
    } catch (error: unknown) {
      this.error.set(readError(error, 'Lost track of that table.'));
    }
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

/** Kept next to the lobby so the base URL is reachable for tests without importing the service. */
export const API_BASE = apiBaseUrl;
