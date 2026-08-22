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
 * Where the API lives.
 *
 * Worked out from the page's own address rather than baked into a config file. The whole point of
 * this app is that somebody opens an invite link on their phone: whatever host they reached the
 * web app on is the host the API is on too, so hardcoding "localhost" would break every device
 * except the one running the server.
 */
export function apiBaseUrl(): string {
  const { protocol, hostname } = window.location;
  return `${protocol}//${hostname}:5080`;
}

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);
  private readonly base = apiBaseUrl();

  createRoom(body: { name: string; password: string; displayName: string }): Promise<SeatedResponse> {
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
