import { Routes } from '@angular/router';
import { ItemDetailPage } from './features/items/item-detail';
import { ItemForm } from './features/items/item-form';
import { ItemList } from './features/items/item-list';

export const routes: Routes = [
  { path: '', component: ItemList, title: 'Last Time' },
  { path: 'items/new', component: ItemForm, title: 'New item · Last Time' },
  { path: 'items/:id', component: ItemDetailPage, title: 'Item · Last Time' },
  { path: 'items/:id/edit', component: ItemForm, title: 'Edit item · Last Time' },
  { path: '**', redirectTo: '' },
];
