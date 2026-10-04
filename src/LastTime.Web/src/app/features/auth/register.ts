import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Auth } from '../../core/auth';
import { passwordValidators } from '../../core/password-rules';

@Component({
  selector: 'app-register',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './register.html',
  styles: `
    .password-input-wrap {
      position: relative;
      display: flex;
      align-items: center;
      width: 100%;
    }
    .password-input-wrap input {
      padding-right: 40px;
    }
    .password-input-wrap input::-ms-reveal,
    .password-input-wrap input::-ms-clear {
      display: none;
    }
    .password-toggle {
      position: absolute;
      right: 4px;
      top: 50%;
      transform: translateY(-50%);
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 32px;
      height: 32px;
      padding: 0;
      border: 0;
      border-radius: 6px;
      background: transparent;
      color: var(--muted);
      cursor: pointer;
    }
    .password-toggle:hover {
      color: var(--text);
    }
    .password-toggle:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 1px;
    }
  `,
})
export class Register {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);

  protected readonly submitting = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly showPassword = signal(false);

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(320)]],
    password: ['', passwordValidators],
  });

  protected togglePassword(): void {
    this.showPassword.update((show) => !show);
  }

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
