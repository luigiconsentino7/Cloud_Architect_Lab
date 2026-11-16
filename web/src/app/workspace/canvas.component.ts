import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, afterNextRender, input, output, signal, viewChild } from '@angular/core';
import type { ExcalidrawImperativeAPI } from '@excalidraw/excalidraw/types';
import type { Root } from 'react-dom/client';
import { ExcalidrawScene, SceneElement } from '../core/models';
import { arrowsMissingConnectionData, needsRestore, sceneSignature, toSkeletons } from './canvas-adapter';

type ExcalidrawModule = typeof import('@excalidraw/excalidraw');

/**
 * Ilha React com o Excalidraw (ADR-002). É o único lugar do app que conhece o Excalidraw: o resto conversa com este
 * componente por elementos simples. React, ReactDOM e o Excalidraw só são baixados quando o canvas aparece.
 */
@Component({
  selector: 'lab-canvas',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="canvas-host" #host></div>
    @if (loading()) {
      <div class="canvas-loading">Carregando o canvas…</div>
    }
  `,
  styles: `
    :host { display: block; position: relative; width: 100%; height: 100%; }
    .canvas-host { position: absolute; inset: 0; }
    .canvas-loading { position: absolute; inset: 0; display: grid; place-items: center; color: var(--muted); }
  `,
})
export class CanvasComponent implements OnDestroy {
  readonly scene = input<ExcalidrawScene | null>(null);
  readonly readOnly = input(false);

  /** Emitido quando algum elemento muda (não a cada movimento do mouse). */
  /** O usuário (ou o app, a pedido dele) mudou o desenho: o workspace salva. */
  readonly sceneChange = output<readonly SceneElement[]>();
  /** Cena carregada ou só remedida (abertura, fonte que terminou de carregar): atualiza os painéis, sem salvar. */
  readonly sceneLoad = output<readonly SceneElement[]>();
  readonly selectionChange = output<string[]>();

  protected readonly loading = signal(true);
  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private root?: Root;
  private api?: ExcalidrawImperativeAPI;
  private excalidraw?: ExcalidrawModule;
  private lastSignature = '';
  private lastSelection = '';
  private measuring = false;

  constructor() {
    afterNextRender(() => void this.mount());
  }

  ngOnDestroy(): void {
    this.root?.unmount();
  }

  elements(): readonly SceneElement[] {
    return (this.api?.getSceneElements() ?? []) as unknown as readonly SceneElement[];
  }

  /** Cria elementos a partir de esqueletos (retângulo + rótulo vinculado); por padrão, seleciona o que criou. */
  add(skeletons: Record<string, unknown>[], options: { select?: boolean; fit?: boolean } = {}): void {
    if (!this.api || !this.excalidraw) return;
    const created = this.excalidraw.convertToExcalidrawElements(skeletons as never, { regenerateIds: false });
    const select = options.select ?? true;
    this.api.updateScene({
      elements: [...this.api.getSceneElementsIncludingDeleted(), ...created],
      appState: select ? { selectedElementIds: Object.fromEntries(created.filter((e) => e.type !== 'text').map((e) => [e.id, true])) } : undefined,
      captureUpdate: this.excalidraw.CaptureUpdateAction.IMMEDIATELY,
    });
    if (options.fit) this.api.scrollToContent(undefined, { fitToContent: true });
  }

  /** Centro da área visível, em coordenadas da cena (onde os componentes novos aparecem). */
  viewportCenter(): { x: number; y: number } {
    const state = this.api?.getAppState();
    if (!state) return { x: 400, y: 300 };
    const zoom = state.zoom.value || 1;
    return { x: -state.scrollX + state.width / 2 / zoom, y: -state.scrollY + state.height / 2 / zoom };
  }

  /** Substitui elementos pelo id (ex.: customData novo vindo do inspector). */
  replace(changed: readonly SceneElement[]): void {
    if (!this.api || !this.excalidraw || changed.length === 0) return;
    const byId = new Map(changed.map((e) => [e.id, e]));
    const next = this.api.getSceneElementsIncludingDeleted().map((e) => (byId.get(e.id) as never) ?? e);
    this.api.updateScene({ elements: next, captureUpdate: this.excalidraw.CaptureUpdateAction.IMMEDIATELY });
  }

  select(elementIds: readonly string[]): void {
    if (!this.api) return;
    this.api.updateScene({ appState: { selectedElementIds: Object.fromEntries(elementIds.map((id) => [id, true])) } });
    const elements = this.api.getSceneElements().filter((e) => elementIds.includes(e.id));
    if (elements.length > 0) this.api.scrollToContent(elements, { fitToContent: elements.length > 1, animate: true });
  }

  /** Cena para salvar: elementos (inclusive apagados, para o Excalidraw reconciliar) e o mínimo de appState. */
  snapshot(): ExcalidrawScene {
    const appState = this.api?.getAppState();
    return {
      type: 'excalidraw',
      version: 2,
      elements: (this.api?.getSceneElements() ?? []) as unknown as readonly SceneElement[],
      appState: appState ? { viewBackgroundColor: appState.viewBackgroundColor, gridSize: appState.gridSize } : {},
    };
  }

  private async mount(): Promise<void> {
    // Assets do Excalidraw (fontes e CSS) servidos pelo próprio app: nada vem de CDN.
    (window as unknown as { EXCALIDRAW_ASSET_PATH: string }).EXCALIDRAW_ASSET_PATH = '/excalidraw/';
    ensureStylesheet('/excalidraw/index.css');
    const [reactModule, clientModule, excalidraw] = await Promise.all([import('react'), import('react-dom/client'), import('@excalidraw/excalidraw')]);
    // React e ReactDOM são CommonJS: no import dinâmico, as funções podem vir dentro de `default`.
    const react = interop(reactModule);
    const client = interop(clientModule);
    this.excalidraw = excalidraw;
    const initial = this.scene();
    const elements = initial?.elements ?? [];
    // Cena gerada pelo motor (sem seed/version): vira esqueletos e o Excalidraw completa geometria e vínculos.
    const initialElements = needsRestore(elements)
      ? excalidraw.convertToExcalidrawElements(toSkeletons(elements) as never, { regenerateIds: false })
      : elements;
    this.root = client.createRoot(this.host().nativeElement);
    this.root.render(
      react.createElement(excalidraw.Excalidraw, {
        initialData: {
          elements: initialElements as never,
          appState: { ...(initial?.appState ?? {}), viewModeEnabled: this.readOnly() } as never,
          scrollToContent: true,
        },
        excalidrawAPI: (api: ExcalidrawImperativeAPI) => {
          this.api = api;
          this.loading.set(false);
          // Enquadra o desenho todo ao abrir (o scrollToContent do initialData só centraliza em 100%).
          if (initialElements.length > 0) setTimeout(() => api.scrollToContent(undefined, { fitToContent: true }), 50);
          void this.refreshTextWhenFontLoads();
        },
        onChange: (elements, appState) => this.onChange(elements as unknown as readonly SceneElement[], Object.keys(appState.selectedElementIds ?? {})),
        langCode: 'pt-BR',
        viewModeEnabled: this.readOnly(),
        UIOptions: { canvasActions: { loadScene: false, saveToActiveFile: false, export: false, saveAsImage: true } },
      }),
    );
  }

  /**
   * Textos criados antes de a fonte manuscrita carregar foram medidos com a fonte reserva (mais estreita) e saem
   * cortados nas pontas. Quando a Excalifont carrega, mede tudo de novo (o Excalidraw só redesenha, não remede).
   */
  private async refreshTextWhenFontLoads(): Promise<void> {
    for (let i = 0; i < 50 && !excalifontLoaded(); i++) await new Promise((resolve) => setTimeout(resolve, 200));
    if (!this.api || !this.excalidraw) return;
    const elements = this.api.getSceneElementsIncludingDeleted();
    if (!elements.some((e) => e.type === 'text' && !e.isDeleted)) return;
    // Remedir texto não é edição do usuário: a mudança sai como carga, sem marcar o rascunho nem disparar o autosave.
    this.measuring = true;
    this.api.updateScene({
      elements: this.excalidraw.restoreElements(elements, null, { refreshDimensions: true, repairBindings: true }),
      captureUpdate: this.excalidraw.CaptureUpdateAction.NEVER,
    });
    setTimeout(() => (this.measuring = false), 300);
  }

  private onChange(elements: readonly SceneElement[], selected: string[]): void {
    const selection = selected.sort().join(',');
    if (selection !== this.lastSelection) {
      this.lastSelection = selection;
      this.selectionChange.emit(selected);
    }

    const signature = sceneSignature(elements);
    if (signature === this.lastSignature) return;
    const loading = this.lastSignature === '' || this.measuring;
    this.lastSignature = signature;
    if (!this.readOnly()) {
      // Setas novas ganham id de conexão estável antes de qualquer um usá-las nos fluxos.
      const missing = arrowsMissingConnectionData(elements);
      if (missing.length > 0) {
        queueMicrotask(() => this.replace(missing));
        return;
      }
    }

    if (loading) this.sceneLoad.emit(elements);
    else this.sceneChange.emit(elements);
  }
}

function excalifontLoaded(): boolean {
  let loaded = false;
  document.fonts.forEach((face) => {
    if (face.family.replace(/["']/g, '') === 'Excalifont' && face.status === 'loaded') loaded = true;
  });
  return loaded;
}

function interop<T>(module: T): T {
  const candidate = module as unknown as { default?: T; createElement?: unknown; createRoot?: unknown };
  return candidate.createElement || candidate.createRoot ? module : (candidate.default ?? module);
}

function ensureStylesheet(href: string): void {
  if (document.querySelector(`link[href="${href}"]`)) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = href;
  document.head.appendChild(link);
}
