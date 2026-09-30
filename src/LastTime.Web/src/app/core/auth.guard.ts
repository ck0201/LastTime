import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Auth } from './auth';

/** Lets signed-in users through; everyone else is sent to the login page. */
export const authGuard: CanActivateFn = () =>
  inject(Auth).isAuthenticated() || inject(Router).createUrlTree(['/login']);
