import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';

@Component({
  selector: 'app-welcome',
  imports: [RouterLink],
  templateUrl: './welcome.html',
  styleUrl: './welcome.scss'
})
export class WelcomeComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  /** Guest mode: a token with no tenant, enough to browse plans and templates. */
  protected continueAsGuest() {
    this.busy.set(true);
    this.error.set(null);

    this.auth.startGuest().subscribe({
      next: () => {
        this.busy.set(false);
        void this.router.navigateByUrl('/plans');
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.error.set(err.message);
      }
    });
  }
}
