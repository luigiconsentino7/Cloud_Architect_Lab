import { ChangeDetectionStrategy, Component, OnDestroy, computed, inject, input, signal, viewChild } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService, problemMessage } from '../core/api.service';
import { difficultyName, fmt, metricName, modeName, providerName, sloTarget } from '../core/format';
import { Attempt, Challenge, FlowDefinition, Hint, Palette, PaletteComponent, SceneElement, Validation } from '../core/models';
import { CanvasComponent } from './canvas.component';
import { Selection, componentData, componentSkeleton, connectionIdOf, elementsForIrIds, externalSkeleton, freePosition, live, nextComponentId, normalize, selectionOf, withCustomData } from './canvas-adapter';
import { FlowsComponent } from './flows.component';
import { CustomDataChange, InspectorComponent } from './inspector.component';

type Tab = 'brief' | 'palette' | 'flows' | 'problems';
type SaveState = 'saved' | 'dirty' | 'saving' | 'conflict' | 'error';

const categoryNames: Record<string, string> = {
  borda: 'Borda e rede',
  computacao: 'Computação',
  dados: 'Dados',
  mensageria: 'Mensageria',
  seguranca: 'Segurança',
  observabilidade: 'Observabilidade',
  clientes: 'Clientes',
};

@Component({
  selector: 'lab-workspace',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CanvasComponent, FlowsComponent, InspectorComponent, RouterLink],
  templateUrl: './workspace.component.html',
  styleUrl: './workspace.component.css',
})
export class WorkspaceComponent implements OnDestroy {
  /** Id do desafio (rota /desafios/:id). */
  readonly id = input.required<string>();

  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly canvas = viewChild(CanvasComponent);

  protected readonly fmt = fmt;
  protected readonly providerName = providerName;
  protected readonly difficultyName = difficultyName;
  protected readonly modeName = modeName;
  protected readonly metricName = metricName;
  protected readonly sloTarget = sloTarget;

  protected readonly challenge = signal<Challenge | null>(null);
  protected readonly attempt = signal<Attempt | null>(null);
  protected readonly palette = signal<Palette | null>(null);
  protected readonly elements = signal<readonly SceneElement[]>([]);
  protected readonly selectedIds = signal<string[]>([]);
  protected readonly flows = signal<FlowDefinition[]>([]);
  protected readonly validation = signal<Validation | null>(null);
  protected readonly tab = signal<Tab>('brief');
  protected readonly saveState = signal<SaveState>('saved');
  protected readonly busy = signal<string | null>(null);
  protected readonly message = signal<string | null>(null);
  protected readonly hints = signal<Hint[]>([]);
  protected readonly paletteFilter = signal('');
  protected readonly loadError = signal<string | null>(null);

  protected readonly selection = computed<Selection>(() => selectionOf(this.elements(), this.selectedIds()));
  protected readonly selectedConnection = computed(() => {
    const sel = this.selection();
    return sel?.kind === 'connection' ? connectionIdOf(sel.arrow) : null;
  });
  protected readonly editable = computed(() => this.attempt()?.status === 'draft');
  protected readonly errors = computed(() => this.validation()?.diagnostics.filter((d) => d.severity === 'error').length ?? 0);
  protected readonly paletteGroups = computed(() => {
    const filter = normalize(this.paletteFilter().trim());
    const groups = new Map<string, PaletteComponent[]>();
    for (const component of this.palette()?.components ?? []) {
      if (component.archetype === 'external.system') continue;
      const haystack = normalize(`${component.displayName} ${component.shortName ?? ''} ${component.aliases.join(' ')} ${component.archetype}`);
      if (filter && !haystack.includes(filter)) continue;
      const key = component.archetype === 'client' ? 'clientes' : normalize(component.category);
      groups.set(key, [...(groups.get(key) ?? []), component]);
    }
    return [...groups.entries()].map(([key, items]) => ({ key, name: categoryNames[key] ?? key, items }));
  });

  private saveTimer?: ReturnType<typeof setTimeout>;
  private flowsTimer?: ReturnType<typeof setTimeout>;
  private pendingScene = false;

  constructor() {
    queueMicrotask(() => void this.load());
  }

  ngOnDestroy(): void {
    if (this.pendingScene) void this.saveNow();
    clearTimeout(this.saveTimer);
    clearTimeout(this.flowsTimer);
  }

  protected setTab(tab: Tab): void {
    this.tab.set(tab);
  }

  protected onScene(elements: readonly SceneElement[]): void {
    this.elements.set(elements);
    if (!this.editable()) return;
    this.pendingScene = true;
    this.saveState.set('dirty');
    clearTimeout(this.saveTimer);
    this.saveTimer = setTimeout(() => void this.saveNow(), 1200);
  }

  protected onSelection(ids: string[]): void {
    this.selectedIds.set(ids);
  }

  protected add(component: PaletteComponent): void {
    const canvas = this.canvas();
    if (!canvas || !this.editable()) return;
    const elements = canvas.elements();
    const componentId = nextComponentId(elements, component.id);
    canvas.add([componentSkeleton(component, freePosition(elements, canvas.viewportCenter()), componentId, this.palette()?.catalogVersion ?? '')]);
  }

  protected addExternal(index: number): void {
    const canvas = this.canvas();
    const system = this.challenge()?.externalSystems[index];
    if (!canvas || !system || !this.editable()) return;
    canvas.add([externalSkeleton(system, freePosition(canvas.elements(), canvas.viewportCenter()), this.palette()?.catalogVersion ?? '')]);
  }

  protected onCustomData(change: CustomDataChange): void {
    if (!this.editable()) return;
    this.canvas()?.replace([withCustomData(change.element, change.customData)]);
  }

  protected onFlows(flows: FlowDefinition[]): void {
    this.flows.set(flows);
    const attempt = this.attempt();
    if (!attempt || !this.editable()) return;
    clearTimeout(this.flowsTimer);
    this.flowsTimer = setTimeout(() => void this.api.saveFlows(attempt.id, flows).catch((e) => this.message.set(problemMessage(e))), 400);
  }

  protected highlight(irIds: string[], elementIds: string[] = []): void {
    const ids = [...elementIds, ...elementsForIrIds(this.canvas()?.elements() ?? [], irIds)];
    if (ids.length > 0) this.canvas()?.select(ids);
  }

  protected highlightPair(nodeIds: string[], edgeIds: string[]): void {
    this.highlight([...nodeIds, ...edgeIds]);
  }

  protected scenarioNames(challenge: Challenge): string {
    return challenge.scenarios.map((s) => s.name || s.id).join(', ');
  }

  protected async validate(): Promise<void> {
    const attempt = this.attempt();
    if (!attempt) return;
    await this.run('Validando…', async () => {
      await this.flushAll();
      this.validation.set(await this.api.validate(attempt.id));
      this.tab.set('problems');
    });
  }

  protected async simulate(): Promise<void> {
    const attempt = this.attempt();
    if (!attempt) return;
    await this.run('Enviando para simulação…', async () => {
      await this.flushAll();
      const ticket = await this.api.simulate(attempt.id);
      await this.router.navigate(['/avaliacoes', ticket.evaluationId]);
    });
  }

  protected async submit(): Promise<void> {
    const attempt = this.attempt();
    if (!attempt) return;
    if (!confirm('Submeter a tentativa? O desenho fica congelado e roda contra todos os cenários, inclusive os ocultos.')) return;
    await this.run('Submetendo…', async () => {
      await this.flushAll();
      const key = idempotencyKey(attempt.id);
      const ticket = await this.api.submit(attempt.id, key);
      await this.router.navigate(['/avaliacoes', ticket.evaluationId]);
    });
  }

  protected async hint(): Promise<void> {
    const attempt = this.attempt();
    if (!attempt) return;
    if (!confirm('Abrir a próxima dica? Cada dica reduz o XP desta tentativa (10%, 20% e 35%).')) return;
    await this.run('Buscando a dica…', async () => {
      const hint = await this.api.hint(attempt.id);
      this.hints.update((list) => [...list, hint]);
      this.attempt.set({ ...attempt, hintLevelsUsed: hint.hintLevelsUsed });
      this.tab.set('brief');
    });
  }

  protected async newAttempt(): Promise<void> {
    const challenge = this.challenge();
    if (!challenge) return;
    await this.run('Abrindo nova tentativa…', async () => {
      this.attempt.set(await this.api.startAttempt(challenge.id));
      this.flows.set([]);
      this.validation.set(null);
      this.hints.set([]);
    });
  }

  protected async keepMine(): Promise<void> {
    const attempt = this.attempt();
    if (!attempt) return;
    const fresh = await this.api.attempt(attempt.id);
    this.attempt.set({ ...attempt, draftVersion: fresh.draftVersion });
    await this.saveNow();
  }

  protected reloadTheirs(): void {
    location.reload();
  }

  private async load(): Promise<void> {
    try {
      const challenge = await this.api.challenge(this.id());
      this.challenge.set(challenge);
      const [palette, attempts] = await Promise.all([this.api.palette(challenge.provider), this.api.attempts(challenge.id)]);
      this.palette.set(palette);
      const latest = attempts.at(-1);
      const attempt = latest ?? (await this.api.startAttempt(challenge.id));
      this.flows.set(attempt.flows);
      if (attempt.scene.elements.length === 0 && attempt.status === 'draft') {
        attempt.scene = { ...attempt.scene, elements: [] };
        this.attempt.set(attempt);
        queueMicrotask(() => this.placeStarters(challenge, palette));
      } else {
        this.attempt.set(attempt);
      }
    } catch (error) {
      this.loadError.set(problemMessage(error));
    }
  }

  /** Canvas vazio: já coloca os clientes de entrada e os sistemas externos do desafio (com os ids certos). */
  private placeStarters(challenge: Challenge, palette: Palette): void {
    const canvas = this.canvas();
    if (!canvas) {
      setTimeout(() => this.placeStarters(challenge, palette), 200);
      return;
    }
    if (live(canvas.elements()).some((e) => componentData(e))) return;
    const skeletons: Record<string, unknown>[] = [];
    const clients = [...new Set(challenge.useCases.map((u) => u.entryClient))];
    clients.forEach((clientId, i) => {
      const client = palette.components.find((c) => c.id === clientId);
      if (client) skeletons.push(componentSkeleton(client, { x: 60, y: 80 + i * 140 }, nextComponentId([], clientId), palette.catalogVersion));
    });
    challenge.externalSystems.forEach((system, i) => skeletons.push(externalSkeleton(system, { x: 900, y: 80 + i * 140 }, palette.catalogVersion)));
    if (skeletons.length > 0) canvas.add(skeletons, { select: false, fit: true });
  }

  private async flushAll(): Promise<void> {
    clearTimeout(this.saveTimer);
    if (this.pendingScene) await this.saveNow();
    const attempt = this.attempt();
    if (attempt) {
      clearTimeout(this.flowsTimer);
      await this.api.saveFlows(attempt.id, this.flows());
    }
  }

  private async saveNow(): Promise<void> {
    const attempt = this.attempt();
    const canvas = this.canvas();
    if (!attempt || !canvas || !this.editable()) return;
    this.saveState.set('saving');
    this.pendingScene = false;
    try {
      const result = await this.api.saveDraft(attempt.id, canvas.snapshot(), attempt.draftVersion);
      this.attempt.set({ ...attempt, draftVersion: result.draftVersion });
      this.saveState.set(this.pendingScene ? 'dirty' : 'saved');
    } catch (error) {
      const status = (error as { status?: number }).status;
      this.saveState.set(status === 412 ? 'conflict' : 'error');
      this.pendingScene = true;
      if (status !== 412) this.message.set(problemMessage(error));
    }
  }

  private async run(label: string, action: () => Promise<void>): Promise<void> {
    this.busy.set(label);
    this.message.set(null);
    try {
      await action();
    } catch (error) {
      this.message.set(problemMessage(error));
    } finally {
      this.busy.set(null);
    }
  }
}

/** Mesma chave para a mesma tentativa enquanto a aba existir: reenviar não cria outra avaliação. */
function idempotencyKey(attemptId: string): string {
  const storageKey = `lab:submit:${attemptId}`;
  try {
    const existing = sessionStorage.getItem(storageKey);
    if (existing) return existing;
    const key = crypto.randomUUID();
    sessionStorage.setItem(storageKey, key);
    return key;
  } catch {
    return `${attemptId}-submit`;
  }
}
