import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Palette, SceneElement } from '../core/models';
import { ComponentData, ConnectionData, Selection, connectionIdOf, describeConnection, live, nodeIdOf } from './canvas-adapter';
import { Field, connectionFields, dataOps, fieldsOf, getPath, interactionOptions, optionLabels, parseFieldValue, setPath } from './schema-form';

export interface CustomDataChange {
  element: SceneElement;
  customData: Record<string, unknown>;
}

/** Inspector: formulário do componente gerado do configSchema do catálogo, ou da configuração da conexão. */
@Component({
  selector: 'lab-inspector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let sel = selection();
    @if (!sel) {
      <div class="hint">
        <p><strong>Nada selecionado.</strong></p>
        <p>Arraste componentes da paleta, ligue-os com a ferramenta de seta e selecione um componente ou uma seta para configurar.</p>
      </div>
    } @else if (sel.kind === 'component') {
      <header>
        <h3>{{ sel.label }}</h3>
        <small>{{ componentName() }} · <code>{{ nodeId() }}</code></small>
      </header>
      @if (fields().length === 0) {
        <p class="muted">Este componente não tem configuração.</p>
      }
      @for (group of groups(); track group.name) {
        <fieldset>
          @if (group.name) {
            <legend>{{ group.name }}</legend>
          }
          @for (field of group.fields; track field.path) {
            <ng-container *ngTemplateOutlet="fieldTpl; context: { field, value: value(field), connection: false }" />
          }
        </fieldset>
      }
    } @else {
      <header>
        <h3>Conexão</h3>
        <small>{{ connectionTitle() }} · <code>{{ connectionId() }}</code></small>
      </header>
      <label class="field">
        <span>Tipo de interação</span>
        <select [value]="connection()?.interaction ?? ''" (change)="setInteraction($any($event.target).value)">
          <option value="">automático (pelo tipo dos componentes)</option>
          @for (option of interactions; track option) {
            <option [value]="option">{{ labels[option] }}</option>
          }
        </select>
      </label>
      @for (group of connectionGroups; track group.name) {
        <fieldset>
          <legend>{{ group.name }}</legend>
          @for (field of group.fields; track field.path) {
            <ng-container *ngTemplateOutlet="fieldTpl; context: { field, value: value(field), connection: true }" />
          }
        </fieldset>
      }
      <fieldset>
        <legend>Operações de dados</legend>
        @for (op of ops(); track $index) {
          <div class="op">
            <select [value]="op.op" (change)="setOp($index, $any($event.target).value, op.count)">
              @for (kind of dataOps; track kind) {
                <option [value]="kind">{{ labels[kind] }}</option>
              }
            </select>
            <input type="number" min="1" [value]="op.count" (change)="setOp($index, op.op, +$any($event.target).value)" />
            <button type="button" class="link" (click)="removeOp($index)">remover</button>
          </div>
        }
        <button type="button" class="small" (click)="addOp()">+ operação</button>
      </fieldset>
    }

    <ng-template #fieldTpl let-field="field" let-value="value" let-connection="connection">
      @switch (field.kind) {
        @case ('boolean') {
          <label class="check">
            <input type="checkbox" [checked]="value ?? field.defaultValue ?? false" (change)="set(field, $any($event.target).checked, connection)" />
            {{ field.label }}
          </label>
        }
        @case ('select') {
          <label class="field">
            <span>{{ field.label }}</span>
            <select [value]="value ?? ''" (change)="set(field, $any($event.target).value, connection)">
              <option value="">{{ field.defaultValue != null ? 'padrão (' + (labels[field.defaultValue] ?? field.defaultValue) + ')' : '—' }}</option>
              @for (option of optionsFor(field); track option) {
                <option [value]="option">{{ labels[option] ?? option }}</option>
              }
            </select>
          </label>
        }
        @case ('multiselect') {
          <div class="field">
            <span>{{ field.label }}</span>
            <div class="chips">
              @for (option of field.options; track option) {
                <label class="check inline">
                  <input type="checkbox" [checked]="$any(value ?? []).includes(option)" (change)="toggle(field, option, $any($event.target).checked, value)" />
                  {{ option }}
                </label>
              }
            </div>
          </div>
        }
        @case ('list') {
          <label class="field">
            <span>{{ field.label }} <small>(separe por vírgula)</small></span>
            <input type="text" [value]="$any(value ?? []).join(', ')" (change)="set(field, $any($event.target).value, connection)" />
          </label>
        }
        @default {
          <label class="field">
            <span>{{ field.label }}</span>
            <input
              [type]="field.kind === 'text' ? 'text' : 'number'"
              [attr.min]="field.min"
              [attr.max]="field.max"
              [attr.step]="field.kind === 'integer' ? 1 : 'any'"
              [placeholder]="field.defaultValue ?? ''"
              [value]="value ?? ''"
              (change)="set(field, $any($event.target).value, connection)"
            />
          </label>
        }
      }
    </ng-template>
  `,
  styles: `
    :host { display: block; padding: 12px; overflow: auto; }
    header { margin-bottom: 8px; }
    h3 { margin: 0 0 2px; font-size: 15px; }
    small { color: var(--muted); }
    fieldset { border: 1px solid var(--line); border-radius: 8px; margin: 10px 0; padding: 8px 10px; }
    legend { font-size: 12px; color: var(--muted); padding: 0 4px; }
    .field { display: flex; flex-direction: column; gap: 3px; margin: 6px 0; font-size: 13px; }
    .field input, .field select, .op select, .op input { font: inherit; padding: 4px 6px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); color: var(--text); }
    .check { display: flex; gap: 6px; align-items: center; margin: 6px 0; font-size: 13px; }
    .check.inline { margin: 0 8px 0 0; }
    .chips { display: flex; flex-wrap: wrap; }
    .op { display: grid; grid-template-columns: 1fr 64px auto; gap: 6px; margin: 4px 0; }
    .hint { color: var(--muted); font-size: 13px; }
    .muted { color: var(--muted); }
  `,
  imports: [NgTemplateOutlet],
})
export class InspectorComponent {
  readonly selection = input<Selection>(null);
  readonly palette = input<Palette | null>(null);
  readonly elements = input<readonly SceneElement[]>([]);
  readonly customDataChange = output<CustomDataChange>();

  protected readonly labels: Record<string, string> = optionLabels as Record<string, string>;
  protected readonly interactions = interactionOptions;
  protected readonly dataOps = dataOps;
  protected readonly connectionGroups = groupFields(connectionFields);

  protected readonly componentDefinition = computed(() => {
    const sel = this.selection();
    if (sel?.kind !== 'component') return undefined;
    return this.palette()?.components.find((c) => c.id === sel.data.catalogId);
  });

  protected readonly componentName = computed(() => this.componentDefinition()?.displayName ?? (this.selection() as { data?: ComponentData })?.data?.catalogId ?? '');
  protected readonly fields = computed(() => fieldsOf(this.componentDefinition()?.configSchema));
  protected readonly groups = computed(() => groupFields(this.fields()));
  protected readonly nodeId = computed(() => {
    const sel = this.selection();
    return sel?.kind === 'component' ? nodeIdOf(sel.anchor) : '';
  });

  protected readonly connection = computed<ConnectionData | null>(() => {
    const sel = this.selection();
    return sel?.kind === 'connection' ? sel.data : null;
  });

  protected readonly connectionId = computed(() => {
    const sel = this.selection();
    return sel?.kind === 'connection' ? connectionIdOf(sel.arrow) : '';
  });

  protected readonly connectionTitle = computed(() => {
    const sel = this.selection();
    return sel?.kind === 'connection' ? describeConnection(this.elements(), sel.arrow) : '';
  });

  protected readonly ops = computed(() => (getPath(this.connection()?.config, 'ops') as { op: string; count: number }[] | undefined) ?? []);

  /** Conexões que saem do mesmo componente: candidatas ao "depende da conexão" do fallback e do cache miss. */
  private readonly siblingConnections = computed(() => {
    const sel = this.selection();
    if (sel?.kind !== 'connection' || !sel.arrow.startBinding) return [];
    const source = sel.arrow.startBinding.elementId;
    return live(this.elements())
      .filter((e) => e.type === 'arrow' && e.id !== sel.arrow.id && e.startBinding?.elementId === source)
      .map((e) => connectionIdOf(e));
  });

  protected value(field: Field): unknown {
    const sel = this.selection();
    if (!sel) return undefined;
    const config = sel.kind === 'component' ? sel.data.config : sel.data.config;
    return getPath(config, field.path);
  }

  protected optionsFor(field: Field): (string | number)[] {
    return field.path === 'triggerOf' ? this.siblingConnections() : (field.options ?? []);
  }

  protected set(field: Field, raw: string | boolean, connection: boolean): void {
    this.update(field.path, parseFieldValue(field, raw), connection);
  }

  protected toggle(field: Field, option: string | number, checked: boolean, current: unknown): void {
    const list = new Set(Array.isArray(current) ? current : []);
    if (checked) list.add(option);
    else list.delete(option);
    this.update(field.path, parseFieldValue(field, [...list].map(String)), false);
  }

  protected setInteraction(value: string): void {
    const sel = this.selection();
    if (sel?.kind !== 'connection') return;
    const data: Record<string, unknown> = { ...sel.data, kind: 'connection', connectionId: connectionIdOf(sel.arrow) };
    if (value) data['interaction'] = value;
    else delete data['interaction'];
    this.customDataChange.emit({ element: sel.arrow, customData: data });
  }

  protected addOp(): void {
    this.update('ops', [...this.ops(), { op: 'read', count: 1 }], true);
  }

  protected setOp(index: number, op: string, count: number): void {
    this.update('ops', this.ops().map((o, i) => (i === index ? { op, count: Math.max(1, count || 1) } : o)), true);
  }

  protected removeOp(index: number): void {
    const next = this.ops().filter((_, i) => i !== index);
    this.update('ops', next.length > 0 ? next : undefined, true);
  }

  private update(path: string, value: unknown, connection: boolean): void {
    const sel = this.selection();
    if (!sel) return;
    if (sel.kind === 'component' && !connection) {
      const config = setPath(sel.data.config, path, value);
      const data: Record<string, unknown> = { ...sel.data };
      if (Object.keys(config).length > 0) data['config'] = config;
      else delete data['config'];
      this.customDataChange.emit({ element: sel.anchor, customData: data });
    } else if (sel.kind === 'connection') {
      const config = setPath(sel.data.config, path, value);
      const data: Record<string, unknown> = { ...sel.data, kind: 'connection', connectionId: connectionIdOf(sel.arrow) };
      if (Object.keys(config).length > 0) data['config'] = config;
      else delete data['config'];
      this.customDataChange.emit({ element: sel.arrow, customData: data });
    }
  }
}

function groupFields(fields: Field[]): { name: string; fields: Field[] }[] {
  const groups = new Map<string, Field[]>();
  for (const field of fields) {
    const name = field.group ?? '';
    groups.set(name, [...(groups.get(name) ?? []), field]);
  }
  return [...groups.entries()].map(([name, list]) => ({ name, fields: list }));
}
