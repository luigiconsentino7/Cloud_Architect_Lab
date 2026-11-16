import { ExternalSystemSpec, PaletteComponent, SceneElement } from '../core/models';

/**
 * Camada semântica sobre a cena do Excalidraw (spec 04): componentes da paleta são retângulos com
 * `customData.kind = "component"` e um texto vinculado; setas ligadas a eles viram conexões. Tudo aqui é função pura
 * sobre elementos — o componente do canvas só aplica o resultado na API do Excalidraw.
 */

export interface ComponentData {
  kind: 'component';
  componentId: string;
  catalogId: string;
  role: 'anchor';
  catalogVersion: string;
  config?: Record<string, unknown>;
  externalId?: string;
}

export interface ConnectionData {
  kind: 'connection';
  connectionId: string;
  interaction?: string;
  config?: Record<string, unknown>;
}

export type Selection =
  | { kind: 'component'; anchor: SceneElement; data: ComponentData; label: string }
  | { kind: 'connection'; arrow: SceneElement; data: ConnectionData; from?: SceneElement; to?: SceneElement }
  | null;

const categoryColors: Record<string, string> = {
  borda: '#e7f5ff',
  computacao: '#fff4e6',
  dados: '#ebfbee',
  mensageria: '#f3f0ff',
  seguranca: '#fff5f5',
  observabilidade: '#f8f9fa',
  clientes: '#e6fcf5',
  externos: '#fff9db',
};

export function colorFor(category: string): string {
  return categoryColors[normalize(category)] ?? '#f1f3f5';
}

/** Esqueleto para o `convertToExcalidrawElements`: retângulo com rótulo vinculado e o customData da paleta. */
export function componentSkeleton(
  component: Pick<PaletteComponent, 'id' | 'category' | 'displayName' | 'shortName'>,
  position: { x: number; y: number },
  componentId: string,
  catalogVersion: string,
  options: { label?: string; externalId?: string; config?: Record<string, unknown> } = {},
): Record<string, unknown> {
  const data: ComponentData = {
    kind: 'component',
    componentId,
    catalogId: component.id,
    role: 'anchor',
    catalogVersion,
    ...(options.externalId ? { externalId: options.externalId } : {}),
    ...(options.config ? { config: options.config } : {}),
  };
  const label = options.label ?? component.shortName ?? component.displayName;
  return {
    type: 'rectangle',
    id: `el_${componentId}`,
    x: position.x,
    y: position.y,
    width: Math.max(190, Math.round(label.length * 10.5) + 40),
    height: 76,
    strokeColor: '#1e1e1e',
    backgroundColor: colorFor(component.category),
    fillStyle: 'solid',
    roundness: { type: 3 },
    label: { text: label, fontSize: 16 },
    customData: data,
  };
}

export function externalSkeleton(system: ExternalSystemSpec, position: { x: number; y: number }, catalogVersion: string): Record<string, unknown> {
  const componentId = `ext_${slug(system.id)}`;
  return componentSkeleton(
    { id: 'external.system', category: 'externos', displayName: system.name, shortName: system.name },
    position,
    componentId,
    catalogVersion,
    { label: system.name, externalId: system.id },
  );
}

export function live(elements: readonly SceneElement[]): SceneElement[] {
  return elements.filter((e) => !e.isDeleted);
}

export function componentData(element: SceneElement | undefined): ComponentData | null {
  const data = element?.customData;
  return data && data['kind'] === 'component' ? (data as unknown as ComponentData) : null;
}

export function connectionData(element: SceneElement | undefined): ConnectionData | null {
  const data = element?.customData;
  return element?.type === 'arrow' && data && data['kind'] === 'connection' ? (data as unknown as ConnectionData) : null;
}

/** O nó da IR de um componente: o id externo para sistemas do desafio, senão o componentId. */
export function nodeIdOf(element: SceneElement): string | null {
  const data = componentData(element);
  return data ? (data.externalId ?? data.componentId) : null;
}

/** Mesma regra do compilador: `customData.connectionId` ou `conn_<id do elemento>`. */
export function connectionIdOf(arrow: SceneElement): string {
  return connectionData(arrow)?.connectionId ?? `conn_${arrow.id}`;
}

/** O componente dono de um elemento: o próprio retângulo, o texto vinculado ou algo do mesmo grupo. */
export function anchorOf(elements: readonly SceneElement[], elementId: string): SceneElement | undefined {
  const byId = new Map(elements.map((e) => [e.id, e]));
  const element = byId.get(elementId);
  if (!element) return undefined;
  if (componentData(element)) return element;
  if (element.containerId) {
    const container = byId.get(element.containerId);
    if (componentData(container)) return container;
  }
  const group = element.groupIds?.[0];
  return group ? elements.find((e) => componentData(e) && e.groupIds?.[0] === group) : undefined;
}

export function labelOf(elements: readonly SceneElement[], anchor: SceneElement): string {
  const text = elements.find((e) => e.type === 'text' && e.containerId === anchor.id && !e.isDeleted);
  return (text?.text as string | undefined)?.trim() || componentData(anchor)?.componentId || anchor.id;
}

export function selectionOf(elements: readonly SceneElement[], selectedIds: readonly string[]): Selection {
  if (selectedIds.length === 0) return null;
  const items = live(elements);
  const byId = new Map(items.map((e) => [e.id, e]));
  const arrows = selectedIds.map((id) => byId.get(id)).filter((e) => e?.type === 'arrow') as SceneElement[];
  if (arrows.length === 1 && selectedIds.length <= 2) {
    const arrow = arrows[0];
    const data = connectionData(arrow) ?? { kind: 'connection', connectionId: connectionIdOf(arrow) };
    return {
      kind: 'connection',
      arrow,
      data,
      from: arrow.startBinding ? anchorOf(items, arrow.startBinding.elementId) : undefined,
      to: arrow.endBinding ? anchorOf(items, arrow.endBinding.elementId) : undefined,
    };
  }

  const anchors = [...new Set(selectedIds.map((id) => anchorOf(items, id)).filter((a): a is SceneElement => !!a))];
  if (anchors.length !== 1) return null;
  return { kind: 'component', anchor: anchors[0], data: componentData(anchors[0])!, label: labelOf(items, anchors[0]) };
}

/** Setas desenhadas pelo usuário ganham customData de conexão com id estável (o mesmo que o compilador usaria). */
export function arrowsMissingConnectionData(elements: readonly SceneElement[]): SceneElement[] {
  return live(elements)
    .filter((e) => e.type === 'arrow' && !connectionData(e))
    .map((e) => ({ ...e, customData: { ...(e.customData ?? {}), kind: 'connection', connectionId: `conn_${e.id}` } }));
}

/** "Origem → Destino" de uma seta, pelos rótulos dos componentes ligados. */
export function describeConnection(elements: readonly SceneElement[], arrow: SceneElement): string {
  const items = live(elements);
  const from = arrow.startBinding ? anchorOf(items, arrow.startBinding.elementId) : undefined;
  const to = arrow.endBinding ? anchorOf(items, arrow.endBinding.elementId) : undefined;
  return `${from ? labelOf(items, from) : '(solta)'} → ${to ? labelOf(items, to) : '(solta)'}`;
}

export function connectionLabels(elements: readonly SceneElement[]): Map<string, string> {
  const result = new Map<string, string>();
  for (const arrow of live(elements).filter((e) => e.type === 'arrow')) result.set(connectionIdOf(arrow), describeConnection(elements, arrow));
  return result;
}

/** Ids da IR (nós e conexões) → elementos do canvas, para destacar um achado ou diagnóstico. */
export function elementsForIrIds(elements: readonly SceneElement[], irIds: readonly string[]): string[] {
  const wanted = new Set(irIds);
  return live(elements)
    .filter((e) => (componentData(e) && wanted.has(nodeIdOf(e)!)) || (e.type === 'arrow' && wanted.has(connectionIdOf(e))))
    .map((e) => e.id);
}

/** Cópia do elemento com customData novo e versão incrementada (o Excalidraw só aplica mudanças com versão nova). */
export function withCustomData(element: SceneElement, customData: Record<string, unknown>): SceneElement {
  const version = typeof element['version'] === 'number' ? (element['version'] as number) : 1;
  return { ...element, customData, version: version + 1, versionNonce: Math.floor(Math.random() * 2 ** 31), updated: Date.now() };
}

/** Assinatura barata da cena: muda quando algum elemento muda (o onChange do Excalidraw dispara a cada movimento do mouse). */
export function sceneSignature(elements: readonly SceneElement[]): string {
  let sum = 0;
  for (const e of elements) sum += ((e['version'] as number | undefined) ?? 0) + (e.isDeleted ? 7 : 0);
  return `${elements.length}:${sum}`;
}

export function nextComponentId(elements: readonly SceneElement[], catalogId: string): string {
  const base = slug(catalogId.split('.').pop() ?? 'cmp');
  const used = new Set(live(elements).map((e) => componentData(e)?.componentId).filter(Boolean));
  let n = 1;
  while (used.has(`${base}_${n}`)) n++;
  return `${base}_${n}`;
}

/**
 * Posição livre para o próximo componente: perto do centro da área visível, em espiral, sem sobrepor outro
 * componente (190 × 76 com folga).
 */
export function freePosition(elements: readonly SceneElement[], center: { x: number; y: number }): { x: number; y: number } {
  const anchors = live(elements).filter((e) => componentData(e));
  const origin = { x: Math.round(center.x - 95), y: Math.round(center.y - 38) };
  const overlaps = (x: number, y: number) => anchors.some((a) => Math.abs(a.x - x) < 210 && Math.abs(a.y - y) < 100);
  for (let ring = 0; ring < 8; ring++) {
    for (let dy = -ring; dy <= ring; dy++) {
      for (let dx = -ring; dx <= ring; dx++) {
        if (Math.max(Math.abs(dx), Math.abs(dy)) !== ring) continue;
        const x = origin.x + dx * 230;
        const y = origin.y + dy * 120;
        if (!overlaps(x, y)) return { x, y };
      }
    }
  }
  return origin;
}

export function normalize(text: string): string {
  return text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
}

function slug(text: string): string {
  return normalize(text).replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '') || 'cmp';
}

/** Elementos gravados pelo próprio Excalidraw têm `seed` e `version`; os gerados pelo motor (referência, testes) não. */
export function needsRestore(elements: readonly SceneElement[]): boolean {
  return elements.some((e) => typeof e['seed'] !== 'number' || typeof e['version'] !== 'number');
}

/**
 * Converte uma cena mínima (como a referência gerada pelo motor) em esqueletos para o `convertToExcalidrawElements`:
 * retângulos com rótulo, setas ligadas aos componentes pelas pontas (a geometria é calculada pelo Excalidraw),
 * frames com seus filhos e textos soltos.
 */
export function toSkeletons(elements: readonly SceneElement[]): Record<string, unknown>[] {
  const items = live(elements);
  const byId = new Map(items.map((e) => [e.id, e]));
  const labels = new Map(items.filter((e) => e.type === 'text' && e.containerId).map((e) => [e.containerId!, (e.text as string | undefined) ?? '']));
  const skeletons: Record<string, unknown>[] = [];
  // Setas paralelas entre o mesmo par de componentes ganham um pequeno deslocamento para não se sobreporem.
  const pairs = new Map<string, string[]>();
  for (const arrow of items.filter((e) => e.type === 'arrow' && e.startBinding && e.endBinding)) {
    const key = [arrow.startBinding!.elementId, arrow.endBinding!.elementId].sort().join('|');
    pairs.set(key, [...(pairs.get(key) ?? []), arrow.id]);
  }
  const pairOffset = (arrow: SceneElement, from: SceneElement, to: SceneElement) => {
    const siblings = pairs.get([from.id, to.id].sort().join('|')) ?? [arrow.id];
    return (siblings.indexOf(arrow.id) - (siblings.length - 1) / 2) * 16;
  };
  // Rótulo longo alarga a caixa (o motor desenha todas com 160 px); as setas usam a largura nova.
  const box = (e: SceneElement): SceneElement => (labels.has(e.id) ? { ...e, width: Math.max(e.width, labelWidth(labels.get(e.id)!)) } : e);
  for (const e of items) {
    if (e.type === 'text' && e.containerId) continue;
    const common = { id: e.id, x: e.x, y: e.y, customData: e.customData, groupIds: e.groupIds ?? [] };
    if (e.type === 'arrow') {
      const from = e.startBinding ? byId.get(e.startBinding.elementId) : undefined;
      const to = e.endBinding ? byId.get(e.endBinding.elementId) : undefined;
      const geometry = from && to ? arrowGeometry(box(from), box(to), pairOffset(e, from, to)) : {};
      skeletons.push({
        ...common,
        ...geometry,
        type: 'arrow',
        strokeStyle: e['strokeStyle'] ?? 'solid',
        start: e.startBinding ? { id: e.startBinding.elementId } : undefined,
        end: e.endBinding ? { id: e.endBinding.elementId } : undefined,
      });
    } else if (e.type === 'frame') {
      skeletons.push({ ...common, type: 'frame', name: e['name'], width: e.width, height: e.height, children: items.filter((c) => c['frameId'] === e.id).map((c) => c.id) });
    } else if (e.type === 'text') {
      skeletons.push({ ...common, type: 'text', text: e.text ?? '' });
    } else {
      const component = componentData(e);
      skeletons.push({
        ...common,
        type: e.type,
        width: box(e).width,
        height: e.height,
        backgroundColor: component ? (component.externalId ? colorFor('externos') : '#f1f3f5') : 'transparent',
        fillStyle: 'solid',
        roundness: { type: 3 },
        label: labels.has(e.id) ? { text: labels.get(e.id), fontSize: 16 } : undefined,
      });
    }
  }
  return skeletons;
}

/** Largura que cabe o rótulo numa linha, na fonte manuscrita do Excalidraw a 16 px (estimativa folgada). */
export function labelWidth(label: string): number {
  return Math.round(label.length * 9.5) + 32;
}

/**
 * Pontos de uma seta entre duas caixas: sai da borda da origem voltada para o destino e chega na borda oposta
 * (horizontal quando estão lado a lado, vertical quando estão empilhadas). `offset` afasta setas paralelas.
 */
export function arrowGeometry(
  from: Pick<SceneElement, 'x' | 'y' | 'width' | 'height'>,
  to: Pick<SceneElement, 'x' | 'y' | 'width' | 'height'>,
  offset = 0,
): { x: number; y: number; width: number; height: number; points: [number, number][] } {
  const gap = 6;
  const fromCenter = { x: from.x + from.width / 2, y: from.y + from.height / 2 };
  const toCenter = { x: to.x + to.width / 2, y: to.y + to.height / 2 };
  const horizontal = Math.abs(toCenter.x - fromCenter.x) >= Math.abs(toCenter.y - fromCenter.y);
  let start: { x: number; y: number };
  let end: { x: number; y: number };
  if (horizontal) {
    const right = toCenter.x >= fromCenter.x;
    start = { x: right ? from.x + from.width + gap : from.x - gap, y: fromCenter.y + offset };
    end = { x: right ? to.x - gap : to.x + to.width + gap, y: toCenter.y + offset };
  } else {
    const down = toCenter.y >= fromCenter.y;
    start = { x: fromCenter.x + offset, y: down ? from.y + from.height + gap : from.y - gap };
    end = { x: toCenter.x + offset, y: down ? to.y - gap : to.y + to.height + gap };
  }
  const dx = end.x - start.x;
  const dy = end.y - start.y;
  return { x: start.x, y: start.y, width: Math.abs(dx), height: Math.abs(dy), points: [[0, 0], [dx, dy]] };
}

