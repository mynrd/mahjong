import { Injectable, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { apiBaseUrl } from './api';
import { ClaimKind, MoveResult, PlayerGameView } from './models';

export type ConnectionState = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'closed';

/**
 * Why the last move was refused.
 *
 * The code is the server naming the rule rather than describing it, which is what lets the table
 * draw the refusal instead of printing it: told `CannotClaim` on a pung, the dialog can put up the
 * two tiles the call would have needed. `detail` is the server's own sentence, for everything the
 * client has nothing better to say about.
 */
export interface MoveFailure {
  code: string;
  detail: string;
}

/** Something that just happened at the table, shown briefly so players can follow along. */
export interface TableMessage {
  id: number;
  text: string;
}

/**
 * The live connection to a table.
 *
 * State handling is deliberately latest-wins: whatever view arrives most recently replaces what
 * came before, and nothing is queued or dropped. Three bots can move in quick succession while the
 * player is still tapping, and an approach that ignored updates arriving during an in-flight action
 * would lose the one update that mattered - the one saying it is your turn again - and the table
 * would sit there looking finished.
 */
@Injectable({ providedIn: 'root' })
export class Game {
  readonly view = signal<PlayerGameView | null>(null);
  readonly connection = signal<ConnectionState>('idle');
  readonly lastError = signal<string | null>(null);
  readonly lastFailure = signal<MoveFailure | null>(null);
  readonly messages = signal<TableMessage[]>([]);

  /** Why this browser no longer holds a seat, once it no longer holds one. */
  readonly removed = signal<string | null>(null);

  private hub: signalR.HubConnection | null = null;
  private messageId = 0;

  async connect(token: string): Promise<void> {
    await this.disconnect();

    this.connection.set('connecting');
    this.removed.set(null);

    const hub = new signalR.HubConnectionBuilder()
      .withUrl(`${apiBaseUrl()}/hubs/game?access_token=${encodeURIComponent(token)}`)
      // A phone that sleeps, a wifi handover, a laptop lid: all normal, all recoverable. The seat
      // is held server-side, so reconnecting picks the hand back up exactly where it was.
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    hub.on('StateChanged', (view: PlayerGameView) => this.view.set(view));

    // The seat is gone from under this browser: left of its own accord, or freed by the host. The
    // token stops resolving the moment the row goes, so the page has to be told rather than left to
    // discover it as a string of failures.
    hub.on('Removed', (reason: string) => this.removed.set(reason));

    hub.on('SeatConnected', (_seat: number, name: string) => this.say(`${name} connected`));
    hub.on('SeatDisconnected', (seat: number) => {
      const name = this.view()?.seats[seat]?.displayName ?? `Seat ${seat}`;
      this.say(`${name} lost connection`);
    });

    hub.onreconnecting(() => this.connection.set('reconnecting'));
    hub.onreconnected(() => this.connection.set('connected'));
    hub.onclose(() => this.connection.set('closed'));

    this.hub = hub;

    await hub.start();
    this.connection.set('connected');
  }

  async disconnect(): Promise<void> {
    const hub = this.hub;
    this.hub = null;

    if (hub) await hub.stop().catch(() => undefined);

    this.connection.set('idle');
  }

  // ---------------------------------------------------------------- moves

  discard(tileId: number): Promise<boolean> {
    return this.invoke('Discard', tileId);
  }

  claim(kind: ClaimKind, tileIds: number[] = []): Promise<boolean> {
    return this.invoke('Claim', kind, tileIds);
  }

  pass(): Promise<boolean> {
    return this.invoke('Pass');
  }

  /**
   * Takes back a call that was pressed but not yet paid for, putting this seat back where it was
   * before pressing: the discard is still there to call, pass or draw through. Only ever reachable
   * with assist off, where pressing a call is a guess made before counting your own tiles.
   */
  withdraw(): Promise<boolean> {
    return this.invoke('Withdraw');
  }

  /**
   * Takes a tile off the wall. Always a button press, never automatic: a tile that appeared in
   * your hand by itself while you were still reading the last discard is a tile you did not see
   * arrive. It is also what ends a claim window with no deadline, for the seat due to play next.
   */
  draw(): Promise<boolean> {
    return this.invoke('Draw');
  }

  declareSecretKang(face: string): Promise<boolean> {
    return this.invoke('DeclareSecretKang', face);
  }

  declareSagasa(face: string): Promise<boolean> {
    return this.invoke('DeclareSagasa', face);
  }

  declareTodas(): Promise<boolean> {
    return this.invoke('DeclareTodas');
  }

  /**
   * Turns your hand face up for the other three, once the hand is over. There is no way back: the
   * table has seen them by the time you could press anything else, and the next deal clears it.
   */
  reveal(): Promise<boolean> {
    return this.invoke('Reveal');
  }

  // ---------------------------------------------------------------- the next game

  /** Host only: offer another game to the table. It deals when all four seats have said yes. */
  proposeNewGame(): Promise<boolean> {
    return this.invoke('ProposeNewGame');
  }

  /** Host only: take the offer back. */
  cancelNewGame(): Promise<boolean> {
    return this.invoke('CancelNewGame');
  }

  /** Host only: sit a bot in every seat still empty. */
  fillWithBots(): Promise<boolean> {
    return this.invoke('FillWithBots');
  }

  /** Host only: free the seat of somebody who has stopped answering. */
  removeSeat(seat: number): Promise<boolean> {
    return this.invoke('RemoveSeat', seat);
  }

  acceptNewGame(): Promise<boolean> {
    return this.invoke('AcceptNewGame');
  }

  /** Says no, and leaves: there is no third state where you sit at a table you declined to play. */
  declineNewGame(): Promise<boolean> {
    return this.invoke('DeclineNewGame');
  }

  // ---------------------------------------------------------------- hand arrangement

  /**
   * How this seat has laid its own tiles out, as groups of tile ids.
   *
   * Cosmetic all the way down: the server stores the value and hands it back, nothing else reads
   * it and no other seat is told. It is kept server-side rather than in the browser because a
   * phone that sleeps mid-hand reloads the page, and losing a grouping you built by hand every
   * time the screen locks makes the feature not worth using.
   *
   * Both calls swallow their errors for the same reason: a drawing preference is never worth
   * putting an error in front of somebody who is mid-hand.
   */
  async getArrangement(): Promise<number[][]> {
    const hub = this.hub;
    if (hub?.state !== signalR.HubConnectionState.Connected) return [];

    try {
      return (await hub.invoke<number[][]>('GetArrangement')) ?? [];
    } catch {
      return [];
    }
  }

  async saveArrangement(groups: readonly (readonly number[])[]): Promise<void> {
    const hub = this.hub;
    if (hub?.state !== signalR.HubConnectionState.Connected) return;

    try {
      await hub.invoke('SaveArrangement', groups);
    } catch {
      // Nothing to recover: the next change queues another save.
    }
  }

  // ---------------------------------------------------------------- internals

  private async invoke(method: string, ...args: unknown[]): Promise<boolean> {
    const hub = this.hub;
    if (!hub || hub.state !== signalR.HubConnectionState.Connected) {
      this.fail({ code: 'NotConnected', detail: 'Not connected to the table.' });
      return false;
    }

    try {
      const result = await hub.invoke<MoveResult>(method, ...args);

      if (!result?.success) {
        // Losing a race for a discard is ordinary, not a fault: somebody else called it first.
        this.fail({
          code: result?.error ?? 'IllegalMove',
          detail: result?.detail ?? result?.error ?? 'That move was not allowed.',
        });
        return false;
      }

      this.clearError();
      return true;
    } catch (error) {
      this.fail({
        code: 'Unreachable',
        detail: error instanceof Error ? error.message : String(error),
      });
      return false;
    }
  }

  private fail(failure: MoveFailure): void {
    this.lastFailure.set(failure);
    this.lastError.set(failure.detail);
  }

  /** Both halves of the last refusal go together, so nothing on screen outlives what caused it. */
  private clearError(): void {
    this.lastFailure.set(null);
    this.lastError.set(null);
  }

  private say(text: string): void {
    const message = { id: ++this.messageId, text };
    this.messages.update((all) => [...all.slice(-4), message]);

    setTimeout(() => this.messages.update((all) => all.filter((m) => m.id !== message.id)), 4000);
  }
}
