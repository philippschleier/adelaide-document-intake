import { Routes } from '@angular/router';
import { Dashboard } from './dashboard/dashboard';
import { Review } from './review/review';
import { Compare } from './compare/compare';

export const routes: Routes = [
  { path: '', component: Dashboard, title: 'Adelaide — Dokumente' },
  { path: 'review/:id', component: Review, title: 'Adelaide — Prüfung' },
  { path: 'compare/:id/:other', component: Compare, title: 'Adelaide — Vergleich' },
  { path: '**', redirectTo: '' },
];
