import { Component, inject, input, OnInit, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ItemsApi } from '../../core/items-api';

/** Create (route `items/new`) or edit (route `items/:id/edit`) an item. */
@Component({
  selector: 'app-item-form',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './item-form.html',
})
export class ItemForm implements OnInit {
  private readonly api = inject(ItemsApi);
  private readonly router = inject(Router);

  /** Bound from the route; undefined when creating. */
  readonly id = input<string>();

  protected readonly loading = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.pattern(/\S/), Validators.maxLength(200)]],
    description: ['', Validators.maxLength(2000)],
  });

  ngOnInit(): void {
    const id = this.id();
    if (!id) return;

    this.loading.set(true);
    this.api.get(id).subscribe({
      next: (item) => {
        this.form.setValue({ name: item.name, description: item.description ?? '' });
        this.loading.set(false);
      },
      error: () => {
        this.error.set("Couldn't load this item.");
        this.loading.set(false);
      },
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { name, description } = this.form.getRawValue();
    const request = { name: name.trim(), description: description.trim() || null };
    const id = this.id();

    this.saving.set(true);
    this.error.set(null);
    const save$ = id ? this.api.update(id, request) : this.api.create(request);
    save$.subscribe({
      next: (item) => this.router.navigate(['/items', item.id]),
      error: () => {
        this.error.set("Couldn't save. Please try again.");
        this.saving.set(false);
      },
    });
  }
}
