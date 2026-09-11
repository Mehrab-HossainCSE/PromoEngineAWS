import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { environment } from '../../environments/environment';
import * as M from './models';

/** A flattened API failure the components can render directly. */
export class ApiError extends Error {
  constructor(
    override readonly message: string,
    readonly status: number,
    readonly fieldErrors: Record<string, string[]> = {}
  ) {
    super(message);
  }

  /** All field messages as a flat list, for a summary block above a form. */
  get allMessages(): string[] {
    const flat = Object.values(this.fieldErrors).flat();
    return flat.length ? flat : [this.message];
  }
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiUrl;

  // --- auth -------------------------------------------------------------

  register(body: M.RegisterRequest) {
    return this.post<M.AuthResponse>('/api/auth/register', body);
  }

  login(body: M.LoginRequest) {
    return this.post<M.AuthResponse>('/api/auth/login', body);
  }

  guest() {
    return this.post<M.AuthResponse>('/api/auth/guest', {});
  }

  convertGuest(body: M.GuestConversionRequest) {
    return this.post<M.AuthResponse>('/api/auth/guest/convert', body);
  }

  me() {
    return this.get<M.MeResponse>('/api/auth/me');
  }

  // --- subscriptions ----------------------------------------------------

  plans() {
    return this.get<M.Plan[]>('/api/subscriptions/plans');
  }

  subscribe(planCode: string) {
    return this.post<M.Subscription>('/api/subscriptions/subscribe', { planCode });
  }

  provisioningStatus() {
    return this.get<M.ProvisioningStatus>('/api/subscriptions/status');
  }

  retryProvisioning() {
    return this.post<void>('/api/subscriptions/retry-provisioning', {});
  }

  templates() {
    return this.get<M.OfferTemplate[]>('/api/offer-templates');
  }

  // --- promotions -------------------------------------------------------

  promotions(page = 1, pageSize = 25) {
    return this.get<M.Paged<M.PromotionSummary>>(`/api/promotions?page=${page}&pageSize=${pageSize}`);
  }

  searchPromotions(body: M.PromotionSearchRequest) {
    return this.post<M.Paged<M.PromotionSummary>>('/api/promotions/search', body);
  }

  promotion(id: string) {
    return this.get<M.PromotionDetail>(`/api/promotions/${id}`);
  }

  createPromotion(description: string, campaignId?: string | null) {
    return this.post<M.PromotionDetail>('/api/promotions', { description, campaignId });
  }

  updatePromotion(id: string, description: string, campaignId?: string | null) {
    return this.put<M.PromotionDetail>(`/api/promotions/${id}`, { description, campaignId });
  }

  deletePromotion(id: string) {
    return this.delete<void>(`/api/promotions/${id}`);
  }

  // --- offers -----------------------------------------------------------

  addOffer(promotionId: string, body: M.SaveOfferRequest) {
    return this.post<M.Offer>(`/api/promotions/${promotionId}/offers`, body);
  }

  updateOffer(offerId: string, body: M.SaveOfferRequest) {
    return this.put<M.Offer>(`/api/offers/${offerId}`, body);
  }

  copyOffer(offerId: string, body: M.CreateOfferFromExistingRequest) {
    return this.post<M.Offer>(`/api/offers/${offerId}/copy`, body);
  }

  massUpdateOffers(body: M.MassUpdateRequest) {
    return this.post<{ updated: number }>('/api/offers/mass-update', body);
  }

  submitOffers(offerIds: string[]) {
    return this.post<number>('/api/offers/submit', { offerIds });
  }

  approveOffers(offerIds: string[]) {
    return this.post<number>('/api/offers/approve', { offerIds });
  }

  reopenOffers(offerIds: string[]) {
    return this.post<number>('/api/offers/reopen', { offerIds });
  }

  cancelOffers(ids: string[], reason: string) {
    return this.post<number>('/api/offers/cancel', { ids, reason });
  }

  cancelItems(offerId: string, ids: string[], reason: string) {
    return this.post<number>(`/api/offers/${offerId}/items/cancel`, { ids, reason });
  }

  deleteOffers(offerIds: string[]) {
    return this.post<void>('/api/offers/delete', { offerIds });
  }

  // --- offer products (the one Excel Upload) ----------------------------

  /**
   * Sends the offer's product sheet to be parsed. Nothing is stored: the rows come
   * back and are saved with the offer itself, because until the offer is created
   * there is nothing to attach them to.
   */
  parseOfferProducts(file: File) {
    const form = new FormData();
    form.append('file', file, file.name);

    return this.http
      .post<M.OfferProductUploadResponse>(`${this.base}/api/offer-products/parse`, form)
      .pipe(catchError(this.toApiError));
  }

  // --- locations --------------------------------------------------------

  addLocations(offerId: string, body: M.SaveOfferLocationRequest[]) {
    return this.post<M.OfferLocation[]>(`/api/offers/${offerId}/locations`, body);
  }

  copyLocations(offerId: string, targetOfferIds: string[]) {
    return this.post<number>(`/api/offers/${offerId}/locations/copy`, { targetOfferIds });
  }

  cancelLocations(offerId: string, ids: string[], reason: string) {
    return this.post<number>(`/api/offers/${offerId}/locations/cancel`, { ids, reason });
  }

  deleteLocations(offerId: string, locationIds: string[]) {
    return this.post<void>(`/api/offers/${offerId}/locations/delete`, { offerIds: locationIds });
  }

  // --- campaigns --------------------------------------------------------

  campaigns() {
    return this.get<M.Campaign[]>('/api/campaigns');
  }

  createCampaign(code: string, description: string) {
    return this.post<M.Campaign>('/api/campaigns', { code, description });
  }

  deleteCampaign(id: string) {
    return this.delete<void>(`/api/campaigns/${id}`);
  }

  // --- plumbing ---------------------------------------------------------

  private get<T>(path: string): Observable<T> {
    return this.http.get<T>(this.base + path).pipe(catchError(this.toApiError));
  }

  private post<T>(path: string, body: unknown): Observable<T> {
    return this.http.post<T>(this.base + path, body).pipe(catchError(this.toApiError));
  }

  private put<T>(path: string, body: unknown): Observable<T> {
    return this.http.put<T>(this.base + path, body).pipe(catchError(this.toApiError));
  }

  private delete<T>(path: string): Observable<T> {
    return this.http.delete<T>(this.base + path).pipe(catchError(this.toApiError));
  }

  /** Normalises ProblemDetails, ValidationProblemDetails and transport failures. */
  private toApiError = (response: HttpErrorResponse) => {
    if (response.status === 0) {
      return throwError(() => new ApiError('Cannot reach the PromoEngine API. Is it running?', 0));
    }

    const problem = response.error as M.ApiProblem | string | null;

    if (typeof problem === 'string') {
      return throwError(() => new ApiError(problem, response.status));
    }

    const message = problem?.detail || problem?.title || response.statusText || 'Request failed.';
    return throwError(() => new ApiError(message, response.status, problem?.errors ?? {}));
  };
}
