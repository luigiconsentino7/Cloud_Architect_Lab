import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Palette, SceneElement } from '../core/models';
import { InspectorComponent } from './inspector.component';

const palette: Palette = {
  catalogVersion: 't',
  provider: 'aws',
  archetypes: [],
  components: [
    {
      id: 'aws.ecs-fargate',
      archetype: 'compute.service',
      displayName: 'Amazon ECS (Fargate)',
      category: 'computacao',
      aliases: [],
      scope: 'zonal',
      isDefault: true,
      shared: false,
      capabilities: {},
      configSchema: { type: 'object', properties: { vcpu: { type: 'number', enum: [0.25, 0.5, 1, 2], default: 1, title: 'vCPU' } } },
    },
  ],
};

const anchor: SceneElement = { id: 'el_svc', type: 'rectangle', x: 0, y: 0, width: 160, height: 80 };
const arrow: SceneElement = { id: 'arrow_e1', type: 'arrow', x: 0, y: 0, width: 100, height: 0 };

function selects(fixture: { nativeElement: HTMLElement }): HTMLSelectElement[] {
  return [...fixture.nativeElement.querySelectorAll('select')];
}

describe('InspectorComponent', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [InspectorComponent], providers: [provideZonelessChangeDetection()] }));

  it('mostra no seletor o valor salvo do componente, não o padrão', async () => {
    const fixture = TestBed.createComponent(InspectorComponent);
    fixture.componentRef.setInput('palette', palette);
    fixture.componentRef.setInput('selection', {
      kind: 'component',
      anchor,
      label: 'pix-service',
      data: { kind: 'component', componentId: 'svc', catalogId: 'aws.ecs-fargate', role: 'anchor', catalogVersion: 't', config: { vcpu: 2 } },
    });
    await fixture.whenStable();

    expect(selects(fixture).map((s) => s.value)).toEqual(['2']);
  });

  it('mostra a interação e a operação de dados salvas da conexão', async () => {
    const fixture = TestBed.createComponent(InspectorComponent);
    fixture.componentRef.setInput('palette', palette);
    fixture.componentRef.setInput('selection', {
      kind: 'connection',
      arrow,
      data: { kind: 'connection', connectionId: 'e1', interaction: 'async-publish', config: { ops: [{ op: 'write', count: 2 }] } },
    });
    await fixture.whenStable();

    const values = selects(fixture).map((s) => s.value);
    expect(values[0]).toBe('async-publish');
    expect(values).toContain('write');
  });
});
