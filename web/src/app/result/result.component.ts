import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, problemMessage } from '../core/api.service';
import { dimensionName, fmt, metricName, rankClass, severityName } from '../core/format';
import { CheckResult, Evaluation, ReferenceView, ScenarioResult, TimeSeries } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { CanvasComponent } from '../workspace/canvas.component';
import { ChartComponent, ChartSeries } from './chart.component';

const palette = ['#1c7ed6', '#e8590c', '#2f9e44', '#ae3ec9', '#f08c00'];

@Component({
  selector: 'lab-result',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ChartComponent, CanvasComponent],
  templateUrl: './result.component.html',
  styleUrl: './result.component.css',
})
export class ResultComponent implements OnDestroy {
  /** Id da avaliação (rota /avaliacoes/:id). */
  readonly id = input.required<string>();

  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);

  protected readonly fmt = fmt;
  protected readonly dimensionName = dimensionName;
  protected readonly severityName = severityName;
  protected readonly metricName = metricName;
  protected readonly rankClass = rankClass;

  protected readonly evaluation = signal<Evaluation | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly reference = signal<ReferenceView | null>(null);
  protected readonly showReference = signal(false);
  protected readonly series = signal<TimeSeries | null>(null);
  protected readonly selectedScenario = signal<string | null>(null);
  protected readonly expanded = signal<Set<string>>(new Set());

  protected readonly result = computed(() => this.evaluation()?.result ?? null);
  protected readonly running = computed(() => {
    const e = this.evaluation();
    return !e || e.status === 'queued' || e.status === 'running';
  });
  protected readonly progress = computed(() => {
    const live = this.realtime.progress();
    const e = this.evaluation();
    if (live && e && live.evaluationId === e.id && live.percent >= e.percent) return { percent: live.percent, detail: live.detail };
    return { percent: e?.percent ?? 0, detail: e?.detail ?? 'Na fila…' };
  });
  protected readonly checksByScenario = computed(() => {
    const groups = new Map<string, { name: string; hidden: boolean; checks: CheckResult[] }>();
    for (const check of this.result()?.score.checks ?? []) {
      const group = groups.get(check.scenarioId) ?? { name: check.scenarioName, hidden: check.visibility === 'hidden', checks: [] };
      group.checks.push(check);
      groups.set(check.scenarioId, group);
    }
    return [...groups.entries()].map(([id, g]) => ({ id, ...g, passed: g.checks.every((c) => c.status === 'passed') }));
  });
  protected readonly scenarios = computed<ScenarioResult[]>(() => this.result()?.simulation.scenarios ?? []);
  protected readonly charts = computed(() => this.buildCharts(this.series()));

  private poll?: ReturnType<typeof setInterval>;

  constructor() {
    effect(() => {
      const id = this.id();
      void this.load(id);
    });
    effect(() => {
      const done = this.realtime.completed();
      if (done && done.evaluationId === this.evaluation()?.id) void this.load(done.evaluationId);
    });
  }

  ngOnDestroy(): void {
    clearInterval(this.poll);
  }

  protected toggle(id: string): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  protected async openReference(): Promise<void> {
    if (!this.reference()) {
      try {
        this.reference.set(await this.api.reference(this.id()));
      } catch (error) {
        this.error.set(problemMessage(error));
        return;
      }
    }
    this.showReference.update((v) => !v);
  }

  protected async selectScenario(scenarioId: string): Promise<void> {
    this.selectedScenario.set(scenarioId);
    this.series.set(null);
    try {
      this.series.set(await this.api.series(this.id(), scenarioId));
    } catch {
      this.series.set(null);
    }
  }

  protected barWidth(score: number): string {
    return `${Math.max(2, Math.min(100, score))}%`;
  }

  protected checkValue(check: CheckResult): string {
    if (check.observed == null) return 'sem dados';
    if (check.metric === 'success-rate') return fmt.pct(check.observed);
    if (check.metric.startsWith('latency.') || check.metric.startsWith('async-completion.')) return fmt.ms(check.observed);
    if (check.metric === 'capacity.headroom') return fmt.pct(check.observed);
    return fmt.int(check.observed);
  }

  private async load(id: string): Promise<void> {
    try {
      const evaluation = await this.api.evaluation(id);
      this.evaluation.set(evaluation);
      if (evaluation.status === 'queued' || evaluation.status === 'running') {
        await this.realtime.joinAttempt(evaluation.attemptId);
        if (!this.poll) this.poll = setInterval(() => void this.refresh(id), 1500);
      } else {
        clearInterval(this.poll);
        this.poll = undefined;
        const first = evaluation.result?.simulation.scenarios[0]?.scenarioId;
        if (first && !this.selectedScenario()) void this.selectScenario(first);
      }
    } catch (error) {
      this.error.set(problemMessage(error));
    }
  }

  private async refresh(id: string): Promise<void> {
    const evaluation = await this.api.evaluation(id).catch(() => null);
    if (!evaluation) return;
    if (evaluation.status === 'completed' || evaluation.status === 'failed') await this.load(id);
    else this.evaluation.set(evaluation);
  }

  private buildCharts(series: TimeSeries | null): { title: string; series: ChartSeries[] }[] {
    if (!series) return [];
    const useCases = Object.entries(series.useCases);
    return [
      { title: 'Carga oferecida (req/s)', series: [{ name: 'oferecida', values: series.offeredRps, color: palette[0] }] },
      { title: 'p99 por caso de uso (ms)', series: useCases.map(([id, uc], i) => ({ name: id, values: uc.p99Ms, color: palette[i % palette.length] })) },
      { title: 'Erros por segundo', series: useCases.map(([id, uc], i) => ({ name: id, values: uc.errors, color: palette[(i + 1) % palette.length] })) },
    ];
  }
}
