import { Routes } from '@angular/router';
import { Home } from './home';

// Panel routes (sign-in, shops) arrive with US2–US5.
export const routes: Routes = [
  { path: '', pathMatch: 'full', component: Home },
  { path: '**', redirectTo: '' },
];
