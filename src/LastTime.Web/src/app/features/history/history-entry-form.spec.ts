import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HistoryEntry, HistoryEntryRequest } from '../../core/models';
import { HistoryEntryForm } from './history-entry-form';

describe('HistoryEntryForm', () => {
  let fixture: ComponentFixture<HistoryEntryForm>;
  let emitted: HistoryEntryRequest[];

  const root = () => fixture.nativeElement as HTMLElement;
  const field = (name: string) =>
    root().querySelector<HTMLInputElement | HTMLTextAreaElement>(`[formControlName="${name}"]`)!;

  function type(name: string, value: string): void {
    const el = field(name);
    el.value = value;
    el.dispatchEvent(new Event('input'));
  }

  const submit = () => root().querySelector('form')!.dispatchEvent(new Event('submit'));

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HistoryEntryForm] }).compileComponents();
    fixture = TestBed.createComponent(HistoryEntryForm);
    emitted = [];
    fixture.componentInstance.submitted.subscribe((r) => emitted.push(r));
  });

  it('defaults the date to today', async () => {
    await fixture.whenStable();
    expect(field('date').value).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('does not submit without an action, and shows why', async () => {
    await fixture.whenStable();
    submit();
    await fixture.whenStable();
    expect(emitted).toHaveLength(0);
    expect(root().textContent).toContain('Describe the action');
  });

  it('rejects a whitespace-only action', async () => {
    await fixture.whenStable();
    type('action', '   ');
    submit();
    expect(emitted).toHaveLength(0);
  });

  it('emits trimmed text and turns blank optional fields into null', async () => {
    await fixture.whenStable();
    type('date', '2026-08-12');
    type('action', '  Serviced ');
    type('cost', '850');
    type('provider', '   ');
    type('notes', ' Gas checked ');
    submit();

    expect(emitted).toEqual([
      { date: '2026-08-12', action: 'Serviced', cost: 850, provider: null, notes: 'Gas checked' },
    ]);
  });

  it('rejects a negative cost', async () => {
    await fixture.whenStable();
    type('action', 'Serviced');
    type('cost', '-5');
    submit();
    expect(emitted).toHaveLength(0);
  });

  it('prefills from an existing entry when editing', async () => {
    const entry: HistoryEntry = {
      id: 'e1', itemId: 'i1', date: '2026-02-03', action: 'Repaired', cost: 1200,
      provider: 'CoolCare Services', notes: null, createdAt: '2026-02-03T10:00:00Z',
    };
    fixture.componentRef.setInput('entry', entry);
    await fixture.whenStable();

    expect(field('date').value).toBe('2026-02-03');
    expect(field('action').value).toBe('Repaired');
    expect(field('cost').value).toBe('1200');
    expect(field('provider').value).toBe('CoolCare Services');
    expect(root().textContent).toContain('Save changes');
  });
});
