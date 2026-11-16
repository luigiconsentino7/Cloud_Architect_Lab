import { HttpClient, HttpErrorResponse, HttpHeaders, HttpInterceptorFn, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import {
  Achievement,
  Attempt,
  Challenge,
  ChallengeSummary,
  Competency,
  Difficulty,
  Evaluation,
  EvaluationTicket,
  ExcalidrawScene,
  FlowDefinition,
  Hint,
  HistoryItem,
  LibraryItem,
  Me,
  Palette,
  ProgressSummary,
  Provider,
  ReferenceView,
  TimeSeries,
  Validation,
} from './models';

/** Header que o backend exige nas requisições que alteram dados (proteção contra CSRF no localhost). */
export const labClientInterceptor: HttpInterceptorFn = (request, next) =>
  next(request.method === 'GET' ? request : request.clone({ setHeaders: { 'X-Lab-Client': 'web' } }));

/** Erro da API em ProblemDetails, com a mensagem pronta para mostrar. */
export function problemMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { detail?: string; title?: string } | null;
    return body?.detail ?? body?.title ?? `Erro ${error.status}`;
  }
  return error instanceof Error ? error.message : 'Erro inesperado';
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  me(): Promise<Me> {
    return this.get<Me>('/api/me');
  }

  updatePreferences(body: { defaultProvider?: Provider; displayName?: string }): Promise<Me> {
    return firstValueFrom(this.http.patch<Me>('/api/me/preferences', body));
  }

  library(q: string, difficulty?: Difficulty, track?: string): Promise<LibraryItem[]> {
    let params = new HttpParams();
    if (q) params = params.set('q', q);
    if (difficulty) params = params.set('difficulty', difficulty);
    if (track) params = params.set('track', track);
    return firstValueFrom(this.http.get<LibraryItem[]>('/api/library', { params }));
  }

  createChallenge(body: { seedId: string; provider: Provider; difficulty: Difficulty; mode: string; twists: string[] }): Promise<Challenge> {
    return this.post<Challenge>('/api/challenges', body);
  }

  recentChallenges(): Promise<ChallengeSummary[]> {
    return this.get<ChallengeSummary[]>('/api/challenges?limit=12');
  }

  challenge(id: string): Promise<Challenge> {
    return this.get<Challenge>(`/api/challenges/${id}`);
  }

  anotherVariation(id: string): Promise<Challenge> {
    return this.post<Challenge>(`/api/challenges/${id}/variations`, {});
  }

  attempts(challengeId: string): Promise<Attempt[]> {
    return this.get<Attempt[]>(`/api/challenges/${challengeId}/attempts`);
  }

  startAttempt(challengeId: string): Promise<Attempt> {
    return this.post<Attempt>(`/api/challenges/${challengeId}/attempts`, {});
  }

  attempt(id: string): Promise<Attempt> {
    return this.get<Attempt>(`/api/attempts/${id}`);
  }

  saveDraft(id: string, scene: ExcalidrawScene, version: number): Promise<{ draftVersion: number }> {
    const headers = new HttpHeaders({ 'If-Match': `"${version}"` });
    return firstValueFrom(this.http.put<{ draftVersion: number }>(`/api/attempts/${id}/draft`, { scene }, { headers }));
  }

  saveFlows(id: string, flows: FlowDefinition[]): Promise<unknown> {
    return firstValueFrom(this.http.put(`/api/attempts/${id}/flows`, { flows }));
  }

  validate(id: string): Promise<Validation> {
    return this.post<Validation>(`/api/attempts/${id}/validate`, {});
  }

  hint(id: string): Promise<Hint> {
    return this.post<Hint>(`/api/attempts/${id}/hints`, {});
  }

  simulate(id: string): Promise<EvaluationTicket> {
    return this.post<EvaluationTicket>(`/api/attempts/${id}/simulate`, {});
  }

  submit(id: string, idempotencyKey: string): Promise<EvaluationTicket> {
    const headers = new HttpHeaders({ 'Idempotency-Key': idempotencyKey });
    return firstValueFrom(this.http.post<EvaluationTicket>(`/api/attempts/${id}/submit`, {}, { headers }));
  }

  evaluation(id: string): Promise<Evaluation> {
    return this.get<Evaluation>(`/api/evaluations/${id}`);
  }

  series(id: string, scenarioId: string): Promise<TimeSeries> {
    return this.get<TimeSeries>(`/api/evaluations/${id}/scenarios/${encodeURIComponent(scenarioId)}/series`);
  }

  reference(id: string): Promise<ReferenceView> {
    return this.get<ReferenceView>(`/api/evaluations/${id}/reference`);
  }

  history(): Promise<HistoryItem[]> {
    return this.get<HistoryItem[]>('/api/evaluations?limit=50');
  }

  progress(): Promise<{ summary: ProgressSummary; recent: { amount: number; createdAt: string; sourceId: string }[] }> {
    return this.get('/api/me/progress');
  }

  achievements(): Promise<Achievement[]> {
    return this.get<Achievement[]>('/api/me/achievements');
  }

  competencies(): Promise<Competency[]> {
    return this.get<Competency[]>('/api/me/competencies');
  }

  palette(provider: Provider): Promise<Palette> {
    return this.get<Palette>(`/api/catalog/${provider}`);
  }

  exportAttempt(id: string): Observable<unknown> {
    return this.http.get(`/api/attempts/${id}/export`);
  }

  private get<T>(url: string): Promise<T> {
    return firstValueFrom(this.http.get<T>(url));
  }

  private post<T>(url: string, body: unknown): Promise<T> {
    return firstValueFrom(this.http.post<T>(url, body));
  }
}
