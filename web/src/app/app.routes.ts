import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'Mahjong',
    loadComponent: () => import('./pages/create').then((m) => m.CreatePage),
  },
  {
    // Where an invite link lands. The room code is bound straight into the component input.
    path: 'join/:code',
    title: 'Join a table - Mahjong',
    loadComponent: () => import('./pages/join').then((m) => m.JoinPage),
  },
  {
    path: 'room/:code',
    title: 'Table - Mahjong',
    loadComponent: () => import('./pages/lobby').then((m) => m.LobbyPage),
  },
  {
    path: 'room/:code/table',
    title: 'Playing - Mahjong',
    loadComponent: () => import('./pages/table').then((m) => m.TablePage),
  },
  {
    // Finished hands for a table. Gated on the room password, not on holding a seat.
    path: 'room/:code/replay',
    title: 'Replays - Mahjong',
    loadComponent: () => import('./pages/replay-list').then((m) => m.ReplayListPage),
  },
  {
    path: 'room/:code/replay/:hand',
    title: 'Replay - Mahjong',
    loadComponent: () => import('./pages/replay').then((m) => m.ReplayPage),
  },
  { path: '**', redirectTo: '' },
];
