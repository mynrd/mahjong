import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  ReplayListItemView,
  ReplayUnlockResponse,
  ReplayView,
  RoomView,
  SeatedResponse,
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

  createRoom(body: {
    name: string;
    password: string;
    displayName: string;
    assistEnabled: boolean;
  }): Promise<SeatedResponse> {
    return firstValueFrom(this.http.post<SeatedResponse>(`${this.base}/api/rooms`, body));
  }

  joinRoom(code: string, body: { displayName: string; password: string }): Promise<SeatedResponse> {
    return firstValueFrom(this.http.post<SeatedResponse>(`${this.base}/api/rooms/${code}/join`, body));
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

function auth(token: string): HttpHeaders {
  return new HttpHeaders({ Authorization: `Bearer ${token}` });
}
