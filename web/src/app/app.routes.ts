import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./home/home.component').then((m) => m.HomeComponent), title: 'Cloud Architect Lab' },
  // O workspace carrega React + Excalidraw sob demanda (chunk próprio).
  { path: 'desafios/:id', loadComponent: () => import('./workspace/workspace.component').then((m) => m.WorkspaceComponent), title: 'Desenho · Cloud Architect Lab' },
  { path: 'avaliacoes/:id', loadComponent: () => import('./result/result.component').then((m) => m.ResultComponent), title: 'Resultado · Cloud Architect Lab' },
  { path: 'perfil', loadComponent: () => import('./profile/profile.component').then((m) => m.ProfileComponent), title: 'Perfil · Cloud Architect Lab' },
  { path: '**', redirectTo: '' },
];
