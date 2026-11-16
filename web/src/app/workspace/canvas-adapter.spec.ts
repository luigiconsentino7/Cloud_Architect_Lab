import { SceneElement } from '../core/models';
import {
  arrowGeometry,
  arrowsMissingConnectionData,
  componentSkeleton,
  connectionIdOf,
  elementsForIrIds,
  freePosition,
  labelWidth,
  needsRestore,
  selectionOf,
  toSkeletons,
  typedShape,
} from './canvas-adapter';
import { suggestFlow } from './flows.component';

/** Cena no formato que o motor gera (SceneWriter): âncora + texto vinculado, setas com binding. */
function box(id: string, catalogId: string, label: string, x: number, y = 0, externalId?: string): SceneElement[] {
  return [
    {
      id: `el_${id}`,
      type: 'rectangle',
      x,
      y,
      width: 160,
      height: 80,
      groupIds: [`g_${id}`],
      customData: { kind: 'component', componentId: id, catalogId, role: 'anchor', catalogVersion: 't', ...(externalId ? { externalId } : {}) },
    },
    { id: `txt_${id}`, type: 'text', x: x + 10, y: y + 30, width: 140, height: 20, containerId: `el_${id}`, text: label, groupIds: [`g_${id}`] },
  ];
}

function arrow(id: string, from: string, to: string, withData = true): SceneElement {
  return {
    id: `arrow_${id}`,
    type: 'arrow',
    x: 0,
    y: 0,
    width: 100,
    height: 0,
    startBinding: { elementId: `el_${from}` },
    endBinding: { elementId: `el_${to}` },
    ...(withData ? { customData: { kind: 'connection', connectionId: id } } : {}),
  };
}

const scene: SceneElement[] = [
  ...box('app', 'client.mobile', 'App', 0),
  ...box('svc', 'aws.ecs-fargate', 'pix-service', 300),
  ...box('db', 'aws.dynamodb', 'saldos', 600),
  ...box('spi_box', 'external.system', 'SPI', 600, 200, 'external:spi'),
  arrow('e1', 'app', 'svc'),
  arrow('e2', 'svc', 'db'),
  arrow('e3', 'svc', 'spi_box', false),
];

describe('canvas-adapter', () => {
  it('cria o esqueleto de componente com o customData que o compilador lê', () => {
    const skeleton = componentSkeleton({ id: 'aws.sqs', category: 'mensageria', displayName: 'Amazon SQS', shortName: 'SQS' }, { x: 10, y: 20 }, 'sqs_1', '2026.11.0');

    expect(skeleton['customData']).toEqual({ kind: 'component', componentId: 'sqs_1', catalogId: 'aws.sqs', role: 'anchor', catalogVersion: '2026.11.0' });
    expect((skeleton['label'] as { text: string }).text).toBe('SQS');
    expect(skeleton['id']).toBe('el_sqs_1');
  });

  it('usa o mesmo id de conexão que o compilador (customData ou conn_<id>)', () => {
    expect(connectionIdOf(scene.find((e) => e.id === 'arrow_e1')!)).toBe('e1');
    expect(connectionIdOf(scene.find((e) => e.id === 'arrow_e3')!)).toBe('conn_arrow_e3');
  });

  it('marca setas novas com customData de conexão estável', () => {
    const missing = arrowsMissingConnectionData(scene);

    expect(missing.map((e) => e.id)).toEqual(['arrow_e3']);
    expect(missing[0].customData).toEqual({ kind: 'connection', connectionId: 'conn_arrow_e3' });
  });

  it('seleciona o componente pelo texto vinculado e a conexão pela seta', () => {
    const component = selectionOf(scene, ['txt_svc']);
    const connection = selectionOf(scene, ['arrow_e2']);

    expect(component?.kind).toBe('component');
    expect(component?.kind === 'component' && component.label).toBe('pix-service');
    expect(connection?.kind === 'connection' && connection.from?.id).toBe('el_svc');
    expect(connection?.kind === 'connection' && connection.to?.id).toBe('el_db');
    expect(selectionOf(scene, ['el_app', 'el_db'])).toBeNull();
  });

  it('acha os elementos do canvas a partir dos ids da IR (nós, externos e conexões)', () => {
    expect(elementsForIrIds(scene, ['svc', 'external:spi', 'e2']).sort()).toEqual(['arrow_e2', 'el_spi_box', 'el_svc']);
  });

  it('põe o componente novo perto do centro sem sobrepor outro', () => {
    const position = freePosition(scene, { x: 80, y: 40 });
    const overlaps = scene.filter((e) => e.type === 'rectangle').some((e) => Math.abs(e.x - position.x) < 210 && Math.abs(e.y - position.y) < 100);

    expect(overlaps).toBe(false);
  });

  it('liga as bordas das caixas na horizontal e na vertical', () => {
    const right = arrowGeometry({ x: 0, y: 0, width: 160, height: 80 }, { x: 300, y: 0, width: 160, height: 80 });
    const down = arrowGeometry({ x: 0, y: 0, width: 160, height: 80 }, { x: 0, y: 300, width: 160, height: 80 });

    expect(right.x).toBe(166);
    expect(right.points[1]).toEqual([300 - 6 - 166, 0]);
    expect(down.y).toBe(86);
    expect(down.points[1][0]).toBe(0);
  });

  it('converte a cena mínima do motor em esqueletos completos do Excalidraw', () => {
    expect(needsRestore(scene)).toBe(true);
    const skeletons = toSkeletons(scene);
    const app = skeletons.find((s) => s['id'] === 'el_app')!;
    const link = skeletons.find((s) => s['id'] === 'arrow_e1')!;

    expect(skeletons.some((s) => s['type'] === 'text')).toBe(false); // o rótulo vira label do retângulo
    expect((app['label'] as { text: string }).text).toBe('App');
    expect(link['start']).toEqual({ id: 'el_app' });
    expect(link['points']).toBeDefined();
    expect(link['x']).toBe(166);
  });

  it('alarga a caixa quando o rótulo não cabe e liga a seta na borda nova', () => {
    const wide = [...box('dedupe', 'aws.dynamodb', 'dedupe-notificacoes', 0), ...box('push', 'external.system', 'Push', 400), arrow('e9', 'dedupe', 'push')];
    const skeletons = toSkeletons(wide);
    const dedupe = skeletons.find((s) => s['id'] === 'el_dedupe')!;
    const link = skeletons.find((s) => s['id'] === 'arrow_e9')!;

    expect(dedupe['width']).toBe(labelWidth('dedupe-notificacoes'));
    expect(dedupe['width']).toBeGreaterThan(160);
    expect(link['x']).toBe((dedupe['width'] as number) + 6);
    expect(skeletons.find((s) => s['id'] === 'el_push')!['width']).toBe(160);
  });

  it('tipa uma forma desenhada à mão sem mudar o lugar, o rótulo nem as setas', () => {
    const shape: SceneElement = { id: 'shape_1', type: 'rectangle', x: 40, y: 60, width: 120, height: 70, version: 3 };
    const label: SceneElement = { id: 'shape_1_txt', type: 'text', x: 50, y: 80, width: 80, height: 20, containerId: 'shape_1', text: 'Redis' };

    const typed = typedShape(shape, { id: 'aws.elasticache', category: 'dados' }, 'elasticache_1', '2026.11.0');
    const selection = selectionOf([typed, label], ['shape_1']);

    expect(typed.customData).toEqual({ kind: 'component', componentId: 'elasticache_1', catalogId: 'aws.elasticache', role: 'anchor', catalogVersion: '2026.11.0' });
    expect([typed.x, typed.y, typed.width]).toEqual([40, 60, 120]);
    expect(typed['version']).toBe(4);
    expect(selection?.kind === 'component' && selection.label).toBe('Redis');
  });

  it('sugere o fluxo percorrendo as setas a partir do cliente de entrada', () => {
    expect(suggestFlow(scene, 'client.mobile')).toEqual(['e1', 'e2', 'conn_arrow_e3']);
    expect(suggestFlow(scene, 'client.partner')).toEqual([]);
  });
});
