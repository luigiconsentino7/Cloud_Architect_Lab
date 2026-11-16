// Modelos da API (espelham os DTOs do backend: camelCase, enums em kebab-case).

export type Provider = 'aws' | 'azure' | 'gcp';
export type Difficulty = 'junior' | 'pleno' | 'senior' | 'staff';
export type GameMode = 'treino' | 'entrevista' | 'plantao';
export type Dimension = 'functional' | 'performance' | 'resilience' | 'consistency' | 'security' | 'cost' | 'operability';
export type Severity = 'critical' | 'high' | 'medium' | 'low';

export interface ProgressSummary {
  totalXp: number;
  level: number;
  title: string;
  levelStartXp: number;
  nextLevelXp: number;
  streakDays: number;
}

export interface Me {
  id: string;
  displayName: string;
  locale: string;
  defaultProvider: Provider;
  preferences: Record<string, unknown>;
  progress: ProgressSummary;
}

export interface TwistInfo {
  id: string;
  title: string;
  description: string;
  minDifficulty: Difficulty;
}

export interface LibraryItem {
  seedId: string;
  title: string;
  summary: string;
  track: string;
  domain: string;
  tags: string[];
  difficulties: Difficulty[];
  twists: TwistInfo[];
  matchedTerms: string[];
  score: number;
}

export interface LatencyDist {
  p50: number;
  p99: number;
}

export interface CapabilityRequirement {
  capability: string;
  target?: string;
  before?: string;
  description?: string;
}

export interface UseCaseSpec {
  id: string;
  name: string;
  description: string;
  entryClient: string;
  monetary: boolean;
  critical: boolean;
  requiresOrdering: boolean;
  requiredCapabilities: CapabilityRequirement[];
  negativePaths: { dependency: string; expect: string; description: string }[];
}

export interface ExternalSystemSpec {
  id: string;
  name: string;
  description: string;
  latency: LatencyDist;
  availability: number;
  rateLimitRps?: number;
  idempotent: boolean;
}

export interface LoadProfile {
  profile: 'constant' | 'ramp' | 'spike';
  ref?: string;
  rps?: number;
  fromRps?: number;
  toRps?: number;
  peakRps?: number;
  baseRps?: number;
  rampFor?: number;
  holdFor?: number;
  duration?: number;
}

export interface ScenarioSpec {
  id: string;
  name: string;
  description: string;
  kind: 'load' | 'chaos' | 'integrity';
  visibility: 'public' | 'hidden';
  load: LoadProfile;
}

export interface SloSpec {
  id: string;
  useCase?: string;
  metric: string;
  max?: number;
  min?: number;
  scenarios: string[];
  description: string;
}

export interface ConstraintsSpec {
  budgetUsdMonth: number;
  dataResidencyBrazil: boolean;
  compliance: string[];
  requireEncryptionAtRest: boolean;
  requireAuditLog: boolean;
  rpoSeconds?: number;
  rtoSeconds?: number;
}

export interface Challenge {
  id: string;
  seedId: string;
  title: string;
  summary: string;
  narrative: string;
  track: string;
  domain: string;
  tags: string[];
  difficulty: Difficulty;
  provider: Provider;
  mode: GameMode;
  region: string;
  useCases: UseCaseSpec[];
  externalSystems: ExternalSystemSpec[];
  workload: { baselineRps: number; mix: Record<string, { share: number }> };
  scenarios: ScenarioSpec[];
  slos: SloSpec[];
  hiddenScenarios: number;
  constraints: ConstraintsSpec;
  competencies: string[];
  hintCount: number;
  clarifications: { question: string; keywords: string[]; answer: string }[];
  variables: Record<string, number>;
  /** Modelo da IA local que personalizou o enunciado (ausente quando veio direto da biblioteca). */
  personalizedBy?: string;
  personalizationNote?: string;
  createdAt: string;
}

export interface ChallengeSummary {
  id: string;
  seedId: string;
  title: string;
  track: string;
  provider: Provider;
  difficulty: Difficulty;
  mode: GameMode;
  createdAt: string;
}

export interface FlowDefinition {
  useCaseId: string;
  steps: string[];
}

export interface Attempt {
  id: string;
  challengeId: string;
  number: number;
  provider: Provider;
  mode: GameMode;
  status: 'draft' | 'submitted' | 'evaluating' | 'evaluated' | 'failed';
  scene: ExcalidrawScene;
  draftVersion: number;
  flows: FlowDefinition[];
  hintLevelsUsed: number[];
  startedAt: string;
  submittedAt?: string;
  submissionEvaluationId?: string;
}

export interface ExcalidrawScene {
  type?: string;
  version?: number;
  elements: readonly SceneElement[];
  appState?: Record<string, unknown>;
}

/** O que o app lê de um elemento do Excalidraw (o resto passa direto). */
export interface SceneElement {
  id: string;
  type: string;
  isDeleted?: boolean;
  groupIds?: readonly string[];
  containerId?: string | null;
  text?: string;
  x: number;
  y: number;
  width: number;
  height: number;
  customData?: Record<string, unknown>;
  startBinding?: { elementId: string } | null;
  endBinding?: { elementId: string } | null;
  [key: string]: unknown;
}

export interface Diagnostic {
  code: string;
  severity: 'error' | 'warning' | 'info';
  message: string;
  elementIds: string[];
  irIds: string[];
  suggestions?: { catalogId: string; displayName: string }[];
}

export interface FindingView {
  ruleId: string;
  title: string;
  dimension: Dimension;
  severity: Severity;
  message: string;
  nodeIds: string[];
  edgeIds: string[];
}

export interface CapabilityCheck {
  capability: string;
  target?: string;
  satisfied: boolean;
  detail: string;
  nodeIds: string[];
  edgeIds: string[];
}

export interface FunctionalResult {
  useCaseId: string;
  name: string;
  status: 'passed' | 'partial' | 'failed';
  pathOk: boolean;
  pathProblems: string[];
  capabilities: CapabilityCheck[];
  negativePaths: CapabilityCheck[];
  score: number;
}

export interface IrNode {
  id: string;
  catalogId: string;
  archetype: string;
  label: string;
  config: Record<string, unknown>;
}

export interface IrEdge {
  id: string;
  from: string;
  to: string;
  interaction: string;
  config: Record<string, unknown>;
}

export interface ArchitectureGraph {
  provider: Provider;
  nodes: IrNode[];
  edges: IrEdge[];
  flows: FlowDefinition[];
  /** Formas com texto que não viraram componente (o id é o do elemento no canvas). */
  annotations?: { id: string; text: string; near?: string }[];
}

export interface MappingOption {
  catalogId: string;
  displayName: string;
  source: 'catalogo' | 'ia';
  reason?: string;
}

export interface LabelMapping {
  elementId: string;
  label: string;
  kind: 'sem-tipo' | 'anotacao';
  options: MappingOption[];
}

export interface Validation {
  irHash: string;
  diagnostics: Diagnostic[];
  findings: FindingView[];
  functional: FunctionalResult[];
  graph: ArchitectureGraph;
}

export interface EvaluationTicket {
  evaluationId: string;
  attemptId: string;
  kind: 'draft' | 'submission';
}

export interface Hint {
  level: number;
  text: string;
  hintLevelsUsed: number[];
}

// ---- resultado da avaliação (EvaluationResult do motor) ----

export interface CheckResult {
  id: string;
  sloId: string;
  scenarioId: string;
  scenarioName: string;
  scenarioKind: 'load' | 'chaos' | 'integrity';
  visibility: 'public' | 'hidden';
  useCaseId?: string;
  metric: string;
  observed?: number;
  target: number;
  isMinimum: boolean;
  status: 'passed' | 'failed';
  score: number;
  dimension: Dimension;
  monetaryViolation: boolean;
  description: string;
}

export interface ScenarioScore {
  scenarioId: string;
  scenarioName: string;
  dimension: Dimension;
  score: number;
  weight: number;
  checkIds: string[];
}

export interface RulePenalty {
  ruleId: string;
  dimension: Dimension;
  severity: Severity;
  findings: number;
  confirmedFindings: number;
  points: number;
  confirmedIn: string[];
}

export interface DimensionScore {
  dimension: Dimension;
  weight: number;
  score: number;
  baseScore: number;
  penalty: number;
  basis: string;
  contribution: number;
  scenarios: ScenarioScore[];
  rules: RulePenalty[];
}

export interface ScoreResult {
  dimensions: DimensionScore[];
  baseScore: number;
  cap?: { value: number; reason: string; applied: boolean };
  finalScore: number;
  rank: string;
  checks: CheckResult[];
  costScore: number;
}

export interface ReviewItem {
  id: string;
  kind: 'rule' | 'scenario' | 'functional' | 'cost';
  title: string;
  severity: Severity;
  dimension: Dimension;
  impact: number;
  causesCap: boolean;
  details: string[];
  evidence: string[];
  why?: string;
  how?: string;
  tradeoffs?: string;
  ruleId?: string;
  confirmed: boolean;
  nodeIds: string[];
  edgeIds: string[];
}

export interface Review {
  summary: { itemId: string; text: string; impact: number }[];
  improvements: ReviewItem[];
  strengths: { title: string; detail: string; nodeIds: string[]; edgeIds: string[] }[];
  followUps: string[];
  reference?: {
    patternsMissing: string[];
    patternsExtra: string[];
    patternsShared: string[];
    componentsMissing: string[];
    componentsExtra: string[];
    referenceCostUsd?: number;
    rationale: string;
  };
  study: { topic: string; links: { title: string; url: string }[]; ruleIds: string[] }[];
}

export interface CostLine {
  nodeId: string;
  label: string;
  archetype: string;
  category: string;
  monthlyUsd: number;
  basis: string;
}

export interface CostReport {
  monthlyUsd: number;
  lines: CostLine[];
  budgetUsd: number;
  assumptions: string[];
}

export interface XpBreakdown {
  base: number;
  scoreFactor: number;
  multipliers: { name: string; factor: number }[];
  hintFactor: number;
  attemptXp: number;
  previousBestXp: number;
  credited: number;
}

export interface ScenarioResult {
  scenarioId: string;
  name: string;
  kind: 'load' | 'chaos' | 'integrity';
  visibility: 'public' | 'hidden';
  useCases: Record<string, { requests: number; successRate: number; latency: { p50: number; p95: number; p99: number } }>;
  bottlenecks: { nodeId: string; reason: string; detail: string }[];
  timeCompression: number;
}

export interface EvaluationResult {
  manifest: Record<string, unknown>;
  graph: ArchitectureGraph;
  diagnostics: Diagnostic[];
  simulation: { scenarios: ScenarioResult[] };
  cost: CostReport;
  referenceCostUsd?: number;
  score: ScoreResult;
  review: Review;
  xp: XpBreakdown;
}

export interface Evaluation {
  id: string;
  attemptId: string;
  challengeId: string;
  kind: 'draft' | 'submission';
  status: 'queued' | 'running' | 'completed' | 'failed';
  stage: string;
  percent: number;
  detail?: string;
  finalScore?: number;
  rank?: string;
  error?: string;
  createdAt: string;
  completedAt?: string;
  result?: EvaluationResult;
  narrative?: NarrativeState;
}

/** Narrativa da revisão pela IA local: só entra se citar números que estão na revisão determinística. */
export interface NarrativeState {
  status: 'pending' | 'ready' | 'rejected' | 'unavailable' | 'failed';
  paragraphs: string[];
  model?: string;
  promptVersion?: string;
  detail?: string;
  updatedAt: string;
}

export interface TimeSeries {
  seconds: number;
  offeredRps: number[];
  useCases: Record<string, { requests: number[]; errors: number[]; p99Ms: number[] }>;
  nodes: Record<string, { utilization: number[]; queue: number[]; instances: number[]; throttled: number[] }>;
}

export interface ReferenceView {
  graph: ArchitectureGraph;
  rationale: string;
  costUsd?: number;
  scene?: ExcalidrawScene;
  flows?: FlowDefinition[];
}

export interface HistoryItem {
  evaluationId: string;
  attemptId: string;
  challengeId: string;
  title: string;
  kind: 'draft' | 'submission';
  status: string;
  finalScore?: number;
  rank?: string;
  createdAt: string;
  completedAt?: string;
}

export interface Achievement {
  code: string;
  title: string;
  criterion: string;
  unlocked: boolean;
  unlockedAt?: string;
}

export interface Competency {
  competency: string;
  rating: number;
  samples: number;
}

// ---- catálogo (paleta) ----

export interface JsonSchema {
  type?: string;
  title?: string;
  description?: string;
  enum?: (string | number)[];
  default?: unknown;
  minimum?: number;
  maximum?: number;
  properties?: Record<string, JsonSchema>;
  items?: JsonSchema;
}

export interface PaletteComponent {
  id: string;
  archetype: string;
  displayName: string;
  shortName?: string;
  category: string;
  aliases: string[];
  scope: string;
  isDefault: boolean;
  shared: boolean;
  capabilities: Record<string, string>;
  configSchema: JsonSchema;
  docs?: string;
}

export interface Palette {
  catalogVersion: string;
  provider: Provider;
  archetypes: { id: string; displayName: string; category: string }[];
  components: PaletteComponent[];
}

// ---- IA e conversas ----

export interface AiStatus {
  mode: 'none' | 'ollama';
  configuredMode: string;
  ollamaReachable: boolean;
  model?: string;
  endpoint: string;
  installedModels: string[];
  detail: string;
}

export type ConversationKind = 'contexto' | 'entrevista';

export interface SeedSuggestion {
  seedId: string;
  title: string;
  summary: string;
  track: string;
  tags: string[];
  matchedTerms: string[];
}

/** Dados estruturados de uma resposta: sugestões da biblioteca, desafio montado pela IA ou pergunta curada usada. */
export type ChatPayload =
  | { kind: 'suggestions'; provider?: Provider; difficulty?: Difficulty; mode?: GameMode; suggestions: SeedSuggestion[] }
  | { kind: 'challenge'; challengeId: string; title: string; seedId: string; provider: Provider; difficulty: Difficulty; narrative?: string; twists: string[]; adjustments: string[] }
  | { kind?: undefined; matchedQuestion?: string };

export interface ChatMessage {
  id: string;
  number: number;
  role: 'user' | 'assistant';
  text: string;
  source: 'usuario' | 'ia' | 'curado' | 'biblioteca';
  model?: string;
  promptVersion?: string;
  createdAt: string;
  payload?: ChatPayload;
}

export interface Conversation {
  id: string;
  kind: ConversationKind;
  challengeId?: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messages: ChatMessage[];
}

export interface ConversationSummary {
  id: string;
  kind: ConversationKind;
  challengeId?: string;
  title: string;
  updatedAt: string;
}

// ---- ajuste do desafio ----

export interface VariableOption {
  description: string;
  step: number;
  /** Faixa [mín, máx] por dificuldade. */
  ranges: Partial<Record<Difficulty, [number, number]>>;
}

export interface ChallengeOptions {
  challengeId: string;
  seedId: string;
  provider: Provider;
  difficulty: Difficulty;
  mode: GameMode;
  difficulties: Difficulty[];
  twists: TwistInfo[];
  variables: Record<string, VariableOption>;
  currentVariables: Record<string, number>;
  currentTwists: string[];
  aiAvailable: boolean;
}

export interface VariationRequest {
  provider?: Provider;
  difficulty?: Difficulty;
  mode?: GameMode;
  twists?: string[];
  variables?: Record<string, number>;
  instruction?: string;
}
