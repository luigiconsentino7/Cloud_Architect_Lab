import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { ApiService, problemMessage } from '../core/api.service';
import { difficultyName, fmt, modeName, providerName } from '../core/format';
import { Challenge, ChallengeOptions, Difficulty, GameMode, Provider } from '../core/models';
import { allowedTwists, clampVariables, rescaleVariables } from './adjust';

/** "Ajustar": nuvem, nível, modo, números e pegadinhas da mesma semente, ou um pedido em texto para a IA local. */
@Component({
  selector: 'lab-adjust-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (options(); as o) {
      <section class="adjust" aria-label="Ajustar o desafio">
        <h3>Ajustar o desafio</h3>
        <p class="muted">Cria um desafio novo da mesma semente. O atual continua no histórico.</p>

        <div class="field">
          <span>Nuvem</span>
          <div class="segmented" role="radiogroup" aria-label="Nuvem">
            @for (p of providers; track p) {
              <button type="button" role="radio" [attr.aria-checked]="provider() === p" [class.active]="provider() === p" (click)="provider.set(p)">
                {{ providerName[p] }}
              </button>
            }
          </div>
        </div>

        <label class="field">
          <span>Nível</span>
          <select (change)="setDifficulty($any($event.target).value)" aria-label="Nível do desafio">
            @for (d of o.difficulties; track d) {
              <option [value]="d" [selected]="d === difficulty()">{{ difficultyName[d] }}</option>
            }
          </select>
        </label>

        <label class="field">
          <span>Modo</span>
          <select (change)="mode.set($any($event.target).value)" aria-label="Modo do desafio">
            <option value="treino" [selected]="mode() === 'treino'">{{ modeName.treino }}</option>
            <option value="entrevista" [selected]="mode() === 'entrevista'">{{ modeName.entrevista }}</option>
          </select>
        </label>

        @for (v of variableList(); track v.name) {
          <label class="field range">
            <span>{{ v.description }}</span>
            <input
              type="range"
              [min]="v.min"
              [max]="v.max"
              [step]="v.step"
              [value]="variables()[v.name]"
              (input)="setVariable(v.name, $any($event.target).value)"
              [attr.aria-label]="v.description"
            />
            <output>{{ fmt.n(variables()[v.name]) }}</output>
          </label>
        }

        @if (twistList().length > 0) {
          <fieldset>
            <legend>Pegadinhas</legend>
            @for (t of twistList(); track t.id) {
              <label class="check">
                <input type="checkbox" [checked]="twists().includes(t.id)" (change)="toggleTwist(t.id, $any($event.target).checked)" />
                <span><strong>{{ t.title }}</strong> — {{ t.description }}</span>
              </label>
            }
          </fieldset>
        }

        <div class="buttons">
          <button type="button" class="primary" [disabled]="busy()" (click)="apply()">{{ busy() === 'form' ? 'Montando…' : 'Criar desafio ajustado' }}</button>
          <button type="button" [disabled]="!!busy()" (click)="closed.emit()">Cancelar</button>
        </div>

        @if (o.aiAvailable) {
          <div class="ai">
            <h4>Ou peça à IA local</h4>
            <textarea
              rows="2"
              placeholder="Ex.: mais volume, com auditoria obrigatória e orçamento apertado"
              [value]="instruction()"
              (input)="instruction.set($any($event.target).value)"
              aria-label="Pedido de ajuste para a IA"
            ></textarea>
            <button type="button" [disabled]="!!busy() || !instruction().trim()" (click)="applyWithAi()">
              {{ busy() === 'ai' ? 'A IA local está ajustando…' : 'Ajustar com a IA' }}
            </button>
          </div>
        }

        @if (error()) {
          <p class="error">{{ error() }}</p>
        }
      </section>
    } @else if (error()) {
      <p class="error">{{ error() }}</p>
    } @else {
      <p class="muted">Carregando as opções…</p>
    }
  `,
  styles: `
    .adjust { border: 1px solid var(--line); border-radius: 10px; padding: 10px 12px; margin: 8px 0 14px; background: var(--surface); display: flex; flex-direction: column; gap: 9px; }
    h3 { margin: 0; font-size: 15px; }
    h4 { margin: 6px 0 4px; font-size: 13px; }
    p { margin: 0; font-size: 12.5px; }
    .field { display: grid; grid-template-columns: 84px 1fr; align-items: center; gap: 8px; font-size: 13px; }
    .field.range { grid-template-columns: 1fr 64px; }
    .field.range span { grid-column: 1 / -1; color: var(--muted); font-size: 12px; }
    .field.range output { text-align: right; font-variant-numeric: tabular-nums; }
    select, textarea { font: inherit; font-size: 13px; padding: 5px 7px; border: 1px solid var(--line); border-radius: 7px; background: var(--surface); color: var(--text); }
    textarea { width: 100%; box-sizing: border-box; resize: vertical; }
    .segmented { display: flex; border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
    .segmented button { flex: 1; border: 0; border-radius: 0; padding: 5px 6px; font-size: 12px; background: var(--surface); }
    .segmented button.active { background: var(--accent); color: white; }
    fieldset { border: 0; padding: 0; margin: 0; font-size: 12.5px; }
    legend { color: var(--muted); font-size: 12px; margin-bottom: 4px; }
    .check { display: flex; gap: 6px; align-items: flex-start; margin: 3px 0; }
    .buttons { display: flex; gap: 8px; }
    .ai { border-top: 1px solid var(--line); padding-top: 8px; display: flex; flex-direction: column; gap: 6px; align-items: flex-start; }
    .muted { color: var(--muted); }
    .error { color: var(--danger); }
  `,
})
export class AdjustPanelComponent {
  readonly challenge = input.required<Challenge>();
  /** O desafio novo, para a tela navegar até ele. */
  readonly created = output<Challenge>();
  readonly closed = output<void>();

  private readonly api = inject(ApiService);

  protected readonly fmt = fmt;
  protected readonly providerName = providerName;
  protected readonly difficultyName = difficultyName;
  protected readonly modeName = modeName;
  protected readonly providers: Provider[] = ['aws', 'azure', 'gcp'];

  protected readonly options = signal<ChallengeOptions | null>(null);
  protected readonly provider = signal<Provider>('aws');
  protected readonly difficulty = signal<Difficulty>('senior');
  protected readonly mode = signal<GameMode>('treino');
  protected readonly variables = signal<Record<string, number>>({});
  protected readonly twists = signal<string[]>([]);
  protected readonly instruction = signal('');
  protected readonly busy = signal<'form' | 'ai' | null>(null);
  protected readonly error = signal<string | null>(null);

  protected readonly twistList = computed(() => {
    const o = this.options();
    return o ? allowedTwists(o, this.difficulty()) : [];
  });
  protected readonly variableList = computed(() => {
    const o = this.options();
    if (!o) return [];
    return Object.entries(o.variables)
      .filter(([, v]) => v.ranges[this.difficulty()])
      .map(([name, v]) => ({ name, description: v.description, step: v.step > 0 ? v.step : 1, min: v.ranges[this.difficulty()]![0], max: v.ranges[this.difficulty()]![1] }));
  });

  constructor() {
    effect(() => {
      const id = this.challenge().id;
      untracked(() => void this.load(id));
    });
  }

  protected setDifficulty(difficulty: Difficulty): void {
    const o = this.options();
    const previous = this.difficulty();
    this.difficulty.set(difficulty);
    if (!o) return;
    this.variables.set(rescaleVariables(o, previous, difficulty, this.variables()));
    const allowed = new Set(allowedTwists(o, difficulty).map((t) => t.id));
    this.twists.update((list) => list.filter((t) => allowed.has(t)));
  }

  protected setVariable(name: string, value: string): void {
    this.variables.update((all) => ({ ...all, [name]: Number(value) }));
  }

  protected toggleTwist(id: string, on: boolean): void {
    this.twists.update((list) => (on ? [...new Set([...list, id])] : list.filter((t) => t !== id)));
  }

  protected async apply(): Promise<void> {
    await this.run('form', () =>
      this.api.anotherVariation(this.challenge().id, {
        provider: this.provider(),
        difficulty: this.difficulty(),
        mode: this.mode(),
        twists: this.twists(),
        variables: this.variables(),
      }),
    );
  }

  protected async applyWithAi(): Promise<void> {
    await this.run('ai', () =>
      this.api.anotherVariation(this.challenge().id, {
        provider: this.provider(),
        difficulty: this.difficulty(),
        mode: this.mode(),
        instruction: this.instruction().trim(),
      }),
    );
  }

  private async run(kind: 'form' | 'ai', action: () => Promise<Challenge>): Promise<void> {
    this.busy.set(kind);
    this.error.set(null);
    try {
      this.created.emit(await action());
    } catch (error) {
      this.error.set(problemMessage(error));
    } finally {
      this.busy.set(null);
    }
  }

  private async load(id: string): Promise<void> {
    try {
      const o = await this.api.challengeOptions(id);
      this.options.set(o);
      this.provider.set(o.provider);
      this.difficulty.set(o.difficulty);
      this.mode.set(o.mode === 'plantao' ? 'treino' : o.mode);
      this.variables.set(clampVariables(o, o.difficulty, o.currentVariables));
      this.twists.set(o.currentTwists);
    } catch (error) {
      this.error.set(problemMessage(error));
    }
  }
}
