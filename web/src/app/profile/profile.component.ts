import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, problemMessage } from '../core/api.service';
import { competencyName, fmt, rankClass } from '../core/format';
import { Achievement, Competency, HistoryItem, ProgressSummary } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { RadarComponent } from './radar.component';

@Component({
  selector: 'lab-profile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RadarComponent],
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
        @if (competencies().length >= 3) {
          <lab-radar [items]="radarItems()" />
        }
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

    <section class="block data">
      <h2>Seus dados</h2>
      <p class="muted">
        Tudo fica na sua máquina. O backup leva desafios, desenhos, avaliações, XP, conquistas e conversas; importar
        substitui os dados atuais (uma cópia do banco atual é guardada antes).
      </p>
      <div class="buttons">
        <button type="button" [disabled]="!!dataBusy()" (click)="exportBackup()">{{ dataBusy() === 'export' ? 'Exportando…' : 'Exportar backup' }}</button>
        <label class="button" [class.disabled]="!!dataBusy()">
          {{ dataBusy() === 'import' ? 'Importando…' : 'Importar backup' }}
          <input type="file" accept="application/json,.json" hidden [disabled]="!!dataBusy()" (change)="importBackup($any($event.target))" />
        </label>
      </div>
      @if (dataMessage()) {
        <p class="data-message" [class.error]="dataError()">{{ dataMessage() }}</p>
      }
    </section>

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
    .data .buttons { display: flex; gap: 8px; align-items: center; }
    .data label.button { cursor: pointer; }
    .data label.button.disabled { opacity: 0.6; pointer-events: none; }
    .data-message { font-size: 13px; margin: 8px 0 0; color: var(--ok); }
    .data-message.error { color: var(--danger); }
    lab-radar { margin: 4px 0 10px; }
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
  protected readonly radarItems = computed(() => this.competencies().map((c) => ({ label: competencyName[c.competency] ?? c.competency, value: c.rating })));
  protected readonly dataBusy = signal<'export' | 'import' | null>(null);
  protected readonly dataMessage = signal<string | null>(null);
  protected readonly dataError = signal(false);

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

  protected async exportBackup(): Promise<void> {
    this.dataBusy.set('export');
    this.dataMessage.set(null);
    try {
      const backup = await this.api.exportBackup();
      const url = URL.createObjectURL(new Blob([JSON.stringify(backup)], { type: 'application/json' }));
      const link = document.createElement('a');
      link.href = url;
      link.download = `cloud-architect-lab-backup-${new Date().toISOString().slice(0, 10)}.json`;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
      this.report('Backup exportado.', false);
    } catch (error) {
      this.report(problemMessage(error), true);
    } finally {
      this.dataBusy.set(null);
    }
  }

  protected async importBackup(input: HTMLInputElement): Promise<void> {
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    if (!confirm(`Importar "${file.name}"? Isso substitui todos os dados atuais (uma cópia do banco atual é guardada antes).`)) return;
    this.dataBusy.set('import');
    this.dataMessage.set(null);
    try {
      const result = await this.api.importBackup(JSON.parse(await file.text()));
      this.report(`Backup importado: ${fmt.int(result.challenges)} desafios e ${fmt.int(result.evaluations)} avaliações. Cópia do banco anterior: ${result.safetyCopy}.`, false);
      await this.load();
    } catch (error) {
      this.report(error instanceof SyntaxError ? 'O arquivo não é um JSON válido.' : problemMessage(error), true);
    } finally {
      this.dataBusy.set(null);
    }
  }

  private report(message: string, isError: boolean): void {
    this.dataMessage.set(message);
    this.dataError.set(isError);
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
