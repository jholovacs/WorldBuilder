import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./world-dashboard/world-dashboard.component').then((m) => m.WorldDashboardComponent),
  },
  {
    path: 'worlds',
    loadComponent: () =>
      import('./worlds-home/worlds-home.component').then((m) => m.WorldsHomeComponent),
  },
  {
    path: 'world/:worldId',
    loadComponent: () =>
      import('./world-detail/world-detail.component').then((m) => m.WorldDetailComponent),
  },
  {
    path: 'activity',
    loadComponent: () => import('./activity/activity.component').then((m) => m.ActivityComponent),
  },
  {
    path: 'terrain',
    loadComponent: () =>
      import('./terrain/terrain-view.component').then((m) => m.TerrainViewComponent),
  },
];
