import { Difficulty, Dimension, GameMode, Provider, Severity } from './models';

const number = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 });
const integer = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 0 });
const usd = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });

export const fmt = {
  n: (value: number | undefined | null) => (value == null ? '—' : number.format(value)),
  int: (value: number | undefined | null) => (value == null ? '—' : integer.format(value)),
  usd: (value: number | undefined | null) => (value == null ? '—' : usd.format(value)),
  pct: (fraction: number | undefined | null) =>
    fraction == null ? '—' : `${new Intl.NumberFormat('pt-BR', { maximumFractionDigits: fraction > 0.99 && fraction < 1 ? 3 : 1 }).format(fraction * 100)}%`,
  ms: (value: number | undefined | null) => (value == null ? '—' : `${number.format(value)} ms`),
  date: (iso: string | undefined) => (iso ? new Date(iso).toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' }) : '—'),
};

export const providerName: Record<Provider, string> = { aws: 'AWS', azure: 'Azure', gcp: 'Google Cloud' };

export const difficultyName: Record<Difficulty, string> = { junior: 'Júnior', pleno: 'Pleno', senior: 'Sênior', staff: 'Staff' };

export const difficultyOrder: Difficulty[] = ['junior', 'pleno', 'senior', 'staff'];

export const modeName: Record<GameMode, string> = { treino: 'Treino', entrevista: 'Entrevista', plantao: 'Plantão' };

export const dimensionName: Record<Dimension, string> = {
  functional: 'Funcional',
  performance: 'Performance e escala',
  resilience: 'Resiliência',
  consistency: 'Consistência e integridade',
  security: 'Segurança e compliance',
  cost: 'Custo',
  operability: 'Operabilidade',
};

export const severityName: Record<Severity, string> = { critical: 'Crítica', high: 'Alta', medium: 'Média', low: 'Baixa' };

export const trackName: Record<string, string> = {
  pix: 'PIX e pagamentos instantâneos',
  ledger: 'Core bancário e ledger',
  cartoes: 'Cartões',
  capitais: 'Mercado de capitais',
  openfinance: 'Open Finance',
  risco: 'Risco e antifraude',
  investimentos: 'Investimentos',
};

export const competencyName: Record<string, string> = {
  escalabilidade: 'Escalabilidade',
  resiliencia: 'Resiliência',
  consistencia: 'Consistência de dados',
  mensageria: 'Mensageria e eventos',
  'baixa-latencia': 'Baixa latência',
  seguranca: 'Segurança e compliance',
  custo: 'Custo (FinOps)',
  observabilidade: 'Observabilidade',
};

export const metricName: Record<string, string> = {
  'latency.p50': 'p50 de latência',
  'latency.p95': 'p95 de latência',
  'latency.p99': 'p99 de latência',
  'success-rate': 'taxa de sucesso',
  'async-completion.p95': 'p95 assíncrono',
  'async-completion.p99': 'p99 assíncrono',
  'duplicate-effects': 'efeitos duplicados',
  'lost-events': 'eventos perdidos',
  'ordering-violations': 'violações de ordem',
};

/** Alvo de um SLO para exibir: "≤ 1.000 ms", "≥ 99,95%", "0". */
export function sloTarget(metric: string, max?: number, min?: number): string {
  if (metric === 'success-rate') return `≥ ${fmt.pct(min)}`;
  if (metric.startsWith('latency.') || metric.startsWith('async-completion.')) return `≤ ${fmt.ms(max)}`;
  return `≤ ${fmt.int(max)}`;
}

export function rankClass(rank: string | undefined): string {
  return `rank rank-${(rank ?? 'd').toLowerCase()}`;
}
