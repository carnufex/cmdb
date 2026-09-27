import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Auth } from './auth';

/** Attaches the bearer token to calls to our own API, never to other origins. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(Auth).accessToken();
  if (!token || !req.url.startsWith('/api/')) {
    return next(req);
  }
  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
