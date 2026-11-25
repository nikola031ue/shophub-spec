import { Routes } from '@angular/router';
import { guestGuard, ownerGuard } from './auth/guards';
import { Register } from './auth/register';
import { SignIn } from './auth/signin';
import { ShopList } from './shops/shop-list';

// Owner pages sit under the owner guard; change and delete arrive with US4 and US5.
export const routes: Routes = [
  { path: '', pathMatch: 'full', component: ShopList, canActivate: [ownerGuard], title: 'Your shops · ShopHub' },
  { path: 'signin', component: SignIn, canActivate: [guestGuard], title: 'Sign in · ShopHub' },
  { path: 'register', component: Register, canActivate: [guestGuard], title: 'Register · ShopHub' },
  { path: '**', redirectTo: '' },
];
