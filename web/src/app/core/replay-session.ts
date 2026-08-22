import { Injectable } from '@angular/core';

const PREFIX = 'mahjong.replay.';

/**
 * The token that came back from typing a room's password on the replay screen.
 *
 * sessionStorage, not localStorage, and deliberately the opposite choice from `Session`. A seat has
 * to survive a phone locking or a tab being closed by accident, because losing it means losing the
 * hand you are in. A replay has nothing running, so the safer default wins: close the tab and the
 * next person to open it types the password again.
 *
 * Keyed per room, so unlocking one table does not open another. The in-memory copy is what keeps
 * the list page and the viewer page working in a browser that refuses storage - private mode on
 * iOS throws on write rather than quietly doing nothing.
 */
@Injectable({ providedIn: 'root' })
export class ReplaySession {
  private readonly held = new Map<string, string>();

  tokenFor(roomCode: string): string | null {
    const key = PREFIX + roomCode.toUpperCase();

    try {
      return sessionStorage.getItem(key) ?? this.held.get(key) ?? null;
    } catch {
      return this.held.get(key) ?? null;
    }
  }

  save(roomCode: string, token: string): void {
    const key = PREFIX + roomCode.toUpperCase();
    this.held.set(key, token);

    try {
      sessionStorage.setItem(key, token);
    } catch {
      // Kept in `held` above, so the replay still opens for as long as the tab is loaded.
    }
  }

  clear(roomCode: string): void {
    const key = PREFIX + roomCode.toUpperCase();
    this.held.delete(key);

    try {
      sessionStorage.removeItem(key);
    } catch {
      // Nothing to do.
    }
  }
}
