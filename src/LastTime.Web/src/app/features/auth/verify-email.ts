import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Auth } from '../../core/auth';

/** How long to wait before asking for another code when the API does not say (it allows one per minute). */
const DEFAULT_WAIT_SECONDS = 60;

/** Verify the signed-in user's email: ask for a 6-digit code, then enter it. */
@Component({
  selector: 'app-verify-email',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './verify-email.html',
})
export class VerifyEmail implements OnDestroy {
  protected readonly auth = inject(Auth);

  protected readonly codeAsked = signal(false);
  protected readonly sending = signal(false);
  protected readonly verifying = signal(false);
  protected readonly notice = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  /** Seconds left before another code can be requested; 0 when allowed. */
  protected readonly waitSeconds = signal(0);

  protected readonly code = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.pattern(/^\s*\d{6}\s*$/)],
  });

  protected readonly sendLabel = computed(() => {
    if (this.sending()) return 'Sending…';
    if (this.waitSeconds() > 0) return `Send again in ${this.waitSeconds()}s`;
    return this.codeAsked() ? 'Send a new code' : 'Send me a code';
  });

  private timer: ReturnType<typeof setInterval> | undefined;

  ngOnDestroy(): void {
    clearInterval(this.timer);
  }

  protected sendCode(): void {
    this.sending.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.auth.resendVerification().subscribe({
      next: () => {
        this.sending.set(false);
        this.codeAsked.set(true);
        this.notice.set("We've emailed you a 6-digit code. It expires in 10 minutes.");
        this.startWait(DEFAULT_WAIT_SECONDS);
      },
      error: (e: HttpErrorResponse) => {
        this.sending.set(false);
        if (e.status === 409) {
          this.auth.markEmailVerified(); // already verified: nothing to do, and the warning can go
        } else if (e.status === 429) {
          this.codeAsked.set(true); // a code was sent a moment ago
          const wait = retryAfterSeconds(e) ?? DEFAULT_WAIT_SECONDS;
          this.startWait(wait);
          this.error.set('A code was sent a moment ago. Please check your email, or try again shortly.');
        } else {
          this.error.set("Couldn't send the code. Please try again.");
        }
      },
    });
  }

  protected verify(): void {
    if (this.code.invalid) {
      this.code.markAsTouched();
      return;
    }

    this.verifying.set(true);
    this.error.set(null);
    this.auth.verifyEmail(this.code.value.trim()).subscribe({
      next: () => this.verifying.set(false), // Auth now knows the email is verified, so the screen switches to "done"
      error: (e: HttpErrorResponse) => {
        this.verifying.set(false);
        this.error.set(
          e.status === 400
            ? "That code isn't right or has expired. Check it, or ask for a new one."
            : "Couldn't verify your email. Please try again.",
        );
      },
    });
  }

  private startWait(seconds: number): void {
    clearInterval(this.timer);
    this.waitSeconds.set(seconds);
    this.timer = setInterval(() => {
      this.waitSeconds.update((s) => Math.max(0, s - 1));
      if (this.waitSeconds() === 0) clearInterval(this.timer);
    }, 1000);
  }
}

/** The API's Retry-After (whole seconds), when the browser is allowed to read it. */
function retryAfterSeconds(e: HttpErrorResponse): number | null {
  const seconds = Number(e.headers?.get('Retry-After'));
  return Number.isFinite(seconds) && seconds > 0 ? Math.ceil(seconds) : null;
}
