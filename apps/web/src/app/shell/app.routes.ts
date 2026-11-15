import { Routes } from '@angular/router';

export const appRoutes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'cluster' },
  { path: 'cluster', loadComponent: () => import('../features/cluster/cluster-page').then((page) => page.ClusterPage) },
  { path: 'chat', loadComponent: () => import('../features/chat/chat-page').then((page) => page.ChatPage) },
  { path: '**', redirectTo: 'cluster' },
];
