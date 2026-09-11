import { Injectable, signal } from '@angular/core';

export interface Toast {
  id: number;
  kind: 'info' | 'success' | 'error';
  title: string;
  text?: string;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  private nextId = 1;
  readonly toasts = signal<Toast[]>([]);

  success(title: string, text?: string) { this.push('success', title, text); }
  error(title: string, text?: string) { this.push('error', title, text, 7000); }
  info(title: string, text?: string) { this.push('info', title, text); }

  dismiss(id: number) {
    this.toasts.update(list => list.filter(t => t.id !== id));
  }

  private push(kind: Toast['kind'], title: string, text?: string, ttl = 4200) {
    const id = this.nextId++;
    this.toasts.update(list => [...list, { id, kind, title, text }]);
    setTimeout(() => this.dismiss(id), ttl);
  }
}
