import { Injectable, signal } from '@angular/core';

/** What is remembered about the seat this browser is sitting in. */
export interface StoredSeat {
  roomCode: string;
  playerId: string;
  seat: number;
  token: string;
  displayName: string;
  isHost: boolean;
}

const KEY = 'mahjong.seat';

/**
 * The whole session model: one bearer token, kept in localStorage, keyed by room.
 *
 * localStorage rather than sessionStorage on purpose. A phone that locks, a browser that discards
 * a background tab, or a player who closes the tab by accident all have to be able to come back to
 * the same seat holding the same tiles - and sessionStorage would throw the token away in every
 * one of those cases.
 */
@Injectable({ providedIn: 'root' })
export class Session {
  readonly current = signal<StoredSeat | null>(read());

  save(seat: StoredSeat): void {
    localStorage.setItem(KEY, JSON.stringify(seat));
    this.current.set(seat);
  }

  /** The stored seat, but only if it is for the room being asked about. */
  forRoom(roomCode: string): StoredSeat | null {
    const seat = this.current();
    if (!seat) return null;
    return seat.roomCode.toUpperCase() === roomCode.toUpperCase() ? seat : null;
  }

  clear(): void {
    localStorage.removeItem(KEY);
    this.current.set(null);
  }
}

function read(): StoredSeat | null {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return null;

    const parsed = JSON.parse(raw) as StoredSeat;
    return parsed?.token && parsed?.roomCode ? parsed : null;
  } catch {
    // A half-written or hand-edited value is not worth crashing the app over.
    return null;
  }
}
