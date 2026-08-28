import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  PlayedGameView,
  ProfileView,
  ReplayListItemView,
  ReplayUnlockResponse,
  ReplayView,
  RoomView,
  SeatedResponse,
  SignedInResponse,
  WhoAmIResponse,
} from './models';

/**
 * Where the API lives: the same origin the page came from.
 *
 * The dev server forwards /api and /hubs to the API on port 5080 (web/proxy.conf.json), so the
 * browser only ever talks to one host and one port. Naming the API's own port here instead broke
 * every way of reaching the game that is not a plain LAN address: a tunnel forwards port 4200 and
 * nothing else, so https://<tunnel-host>:5080 resolves to nothing, and on an https page a call to
 * http://<host>:5080 is blocked as mixed content before it is even sent.
 *
 * Empty string rather than window.location.origin so the paths built from it stay relative and
 * the browser resolves them itself.
 */
export function apiBaseUrl(): string {
  return '';
}

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);
  private readonly base = apiBaseUrl();

  /**
   * `accountToken` is optional throughout: sent, the seat is recorded against that account and the
   * hands played from it land on its profile; left out, the table works exactly as it always has,
   * with nobody signed in and nothing recorded.
   */
  createRoom(
    body: { name: string; password: string; displayName: string; assistEnabled: boolean },
    accountToken?: string | null,
  ): Promise<SeatedResponse> {
    return firstValueFrom(
      this.http.post<SeatedResponse>(`${this.base}/api/rooms`, body, {
        headers: auth(accountToken),
      }),
    );
  }

  joinRoom(
    code: string,
    body: { displayName: string; password: string },
    accountToken?: string | null,
  ): Promise<SeatedResponse> {
    return firstValueFrom(
      this.http.post<SeatedResponse>(`${this.base}/api/rooms/${code}/join`, body, {
        headers: auth(accountToken),
      }),
    );
  }

  // ------------------------------------------------------------------ accounts

  /** Claims a username. Refused with UsernameTaken if somebody registered it first. */
  register(body: { username: string; password: string }): Promise<SignedInResponse> {
    return firstValueFrom(
      this.http.post<SignedInResponse>(`${this.base}/api/users/register`, body),
    );
  }

  signIn(body: { username: string; password: string }): Promise<SignedInResponse> {
    return firstValueFrom(this.http.post<SignedInResponse>(`${this.base}/api/users/login`, body));
  }

  /** Drops this browser's session server side. Other devices stay signed in. */
  signOut(token: string): Promise<unknown> {
    return firstValueFrom(
      this.http.post(`${this.base}/api/users/logout`, {}, { headers: auth(token) }),
    );
  }

  /** Who you are, and every finished hand you had a seat in. */
  profile(token: string): Promise<ProfileView> {
    return firstValueFrom(
      this.http.get<ProfileView>(`${this.base}/api/users/me`, { headers: auth(token) }),
    );
  }

  myGames(token: string): Promise<PlayedGameView[]> {
    return firstValueFrom(
      this.http.get<PlayedGameView[]>(`${this.base}/api/users/me/games`, { headers: auth(token) }),
    );
  }

  getRoom(code: string): Promise<RoomView> {
    return firstValueFrom(this.http.get<RoomView>(`${this.base}/api/rooms/${code}`));
  }

  whoAmI(code: string, token: string): Promise<WhoAmIResponse> {
    return firstValueFrom(
      this.http.get<WhoAmIResponse>(`${this.base}/api/rooms/${code}/me`, { headers: auth(token) }),
    );
  }

  addBots(code: string, token: string, count?: number): Promise<RoomView> {
    return firstValueFrom(
      this.http.post<RoomView>(`${this.base}/api/rooms/${code}/bots`, { count }, { headers: auth(token) }),
    );
  }

  /**
   * Frees a seat from the lobby: a bot that was filled in, or somebody who sat down and is no
   * longer wanted. Host only, and refused once a hand is being played - the table has its own
   * version of this over the hub for the gap between hands.
   */
  removeSeat(code: string, token: string, seat: number): Promise<RoomView> {
    return firstValueFrom(
      this.http.delete<RoomView>(`${this.base}/api/rooms/${code}/seats/${seat}`, { headers: auth(token) }),
    );
  }

  /** Ends the table for everybody. Host only. The room and its finished hands stay readable. */
  closeRoom(code: string, token: string): Promise<RoomView> {
    return firstValueFrom(
      this.http.post<RoomView>(`${this.base}/api/rooms/${code}/close`, {}, { headers: auth(token) }),
    );
  }

  startHand(code: string, token: string): Promise<{ started: boolean }> {
    return firstValueFrom(
      this.http.post<{ started: boolean }>(`${this.base}/api/rooms/${code}/start`, {}, { headers: auth(token) }),
    );
  }

  // ------------------------------------------------------------------ replays

  /**
   * Trades the room password for a token that reads its finished hands. Separate from the seat
   * token because a replay link is usually opened in a browser that never sat down.
   */
  unlockReplays(code: string, password: string): Promise<ReplayUnlockResponse> {
    return firstValueFrom(
      this.http.post<ReplayUnlockResponse>(`${this.base}/api/rooms/${code}/replay/unlock`, { password }),
    );
  }

  listReplays(code: string, token: string): Promise<ReplayListItemView[]> {
    return firstValueFrom(
      this.http.get<ReplayListItemView[]>(`${this.base}/api/rooms/${code}/replays`, { headers: auth(token) }),
    );
  }

  getReplay(code: string, handNumber: number, token: string): Promise<ReplayView> {
    return firstValueFrom(
      this.http.get<ReplayView>(`${this.base}/api/rooms/${code}/replays/${handNumber}`, { headers: auth(token) }),
    );
  }
}

/** No token means no Authorization header, rather than one reading "Bearer null". */
function auth(token: string | null | undefined): HttpHeaders {
  return token ? new HttpHeaders({ Authorization: `Bearer ${token}` }) : new HttpHeaders();
}
