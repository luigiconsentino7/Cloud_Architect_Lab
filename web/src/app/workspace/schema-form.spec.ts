import { JsonSchema } from '../core/models';
import { fieldsOf, getPath, parseFieldValue, setPath } from './schema-form';

const serviceSchema: JsonSchema = {
  type: 'object',
  properties: {
    instances: {
      type: 'object',
      title: 'Instâncias',
      properties: {
        min: { type: 'integer', minimum: 1, default: 2, title: 'Mínimo' },
        max: { type: 'integer', minimum: 1, default: 10, title: 'Máximo' },
      },
    },
    vcpu: { type: 'number', enum: [0.25, 0.5, 1, 2], default: 1, title: 'vCPU' },
    zones: { type: 'array', items: { type: 'string', enum: ['a', 'b', 'c'] }, title: 'Zonas' },
    responsibilities: { type: 'array', items: { type: 'string' }, title: 'Responsabilidades' },
    observability: { type: 'object', title: 'Operação', properties: { tracing: { type: 'boolean', default: false, title: 'Tracing' } } },
  },
};

describe('schema-form', () => {
  it('achata o schema em campos agrupados pelo objeto pai', () => {
    const fields = fieldsOf(serviceSchema);

    expect(fields.map((f) => `${f.path}:${f.kind}`)).toEqual([
      'instances.min:integer',
      'instances.max:integer',
      'vcpu:select',
      'zones:multiselect',
      'responsibilities:list',
      'observability.tracing:boolean',
    ]);
    expect(fields[0].group).toBe('Instâncias');
  });

  it('atribui por caminho sem mutar e remove objetos que ficam vazios', () => {
    const original = { instances: { min: 3 } };
    const updated = setPath(original, 'instances.max', 12);
    const cleared = setPath(setPath(updated, 'instances.max', undefined), 'instances.min', undefined);

    expect(original).toEqual({ instances: { min: 3 } });
    expect(getPath(updated, 'instances.max')).toBe(12);
    expect(cleared).toEqual({});
  });

  it('converte o valor digitado para o tipo do campo', () => {
    const [min, , vcpu, zones, responsibilities] = fieldsOf(serviceSchema);

    expect(parseFieldValue(min, '7.6')).toBe(8);
    expect(parseFieldValue(min, '')).toBeUndefined();
    expect(parseFieldValue(vcpu, '0.5')).toBe(0.5);
    expect(parseFieldValue(zones, [])).toBeUndefined();
    expect(parseFieldValue(responsibilities, 'notify, audit-log')).toEqual(['notify', 'audit-log']);
  });
});
