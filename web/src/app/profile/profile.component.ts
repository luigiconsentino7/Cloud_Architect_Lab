import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { competencyName, fmt, rankClass } from '../core/format';
import { Achievement, Competency, HistoryItem, ProgressSummary } from '../core/models';
import { RealtimeService } from '../core/realtime.service';

@Component({
  selector: 'lab-profile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    @if (progress(); as p) {
      <section class="level">
        <div>
          <h1>Nível {{ p.level }} · {{ p.title }}</h1>
          <p class="muted">{{ fmt.int(p.totalXp) }} XP · faltam {{ fmt.int(p.nextLevelXp - p.totalXp) }} para o próximo nível · streak de {{ p.streakDays }} dia(s)</p>
        </div>
        <div class="xpbar"><div [style.width]="percent(p) + '%'"></div></div>
      </section>
    }

    <div class="grid">
      <section class="block">
        <h2>Competências</h2>
        @for (c of competencies(); track c.competency) {
          <div class="competency">
            <span>{{ competencyName[c.competency] ?? c.competency }}</span>
            <div class="bar"><div [style.width]="c.rating + '%'"></div></div>
            <span class="value">{{ fmt.int(c.rating) }}</span>
            <small class="muted">{{ c.samples }} aval.</small>
          </div>
        } @empty {
          <p class="muted">Submeta um desafio para começar o radar de competências.</p>
        }
      </section>

      <section class="block">
        <h2>Conquistas</h2>
        <div class="achievements">
          @for (a of achievements(); track a.code) {
            <div class="achievement" [class.locked]="!a.unlocked" [title]="a.criterion">
              <strong>{{ a.unlocked ? '🏆' : '🔒' }} {{ a.title }}</strong>
              <small>{{ a.unlocked ? fmt.date(a.unlockedAt) : a.criterion }}</small>
            </div>
          }
        </div>
      </section>
    </div>

    <section class="block">
      <h2>Histórico</h2>
      <table>
        <tbody>
          @for (h of history(); track h.evaluationId) {
            <tr>
              <td><span [class]="rankClass(h.rank) + ' mini'">{{ h.rank ?? '…' }}</span></td>
              <td><a [routerLink]="['/avaliacoes', h.evaluationId]">{{ h.title || 'Desafio' }}</a></td>
              <td>{{ h.kind === 'submission' ? 'submissão' : 'rascunho' }}</td>
              <td class="value">{{ h.finalScore ?? '—' }}</td>
              <td class="muted">{{ fmt.date(h.createdAt) }}</td>
            </tr>
          } @empty {
            <tr><td class="muted">Nenhuma avaliação ainda.</td></tr>
          }
        </tbody>
      </table>
    </section>
  `,
  styles: `
    :host { display: block; max-width: 1100px; margin: 0 auto; padding: 22px 20px 60px; }
    h1 { font-size: 22px; margin: 0; }
    h2 { font-size: 16px; margin: 0 0 10px; }
    .muted { color: var(--muted); }
    .level { margin-bottom: 14px; }
    .xpbar { height: 12px; border-radius: 999px; background: var(--line); overflow: hidden; margin-top: 8px; }
    .xpbar div { height: 100%; background: linear-gradient(90deg, var(--accent), #ae3ec9); }
    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
    .block { border: 1px solid var(--line); border-radius: 12px; padding: 14px 16px; background: var(--surface); margin-bottom: 14px; }
    .competency { display: grid; grid-template-columns: 170px 1fr 36px 64px; gap: 8px; align-items: center; font-size: 13px; margin: 6px 0; }
    .bar { height: 8px; border-radius: 999px; background: var(--line); overflow: hidden; }
    .bar div { height: 100%; background: var(--ok); }
    .value { text-align: right; font-weight: 700; }
    .achievements { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    .achievement { border: 1px solid var(--line); border-radius: 8px; padding: 6px 8px; font-size: 13px; display: flex; flex-direction: column; }
    .achievement.locked { opacity: 0.6; }
    .achievement small { color: var(--muted); }
    table { width: 100%; border-collapse: collapse; font-size: 13.5px; }
    td { padding: 6px 4px; border-bottom: 1px solid var(--line); }
    .mini { width: 26px; height: 26px; border-radius: 7px; font-size: 14px; display: grid; place-items: center; color: white; font-weight: 800; }
    @media (max-width: 860px) { .grid { grid-template-columns: 1fr; } }
  `,
})
export class ProfileComponent {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);

  protected readonly fmt = fmt;
  protected readonly competencyName = competencyName;
  protected readonly rankClass = rankClass;
  protected readonly progress = signal<ProgressSummary | null>(null);
  protected readonly competencies = signal<Competency[]>([]);
  protected readonly achievements = signal<Achievement[]>([]);
  protected readonly history = signal<HistoryItem[]>([]);

  constructor() {
    effect(() => {
      this.realtime.xpVersion();
      void this.load();
    });
  }

  protected percent(p: ProgressSummary): number {
    const span = Math.max(1, p.nextLevelXp - p.levelStartXp);
    return Math.min(100, Math.max(0, ((p.totalXp - p.levelStartXp) / span) * 100));
  }

  private async load(): Promise<void> {
    const [progress, competencies, achievements, history] = await Promise.all([
      this.api.progress(),
      this.api.competencies(),
      this.api.achievements(),
      this.api.history(),
    ]);
    this.progress.set(progress.summary);
    this.competencies.set(competencies);
    this.achievements.set(achievements);
    this.history.set(history);
  }
}
