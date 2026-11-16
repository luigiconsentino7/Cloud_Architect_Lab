import { Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';

export interface EvaluationProgressEvent {
  evaluationId: string;
  attemptId: string;
  stage: string;
  percent: number;
  detail: string;
}

export interface EvaluationCompletedEvent {
  evaluationId: string;
  attemptId: string;
  kind: string;
  finalScore: number;
  rank: string;
}

export interface Toast {
  id: number;
  kind: 'xp' | 'level' | 'achievement' | 'error';
  text: string;
}

/** Cliente do hub /hubs/lab. Os eventos viram signals; o app também consulta a API, então perder um evento não quebra nada. */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  readonly progress = signal<EvaluationProgressEvent | null>(null);
  readonly completed = signal<EvaluationCompletedEvent | null>(null);
  readonly toasts = signal<Toast[]>([]);
  /** Incrementa a cada XP creditado: quem mostra o progresso recarrega. */
  readonly xpVersion = signal(0);

  private connection?: HubConnection;
  private nextToast = 1;
  private readonly joined = new Set<string>();

  async start(): Promise<void> {
    if (this.connection) return;
    this.connection = new HubConnectionBuilder()
      .withUrl('/hubs/lab')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.on('EvaluationProgress', (e: EvaluationProgressEvent) => this.progress.set(e));
    this.connection.on('EvaluationCompleted', (e: EvaluationCompletedEvent) => this.completed.set(e));
    this.connection.on('EvaluationFailed', (e: { error: string }) => this.toast('error', `A avaliação falhou: ${e.error}`));
    this.connection.on('XpAwarded', (e: { amount: number; totalXp: number }) => {
      this.xpVersion.update((v) => v + 1);
      if (e.amount > 0) this.toast('xp', `+${e.amount} XP`);
    });
    this.connection.on('LevelUp', (e: { level: number; title: string }) => this.toast('level', `Nível ${e.level}: ${e.title}!`));
    this.connection.on('AchievementUnlocked', (e: { title: string }) => this.toast('achievement', `Conquista: ${e.title}`));
    this.connection.onreconnected(async () => {
      for (const id of this.joined) await this.connection?.invoke('JoinAttempt', id);
    });

    try {
      await this.connection.start();
    } catch {
      // sem tempo real (ex.: testes): as telas usam polling como alternativa
    }
  }

  async joinAttempt(attemptId: string): Promise<void> {
    this.joined.add(attemptId);
    await this.start();
    if (this.connection?.state === HubConnectionState.Connected) await this.connection.invoke('JoinAttempt', attemptId);
  }

  async leaveAttempt(attemptId: string): Promise<void> {
    this.joined.delete(attemptId);
    if (this.connection?.state === HubConnectionState.Connected) await this.connection.invoke('LeaveAttempt', attemptId);
  }

  toast(kind: Toast['kind'], text: string): void {
    const id = this.nextToast++;
    this.toasts.update((list) => [...list, { id, kind, text }]);
    setTimeout(() => this.toasts.update((list) => list.filter((t) => t.id !== id)), 6000);
  }
}
