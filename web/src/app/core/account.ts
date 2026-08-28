import { Injectable, computed, signal } from '@angular/core';
import { SignedInResponse } from './models';

/** The account this browser is signed in as, as it is kept between visits. */
export interface StoredAccount {
  userId: string;
  username: string;
  token: string;
  /** ISO instant the token stops working. Checked on read, so a stale one never reaches the API. */
  expiresAt: string;
}

const KEY = 'mahjong.account';

/**
 * Being signed in.
 *
 * Deliberately separate from `Session`, which holds a seat at one table. A seat is per room and
 * ends with the evening; an account outlives both, and the two are stored apart so signing out
 * does not stand somebody up from a hand they are in the middle of playing.
 *
 * localStorage, like the seat: the whole point of registering is that the phone still knows you
 * next weekend.
 */
@Injectable({ providedIn: 'root' })
export class Account {
  readonly current = signal<StoredAccount | null>(read());

  readonly username = computed(() => this.current()?.username ?? null);
  readonly signedIn = computed(() => this.current() !== null);

  save(response: SignedInResponse): void {
    const account: StoredAccount = {
      userId: response.userId,
      username: response.username,
      token: response.token,
      expiresAt: response.expiresAt,
    };

    try {
      localStorage.setItem(KEY, JSON.stringify(account));
    } catch {
      // Private mode refuses the write. Signed in for this tab is better than not at all.
    }

    this.current.set(account);
  }

  /** The bearer token to send, or null when there is nobody signed in and nothing to send. */
  token(): string | null {
    return this.current()?.token ?? null;
  }

  clear(): void {
    try {
      localStorage.removeItem(KEY);
    } catch {
      // Nothing to do: the signal below is what the app actually reads.
    }

    this.current.set(null);
  }
}

function read(): StoredAccount | null {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return null;

    const parsed = JSON.parse(raw) as StoredAccount;
    if (!parsed?.token || !parsed?.username) return null;

    // A token the server would refuse anyway is dropped here, so the app shows the signed-out
    // state straight away instead of a profile that fails to load.
    return Date.parse(parsed.expiresAt) > Date.now() ? parsed : null;
  } catch {
    // A half-written or hand-edited value is not worth crashing the app over.
    return null;
  }
}
