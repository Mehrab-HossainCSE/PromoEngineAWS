import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { AuthService } from './auth.service';

/** Attaches the bearer token and drops the session when the API rejects it. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const token = auth.token();

  const request = token
    ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : req;

  return next(request).pipe(
    catchError(error => {
      // Only an expired or invalid token should end the session. A 403 means the
      // caller is a guest or lacks a plan, which the UI handles in place.
      if (error?.status === 401 && !req.url.includes('/api/auth/')) {
        auth.logout('/welcome');
        void router.navigateByUrl('/welcome');
      }

      return throwError(() => error);
    })
  );
};
