import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map } from 'rxjs/operators';
import { AuthService } from './core/auth.service';
import { ToastService } from './core/toast.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  protected readonly auth = inject(AuthService);
  protected readonly toasts = inject(ToastService);
  private readonly router = inject(Router);

  protected readonly menuOpen = signal(false);

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(event => event.urlAfterRedirects)
    ),
    { initialValue: this.router.url }
  );

  /** The full workspace chrome only appears once the tenant can actually work. */
  protected readonly showSidebar = computed(() =>
    this.auth.isTenantUser() && this.auth.hasActiveSubscription() && this.auth.workspaceReady()
  );

  protected readonly onPublicPage = computed(() => {
    const url = this.url();
    return url.startsWith('/welcome') || url.startsWith('/sign-in');
  });

  protected toggleMenu() {
    this.menuOpen.update(open => !open);
  }

  protected signOut() {
    this.menuOpen.set(false);
    this.auth.logout();
  }
}
