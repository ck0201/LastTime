import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Auth } from '../../core/auth';

@Component({
  selector: 'app-register',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './register.html',
})
export class Register {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);

  protected readonly submitting = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(320)]],
    password: ['', [Validators.required, Validators.minLength(8), Validators.maxLength(128)]],
  });

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { email, password } = this.form.getRawValue();
    this.submitting.set(true);
    this.error.set(null);
    this.auth.register(email.trim(), password).subscribe({
      next: () => this.router.navigate(['/']),
      error: (e: HttpErrorResponse) => {
        this.error.set(message(e));
        this.submitting.set(false);
      },
    });
  }
}

/** Turns the API's 409 and ASP.NET validation (400) responses into something readable. */
function message(e: HttpErrorResponse): string {
  if (e.status === 409) return 'That email is already registered.';
  if (e.status === 400) {
    const errors = e.error?.errors as Record<string, string[]> | undefined;
    const messages = errors ? Object.values(errors).flat() : [];
    if (messages.length) return messages.join(' ');
  }
  return "Couldn't create the account. Please try again.";
}
