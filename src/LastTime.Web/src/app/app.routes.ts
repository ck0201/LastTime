import { Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';
import { Login } from './features/auth/login';
import { Register } from './features/auth/register';
import { ItemDetailPage } from './features/items/item-detail';
import { ItemForm } from './features/items/item-form';
import { ItemList } from './features/items/item-list';

export const routes: Routes = [
  { path: 'login', component: Login, title: 'Sign in · Last Time' },
  { path: 'register', component: Register, title: 'Create account · Last Time' },
  {
    path: '',
    canActivate: [authGuard],
    children: [
      { path: '', component: ItemList, title: 'Last Time' },
      { path: 'items/new', component: ItemForm, title: 'New item · Last Time' },
      { path: 'items/:id', component: ItemDetailPage, title: 'Item · Last Time' },
      { path: 'items/:id/edit', component: ItemForm, title: 'Edit item · Last Time' },
    ],
  },
  { path: '**', redirectTo: '' },
];
