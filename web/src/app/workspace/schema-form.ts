import { JsonSchema } from '../core/models';

/** Campo do inspector gerado a partir do `configSchema` do catálogo. */
export interface Field {
  path: string;
  label: string;
  kind: 'boolean' | 'number' | 'integer' | 'text' | 'select' | 'multiselect' | 'list';
  options?: (string | number)[];
  min?: number;
  max?: number;
  defaultValue?: unknown;
  group?: string;
}

/** Achata o JSON Schema em campos com caminho pontilhado (`instances.min`), agrupados pelo título do objeto pai. */
export function fieldsOf(schema: JsonSchema | undefined, prefix = '', group?: string): Field[] {
  const fields: Field[] = [];
  for (const [key, property] of Object.entries(schema?.properties ?? {})) {
    const path = prefix ? `${prefix}.${key}` : key;
    const label = property.title ?? key;
    if (property.type === 'object') {
      fields.push(...fieldsOf(property, path, property.title ?? key));
      continue;
    }

    const base = { path, label, defaultValue: property.default, group };
    if (property.type === 'array') {
      fields.push(property.items?.enum ? { ...base, kind: 'multiselect', options: property.items.enum } : { ...base, kind: 'list' });
    } else if (property.enum) {
      fields.push({ ...base, kind: 'select', options: property.enum });
    } else if (property.type === 'boolean') {
      fields.push({ ...base, kind: 'boolean' });
    } else if (property.type === 'integer' || property.type === 'number') {
      fields.push({ ...base, kind: property.type, min: property.minimum, max: property.maximum });
    } else {
      fields.push({ ...base, kind: 'text' });
    }
  }
  return fields;
}

export function getPath(source: Record<string, unknown> | undefined, path: string): unknown {
  let current: unknown = source;
  for (const part of path.split('.')) {
    if (current == null || typeof current !== 'object') return undefined;
    current = (current as Record<string, unknown>)[part];
  }
  return current;
}

/** Atribuição imutável por caminho; `undefined` remove a chave (e objetos que ficam vazios). */
export function setPath(source: Record<string, unknown> | undefined, path: string, value: unknown): Record<string, unknown> {
  const [head, ...rest] = path.split('.');
  const copy: Record<string, unknown> = { ...(source ?? {}) };
  if (rest.length === 0) {
    if (value === undefined || value === '') delete copy[head];
    else copy[head] = value;
    return copy;
  }

  const child = setPath(copy[head] as Record<string, unknown> | undefined, rest.join('.'), value);
  if (Object.keys(child).length === 0) delete copy[head];
  else copy[head] = child;
  return copy;
}

/** Converte o texto digitado no tipo do campo (número vazio = remover a configuração e voltar ao padrão). */
export function parseFieldValue(field: Field, raw: string | boolean | string[]): unknown {
  switch (field.kind) {
    case 'boolean':
      return raw === true;
    case 'number':
    case 'integer': {
      if (raw === '' || raw == null) return undefined;
      const value = Number(raw);
      if (Number.isNaN(value)) return undefined;
      return field.kind === 'integer' ? Math.round(value) : value;
    }
    case 'multiselect':
      return Array.isArray(raw) && raw.length > 0 ? raw : undefined;
    case 'list':
      return typeof raw === 'string' ? raw.split(',').map((s) => s.trim()).filter(Boolean) : raw;
    case 'select': {
      if (raw === '') return undefined;
      const numeric = field.options?.find((o) => typeof o === 'number' && String(o) === raw);
      return numeric ?? raw;
    }
    default:
      return raw === '' ? undefined : raw;
  }
}

/** Campos fixos da configuração de uma conexão (espelham o `EdgeConfig` do motor). */
export const connectionFields: Field[] = [
  { path: 'timeoutMs', label: 'Timeout (ms)', kind: 'number', min: 1, group: 'Chamada' },
  { path: 'idempotencyKey', label: 'Envia chave de idempotência', kind: 'boolean', group: 'Chamada' },
  { path: 'callRatio', label: 'Chamadas por requisição', kind: 'number', min: 0, group: 'Chamada' },
  { path: 'parallelGroup', label: 'Grupo paralelo', kind: 'text', group: 'Chamada' },
  { path: 'retries.max', label: 'Retries', kind: 'integer', min: 0, max: 10, group: 'Retry' },
  { path: 'retries.backoff', label: 'Backoff', kind: 'select', options: ['none', 'fixed', 'exponential', 'exponential-jitter'], group: 'Retry' },
  { path: 'retries.baseMs', label: 'Espera base (ms)', kind: 'number', min: 0, group: 'Retry' },
  { path: 'circuitBreaker.enabled', label: 'Circuit breaker', kind: 'boolean', group: 'Circuit breaker' },
  { path: 'circuitBreaker.errorRateThreshold', label: 'Abre com taxa de erro de', kind: 'number', min: 0, max: 1, group: 'Circuit breaker' },
  { path: 'circuitBreaker.openMs', label: 'Fica aberto por (ms)', kind: 'number', min: 0, group: 'Circuit breaker' },
  { path: 'trigger', label: 'Executa', kind: 'select', options: ['always', 'on-failure', 'on-cache-miss'], group: 'Quando' },
  { path: 'triggerOf', label: 'Depende da conexão', kind: 'select', options: [], group: 'Quando' },
  { path: 'delivery', label: 'Publicação', kind: 'select', options: ['direct', 'outbox'], group: 'Mensageria' },
  { path: 'batchSize', label: 'Tamanho do lote', kind: 'integer', min: 1, group: 'Mensageria' },
];

export const interactionOptions = ['sync-request', 'async-publish', 'async-consume', 'data-access', 'replication', 'cdc'];

export const dataOps = ['read', 'write', 'conditional-write', 'transaction'];

export const optionLabels: Record<string, string> = {
  always: 'sempre',
  'on-failure': 'quando a conexão escolhida falha',
  'on-cache-miss': 'no cache miss da conexão escolhida',
  direct: 'direta',
  outbox: 'outbox transacional',
  none: 'sem espera',
  fixed: 'fixa',
  exponential: 'exponencial',
  'exponential-jitter': 'exponencial com jitter',
  'sync-request': 'requisição síncrona',
  'async-publish': 'publica evento',
  'async-consume': 'consome mensagens',
  'data-access': 'acesso a dados',
  replication: 'replicação',
  cdc: 'CDC (captura de mudanças)',
  read: 'leitura',
  write: 'escrita',
  'conditional-write': 'escrita condicional',
  transaction: 'transação',
};
