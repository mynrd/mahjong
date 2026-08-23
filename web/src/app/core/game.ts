import { Injectable, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { apiBaseUrl } from './api';
import { ClaimKind, MoveResult, PlayerGameView } from './models';

export type ConnectionState = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'closed';

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
  readonly messages = signal<TableMessage[]>([]);

  private hub: signalR.HubConnection | null = null;
  private messageId = 0;

  /**
   * Guards the automatic draw. Without it, every state update that arrives while the draw is in
   * flight would fire another one, and the server would reject the extras noisily.
   */
  private drawing = false;

  async connect(token: string): Promise<void> {
    await this.disconnect();

    this.connection.set('connecting');

    const hub = new signalR.HubConnectionBuilder()
      .withUrl(`${apiBaseUrl()}/hubs/game?access_token=${encodeURIComponent(token)}`)
      // A phone that sleeps, a wifi handover, a laptop lid: all normal, all recoverable. The seat
      // is held server-side, so reconnecting picks the hand back up exactly where it was.
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    hub.on('StateChanged', (view: PlayerGameView) => {
      this.view.set(view);
      void this.autoDraw(view);
    });

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
    this.drawing = false;
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

  declareSecretKang(face: string): Promise<boolean> {
    return this.invoke('DeclareSecretKang', face);
  }

  declareSagasa(face: string): Promise<boolean> {
    return this.invoke('DeclareSagasa', face);
  }

  declareTodas(): Promise<boolean> {
    return this.invoke('DeclareTodas');
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

  /**
   * Takes the tile off the wall automatically when the turn comes round. Drawing is not a decision
   * in mahjong - there is exactly one thing you can do - so making the player tap for it would add
   * a step that never changes the outcome.
   */
  private async autoDraw(view: PlayerGameView): Promise<void> {
    if (this.drawing) return;
    if (view.phase !== 'AwaitingDraw' || view.currentSeat !== view.yourSeat) return;

    this.drawing = true;
    try {
      await this.invoke('Draw');
    } finally {
      this.drawing = false;
    }
  }

  private async invoke(method: string, ...args: unknown[]): Promise<boolean> {
    const hub = this.hub;
    if (!hub || hub.state !== signalR.HubConnectionState.Connected) {
      this.lastError.set('Not connected to the table.');
      return false;
    }

    try {
      const result = await hub.invoke<MoveResult>(method, ...args);

      if (!result?.success) {
        // Losing a race for a discard is ordinary, not a fault: somebody else called it first.
        this.lastError.set(result?.detail ?? result?.error ?? 'That move was not allowed.');
        return false;
      }

      this.lastError.set(null);
      return true;
    } catch (error) {
      this.lastError.set(error instanceof Error ? error.message : String(error));
      return false;
    }
  }

  private say(text: string): void {
    const message = { id: ++this.messageId, text };
    this.messages.update((all) => [...all.slice(-4), message]);

    setTimeout(() => this.messages.update((all) => all.filter((m) => m.id !== message.id)), 4000);
  }
}
