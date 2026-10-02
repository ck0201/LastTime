import { Validators } from '@angular/forms';

/** The password rule, as the API enforces it for registration and password reset: required, 8 to 128 characters. */
export const passwordValidators = [Validators.required, Validators.minLength(8), Validators.maxLength(128)];
