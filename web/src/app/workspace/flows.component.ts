import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { FlowDefinition, SceneElement, UseCaseSpec } from '../core/models';
import { anchorOf, componentData, connectionIdOf, connectionLabels, live } from './canvas-adapter';

/** Sugestão de fluxo: percorre as setas a partir do cliente de entrada do caso de uso (busca em largura). */
export function suggestFlow(elements: readonly SceneElement[], entryClient: string): string[] {
  const items = live(elements);
  const start = items.find((e) => componentData(e)?.catalogId === entryClient);
  if (!start) return [];
  const arrows = items.filter((e) => e.type === 'arrow' && e.startBinding && e.endBinding);
  const steps: string[] = [];
  const visited = new Set<string>([start.id]);
  const queue = [start.id];
  while (queue.length > 0) {
    const current = queue.shift()!;
    for (const arrow of arrows) {
      const from = anchorOf(items, arrow.startBinding!.elementId);
      const to = anchorOf(items, arrow.endBinding!.elementId);
      if (!from || !to || from.id !== current) continue;
      const id = connectionIdOf(arrow);
      if (!steps.includes(id)) steps.push(id);
      if (!visited.has(to.id)) {
        visited.add(to.id);
        queue.push(to.id);
      }
    }
  }
  return steps;
}

@Component({
  selector: 'lab-flows',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="muted">
      O fluxo de cada caso de uso é a ordem das setas que uma requisição percorre, a partir do cliente. Selecione uma seta no
      canvas e adicione, ou peça uma sugestão a partir do desenho.
    </p>
    @for (uc of useCases(); track uc.id) {
      <section class="flow">
        <header>
          <strong>{{ uc.id }} · {{ uc.name }}</strong>
          @if (uc.monetary) {
            <span class="tag money">monetário</span>
          }
        </header>
        <ol>
          @for (step of stepsOf(uc.id); track $index) {
            <li [class.missing]="!labels().has(step)" (click)="highlight.emit([step])">
              <span class="label">{{ labels().get(step) ?? '(seta apagada)' }}</span>
              <span class="actions">
                <button type="button" class="link" title="Subir" [disabled]="$first" (click)="move(uc.id, $index, -1); $event.stopPropagation()">↑</button>
                <button type="button" class="link" title="Descer" [disabled]="$last" (click)="move(uc.id, $index, 1); $event.stopPropagation()">↓</button>
                <button type="button" class="link" title="Remover" (click)="remove(uc.id, $index); $event.stopPropagation()">✕</button>
              </span>
            </li>
          } @empty {
            <li class="empty">Sem passos ainda.</li>
          }
        </ol>
        <div class="buttons">
          <button type="button" class="small" [disabled]="!selectedConnection()" (click)="addSelected(uc.id)">+ seta selecionada</button>
          <button type="button" class="small" (click)="suggest(uc)">Sugerir pelo desenho</button>
          @if (stepsOf(uc.id).length > 0) {
            <button type="button" class="small link" (click)="clear(uc.id)">limpar</button>
          }
        </div>
      </section>
    }
  `,
  styles: `
    :host { display: block; padding: 12px; overflow: auto; font-size: 13px; }
    .muted { color: var(--muted); margin-top: 0; }
    .flow { border: 1px solid var(--line); border-radius: 8px; padding: 8px 10px; margin-bottom: 10px; }
    header { display: flex; gap: 8px; align-items: center; margin-bottom: 4px; }
    ol { margin: 4px 0; padding-left: 20px; }
    li { display: flex; justify-content: space-between; gap: 6px; padding: 2px 0; cursor: pointer; }
    li:hover .label { text-decoration: underline; }
    li.missing .label { color: var(--danger); }
    li.empty { color: var(--muted); cursor: default; list-style: none; margin-left: -20px; }
    .actions { white-space: nowrap; }
    .buttons { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 6px; }
    .tag.money { background: var(--warn-bg); color: var(--warn); border-radius: 999px; padding: 0 8px; font-size: 11px; }
  `,
})
export class FlowsComponent {
  readonly useCases = input<UseCaseSpec[]>([]);
  readonly flows = input<FlowDefinition[]>([]);
  readonly elements = input<readonly SceneElement[]>([]);
  readonly selectedConnection = input<string | null>(null);
  readonly flowsChange = output<FlowDefinition[]>();
  readonly highlight = output<string[]>();

  protected readonly labels = computed(() => connectionLabels(this.elements()));

  protected stepsOf(useCaseId: string): string[] {
    return this.flows().find((f) => f.useCaseId === useCaseId)?.steps ?? [];
  }

  protected addSelected(useCaseId: string): void {
    const id = this.selectedConnection();
    if (!id || this.stepsOf(useCaseId).includes(id)) return;
    this.write(useCaseId, [...this.stepsOf(useCaseId), id]);
  }

  protected suggest(uc: UseCaseSpec): void {
    this.write(uc.id, suggestFlow(this.elements(), uc.entryClient));
  }

  protected move(useCaseId: string, index: number, delta: number): void {
    const steps = [...this.stepsOf(useCaseId)];
    const [step] = steps.splice(index, 1);
    steps.splice(index + delta, 0, step);
    this.write(useCaseId, steps);
  }

  protected remove(useCaseId: string, index: number): void {
    this.write(useCaseId, this.stepsOf(useCaseId).filter((_, i) => i !== index));
  }

  protected clear(useCaseId: string): void {
    this.write(useCaseId, []);
  }

  private write(useCaseId: string, steps: string[]): void {
    const others = this.flows().filter((f) => f.useCaseId !== useCaseId);
    const order = this.useCases().map((u) => u.id);
    this.flowsChange.emit([...others, { useCaseId, steps }].sort((a, b) => order.indexOf(a.useCaseId) - order.indexOf(b.useCaseId)));
  }
}
