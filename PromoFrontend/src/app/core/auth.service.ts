import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { tap } from 'rxjs/operators';
import { ApiService } from './api.service';
import * as M from './models';

const TOKEN_KEY = 'promoengine.token';
const USER_KEY = 'promoengine.user';
const GUEST_KEY = 'promoengine.guest';
const PLAN_KEY = 'promoengine.pendingPlan';

/**
 * Holds the session. Two shapes exist: a guest session, which can browse plans and
 * offer templates, and a tenant session, which additionally has a workspace once its
 * database is provisioned.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  readonly token = signal<string | null>(readString(TOKEN_KEY));
  readonly user = signal<M.UserProfile | null>(readJson<M.UserProfile>(USER_KEY));
  readonly isGuest = signal<boolean>(readString(GUEST_KEY) === 'true');

  /** Plan a guest picked before they were asked to identify themselves. */
  readonly pendingPlanCode = signal<string | null>(readString(PLAN_KEY));

  readonly isAuthenticated = computed(() => !!this.token());
  readonly isTenantUser = computed(() => !!this.token() && !this.isGuest() && !!this.user());
  readonly subscription = computed(() => this.user()?.subscription ?? null);

  readonly hasActiveSubscription = computed(() => {
    const sub = this.user()?.subscription;
    return !!sub && (sub.status === 'Active' || sub.status === 'Pending');
  });

  readonly workspaceReady = computed(() => this.user()?.provisioningStatus === 'Ready');

  readonly initials = computed(() => {
    const name = this.user()?.displayName || this.user()?.email || 'G';
    return name
      .split(/[\s@.]+/)
      .filter(Boolean)
      .slice(0, 2)
      .map(part => part[0]!.toUpperCase())
      .join('');
  });

  login(body: M.LoginRequest) {
    return this.api.login(body).pipe(tap(response => this.accept(response)));
  }

  register(body: M.RegisterRequest) {
    return this.api.register(body).pipe(tap(response => this.accept(response)));
  }

  startGuest() {
    return this.api.guest().pipe(tap(response => this.accept(response)));
  }

  convertGuest(body: M.GuestConversionRequest) {
    return this.api.convertGuest(body).pipe(tap(response => {
      this.accept(response);
      this.setPendingPlan(null);
    }));
  }

  /** Re-reads the profile so provisioning and subscription state stay current. */
  refresh() {
    return this.api.me().pipe(tap(me => {
      this.isGuest.set(me.isGuest);
      writeString(GUEST_KEY, String(me.isGuest));

      if (me.user) {
        this.user.set(me.user);
        writeJson(USER_KEY, me.user);
      }
    }));
  }

  setPendingPlan(code: string | null) {
    this.pendingPlanCode.set(code);
    if (code) {
      writeString(PLAN_KEY, code);
    } else {
      remove(PLAN_KEY);
    }
  }

  /** Applies a freshly polled provisioning status onto the cached profile. */
  applyProvisioning(status: M.ProvisioningStatus) {
    const current = this.user();
    if (!current) return;

    const next: M.UserProfile = {
      ...current,
      provisioningStatus: status.status,
      databaseName: status.databaseName ?? current.databaseName,
      subscription: status.subscription ?? current.subscription
    };

    this.user.set(next);
    writeJson(USER_KEY, next);
  }

  logout(redirectTo = '/welcome') {
    this.token.set(null);
    this.user.set(null);
    this.isGuest.set(false);
    remove(TOKEN_KEY);
    remove(USER_KEY);
    remove(GUEST_KEY);
    remove(PLAN_KEY);
    void this.router.navigateByUrl(redirectTo);
  }

  private accept(response: M.AuthResponse) {
    this.token.set(response.token);
    this.isGuest.set(response.isGuest);
    writeString(TOKEN_KEY, response.token);
    writeString(GUEST_KEY, String(response.isGuest));

    if (response.user) {
      this.user.set(response.user);
      writeJson(USER_KEY, response.user);
    } else {
      this.user.set(null);
      remove(USER_KEY);
    }
  }
}

// localStorage is best effort: private windows and blocked site data throw.
function readString(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}

function readJson<T>(key: string): T | null {
  try {
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

function writeString(key: string, value: string) {
  try { localStorage.setItem(key, value); } catch { /* ignore */ }
}

function writeJson(key: string, value: unknown) {
  try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* ignore */ }
}

function remove(key: string) {
  try { localStorage.removeItem(key); } catch { /* ignore */ }
}
