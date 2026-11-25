import { Routes } from '@angular/router';
import { guestGuard, ownerGuard } from './auth/guards';
import { Register } from './auth/register';
import { SignIn } from './auth/signin';
import { Home } from './home';

// Shop pages arrive with US3–US5 under the owner guard.
export const routes: Routes = [
  { path: '', pathMatch: 'full', component: Home, canActivate: [ownerGuard] },
  { path: 'signin', component: SignIn, canActivate: [guestGuard], title: 'Sign in · ShopHub' },
  { path: 'register', component: Register, canActivate: [guestGuard], title: 'Register · ShopHub' },
  { path: '**', redirectTo: '' },
];
