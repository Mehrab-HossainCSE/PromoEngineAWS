import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Any session, guest included. */
export const sessionGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.isAuthenticated() ? true : router.createUrlTree(['/welcome']);
};

/** A real tenant user; guests are sent to pick a plan and identify themselves. */
export const tenantGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) return router.createUrlTree(['/welcome']);
  if (auth.isGuest()) return router.createUrlTree(['/plans']);
  return true;
};

/**
 * Promotion features additionally require a subscription and a provisioned
 * database, which is what "based on the selected subscription, the user can access
 * the promotion creation option" means in practice.
 */
export const workspaceGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) return router.createUrlTree(['/welcome']);
  if (auth.isGuest()) return router.createUrlTree(['/plans']);
  if (!auth.hasActiveSubscription()) return router.createUrlTree(['/plans']);
  if (!auth.workspaceReady()) return router.createUrlTree(['/workspace']);
  return true;
};

/** Keeps a signed-in user out of the welcome and sign-in screens. */
export const anonymousGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) return true;
  if (auth.isGuest()) return true;
  return router.createUrlTree(['/promotions']);
};
