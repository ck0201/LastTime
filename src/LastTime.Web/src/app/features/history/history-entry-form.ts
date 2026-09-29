import { Component, inject, input, OnInit, output } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { todayIso } from '../../core/format';
import { HistoryEntry, HistoryEntryRequest } from '../../core/models';

/** Form for adding a history entry, or editing one when `entry` is provided. */
@Component({
  selector: 'app-history-entry-form',
  imports: [ReactiveFormsModule],
  templateUrl: './history-entry-form.html',
})
export class HistoryEntryForm implements OnInit {
  readonly entry = input<HistoryEntry | null>(null);
  readonly saving = input(false);
  readonly error = input<string | null>(null);

  readonly submitted = output<HistoryEntryRequest>();
  readonly cancelled = output<void>();

  private readonly fb = inject(NonNullableFormBuilder);

  protected readonly form = this.fb.group({
    date: ['', Validators.required],
    action: ['', [Validators.required, Validators.pattern(/\S/), Validators.maxLength(100)]],
    cost: this.fb.control<number | null>(null, Validators.min(0)),
    provider: ['', Validators.maxLength(200)],
    notes: ['', Validators.maxLength(2000)],
  });

  ngOnInit(): void {
    const e = this.entry();
    this.form.reset({
      date: e?.date ?? todayIso(),
      action: e?.action ?? '',
      cost: e?.cost ?? null,
      provider: e?.provider ?? '',
      notes: e?.notes ?? '',
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const v = this.form.getRawValue();
    this.submitted.emit({
      date: v.date,
      action: v.action.trim(),
      cost: v.cost,
      provider: v.provider.trim() || null,
      notes: v.notes.trim() || null,
    });
  }
}
