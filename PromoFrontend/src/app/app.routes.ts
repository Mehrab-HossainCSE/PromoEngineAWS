import { Routes } from '@angular/router';
import { anonymousGuard, sessionGuard, tenantGuard, workspaceGuard } from './core/guards';

export const routes: Routes = [
  {
    path: 'welcome',
    canActivate: [anonymousGuard],
    loadComponent: () => import('./features/welcome/welcome').then(m => m.WelcomeComponent),
    title: 'PromoEngine'
  },
  {
    path: 'sign-in',
    canActivate: [anonymousGuard],
    loadComponent: () => import('./features/auth/sign-in').then(m => m.SignInComponent),
    title: 'Sign in | PromoEngine'
  },
  {
    path: 'plans',
    canActivate: [sessionGuard],
    loadComponent: () => import('./features/plans/plans').then(m => m.PlansComponent),
    title: 'Subscription | PromoEngine'
  },
  {
    path: 'get-started',
    canActivate: [sessionGuard],
    loadComponent: () => import('./features/onboarding/customer-details').then(m => m.CustomerDetailsComponent),
    title: 'Your details | PromoEngine'
  },
  {
    path: 'workspace',
    canActivate: [tenantGuard],
    loadComponent: () => import('./features/workspace/workspace-status').then(m => m.WorkspaceStatusComponent),
    title: 'Workspace | PromoEngine'
  },
  {
    path: 'promotions',
    canActivate: [workspaceGuard],
    loadComponent: () => import('./features/promotions/promotion-list').then(m => m.PromotionListComponent),
    title: 'Promotions | PromoEngine'
  },
  {
    path: 'promotions/:id',
    canActivate: [workspaceGuard],
    loadComponent: () => import('./features/promotions/promotion-detail').then(m => m.PromotionDetailComponent),
    title: 'Promotion | PromoEngine'
  },
  {
    path: 'templates',
    canActivate: [sessionGuard],
    loadComponent: () => import('./features/templates/templates').then(m => m.TemplatesComponent),
    title: 'Offer templates | PromoEngine'
  },
  {
    path: 'account',
    canActivate: [tenantGuard],
    loadComponent: () => import('./features/account/account').then(m => m.AccountComponent),
    title: 'Account | PromoEngine'
  },
  { path: '', pathMatch: 'full', redirectTo: 'welcome' },
  { path: '**', redirectTo: 'welcome' }
];
