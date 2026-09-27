import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', loadComponent: () => import('./map/map').then((m) => m.MapComponent) },
  { path: '**', redirectTo: '' },
];
