import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService, problemMessage } from '../core/api.service';
import { difficultyName, fmt, modeName, providerName, trackName } from '../core/format';
import { ChallengeSummary, Difficulty, GameMode, LibraryItem, Provider } from '../core/models';

const difficultyOrder: Difficulty[] = ['junior', 'pleno', 'senior', 'staff'];

@Component({
  selector: 'lab-home',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <section class="hero">
      <h1>Qual problema de System Design você quer resolver hoje?</h1>
      <p class="muted">Descreva o contexto ou o tema (ex.: "idempotência no PIX", "pico de Black Friday", "ordem de cotações") e escolha a nuvem.</p>
      <div class="controls">
        <input
          type="search"
          class="query"
          placeholder="Contexto ou tema do desafio…"
          [value]="query()"
          (input)="query.set($any($event.target).value)"
        />
        <div class="providers" role="radiogroup" aria-label="Provedor de nuvem">
          @for (p of providers; track p) {
            <button type="button" role="radio" [attr.aria-checked]="provider() === p" [class.active]="provider() === p" (click)="setProvider(p)">
              {{ providerName[p] }}
            </button>
          }
        </div>
        <select [value]="difficulty()" (change)="difficulty.set($any($event.target).value)" aria-label="Dificuldade">
          @for (d of difficulties; track d) {
            <option [value]="d">{{ difficultyName[d] }}</option>
          }
        </select>
        <select [value]="mode()" (change)="mode.set($any($event.target).value)" aria-label="Modo">
          <option value="treino">Treino (com simulação de rascunho)</option>
          <option value="entrevista">Entrevista (sem rascunho, XP ×1,3)</option>
        </select>
      </div>
      @if (error()) {
        <p class="error">{{ error() }}</p>
      }
    </section>

    <section class="results">
      @for (item of results(); track item.seedId) {
        <article class="card">
          <header>
            <span class="track">{{ trackName[item.track] ?? item.track }}</span>
            <h2>{{ item.title }}</h2>
          </header>
          <p>{{ item.summary }}</p>
          <p class="meta">
            {{ levels(item) }}
            @if (item.matchedTerms.length > 0) {
              · por que: <em>{{ item.matchedTerms.join(', ') }}</em>
            }
          </p>
          <div class="tags">
            @for (tag of item.tags; track tag) {
              <span class="tag">{{ tag }}</span>
            }
          </div>
          @if (twistsFor(item).length > 0) {
            <details>
              <summary>Pegadinhas opcionais</summary>
              @for (twist of twistsFor(item); track twist.id) {
                <label class="check">
                  <input type="checkbox" [checked]="isTwistOn(item.seedId, twist.id)" (change)="toggleTwist(item.seedId, twist.id, $any($event.target).checked)" />
                  <span><strong>{{ twist.title }}</strong> — {{ twist.description }}</span>
                </label>
              }
            </details>
          }
          <footer>
            @if (!supports(item)) {
              <span class="muted">Não tem nível {{ difficultyName[difficulty()] }}.</span>
            }
            <button type="button" class="primary" [disabled]="!supports(item) || !!starting()" (click)="start(item)">
              {{ starting() === item.seedId ? 'Montando…' : 'Começar em ' + providerName[provider()] }}
            </button>
          </footer>
        </article>
      } @empty {
        <p class="muted">Nenhum desafio casou com a busca. Tente outros termos (pix, ledger, cartão, ordens, antifraude…).</p>
      }
    </section>

    @if (recent().length > 0) {
      <section class="recent">
        <h2>Continuar</h2>
        <ul>
          @for (c of recent(); track c.id) {
            <li>
              <a [routerLink]="['/desafios', c.id]">{{ c.title }}</a>
              <span class="muted">{{ providerName[c.provider] }} · {{ difficultyName[c.difficulty] }} · {{ modeName[c.mode] }} · {{ fmt.date(c.createdAt) }}</span>
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: `
    :host { display: block; max-width: 1180px; margin: 0 auto; padding: 24px 20px 60px; }
    .hero h1 { font-size: 26px; margin: 0 0 6px; }
    .controls { display: grid; grid-template-columns: 1fr auto auto auto; gap: 10px; margin-top: 14px; align-items: center; }
    .query { font: inherit; font-size: 16px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); color: var(--text); }
    select { font: inherit; padding: 9px 8px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); color: var(--text); }
    .providers { display: flex; border: 1px solid var(--line); border-radius: 10px; overflow: hidden; }
    .providers button { border: 0; border-radius: 0; padding: 9px 14px; background: var(--surface); }
    .providers button.active { background: var(--accent); color: white; }
    .results { display: grid; grid-template-columns: repeat(auto-fill, minmax(330px, 1fr)); gap: 14px; margin-top: 22px; }
    .card { display: flex; flex-direction: column; border: 1px solid var(--line); border-radius: 12px; padding: 14px 16px; background: var(--surface); }
    .card h2 { font-size: 17px; margin: 2px 0 6px; }
    .card p { margin: 4px 0; font-size: 13.5px; }
    .track { font-size: 11px; text-transform: uppercase; letter-spacing: 0.05em; color: var(--accent); }
    .meta { color: var(--muted); }
    .tags { display: flex; flex-wrap: wrap; gap: 4px; margin: 6px 0; }
    .tag { font-size: 11px; padding: 1px 8px; border-radius: 999px; background: var(--line); }
    details { font-size: 13px; margin: 6px 0; }
    .check { display: flex; gap: 6px; margin: 4px 0; align-items: flex-start; }
    footer { margin-top: auto; padding-top: 10px; display: flex; justify-content: flex-end; align-items: center; gap: 8px; }
    .recent { margin-top: 34px; }
    .recent h2 { font-size: 18px; }
    .recent ul { list-style: none; padding: 0; }
    .recent li { display: flex; justify-content: space-between; padding: 7px 0; border-bottom: 1px solid var(--line); font-size: 14px; }
    .muted { color: var(--muted); }
    .error { color: var(--danger); }
    @media (max-width: 860px) { .controls { grid-template-columns: 1fr; } }
  `,
})
export class HomeComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  protected readonly fmt = fmt;
  protected readonly providerName = providerName;
  protected readonly difficultyName = difficultyName;
  protected readonly modeName = modeName;
  protected readonly trackName = trackName;
  protected readonly providers: Provider[] = ['aws', 'azure', 'gcp'];
  protected readonly difficulties = difficultyOrder;

  protected readonly query = signal('');
  protected readonly provider = signal<Provider>('aws');
  protected readonly difficulty = signal<Difficulty>('senior');
  protected readonly mode = signal<GameMode>('treino');
  protected readonly results = signal<LibraryItem[]>([]);
  protected readonly recent = signal<ChallengeSummary[]>([]);
  protected readonly starting = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  private readonly twists = signal<Record<string, string[]>>({});
  private searchTimer?: ReturnType<typeof setTimeout>;

  protected readonly levelIndex = computed(() => difficultyOrder.indexOf(this.difficulty()));

  constructor() {
    void this.api.me().then((me) => this.provider.set(me.defaultProvider)).catch(() => undefined);
    void this.api.recentChallenges().then((list) => this.recent.set(list)).catch(() => undefined);
    effect(() => {
      const q = this.query();
      clearTimeout(this.searchTimer);
      this.searchTimer = setTimeout(() => void this.search(q), 200);
    });
  }

  protected setProvider(provider: Provider): void {
    this.provider.set(provider);
    void this.api.updatePreferences({ defaultProvider: provider }).catch(() => undefined);
  }

  protected supports(item: LibraryItem): boolean {
    return item.difficulties.length === 0 || item.difficulties.includes(this.difficulty());
  }

  protected levels(item: LibraryItem): string {
    return item.difficulties.map((d) => difficultyName[d]).join(' · ');
  }

  protected twistsFor(item: LibraryItem) {
    return item.twists.filter((t) => difficultyOrder.indexOf(t.minDifficulty) <= this.levelIndex());
  }

  protected isTwistOn(seedId: string, twistId: string): boolean {
    return this.twists()[seedId]?.includes(twistId) ?? false;
  }

  protected toggleTwist(seedId: string, twistId: string, on: boolean): void {
    this.twists.update((all) => {
      const current = new Set(all[seedId] ?? []);
      if (on) current.add(twistId);
      else current.delete(twistId);
      return { ...all, [seedId]: [...current] };
    });
  }

  protected async start(item: LibraryItem): Promise<void> {
    this.starting.set(item.seedId);
    this.error.set(null);
    try {
      const allowed = new Set(this.twistsFor(item).map((t) => t.id));
      const challenge = await this.api.createChallenge({
        seedId: item.seedId,
        provider: this.provider(),
        difficulty: this.difficulty(),
        mode: this.mode(),
        twists: (this.twists()[item.seedId] ?? []).filter((t) => allowed.has(t)),
      });
      await this.router.navigate(['/desafios', challenge.id]);
    } catch (error) {
      this.error.set(problemMessage(error));
    } finally {
      this.starting.set(null);
    }
  }

  private async search(q: string): Promise<void> {
    try {
      this.results.set(await this.api.library(q.trim()));
    } catch (error) {
      this.error.set(problemMessage(error));
    }
  }
}
