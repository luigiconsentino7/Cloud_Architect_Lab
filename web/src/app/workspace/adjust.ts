import { difficultyOrder } from '../core/format';
import { ChallengeOptions, Difficulty, TwistInfo } from '../core/models';

/** Pegadinhas que valem no nível escolhido. */
export function allowedTwists(options: ChallengeOptions, difficulty: Difficulty): TwistInfo[] {
  const level = difficultyOrder.indexOf(difficulty);
  return options.twists.filter((t) => difficultyOrder.indexOf(t.minDifficulty) <= level);
}

/** Prende cada valor na faixa do nível e no passo da variável (ao trocar de nível, os números acompanham). */
export function clampVariables(options: ChallengeOptions, difficulty: Difficulty, values: Record<string, number>): Record<string, number> {
  const result: Record<string, number> = {};
  for (const [name, variable] of Object.entries(options.variables)) {
    const range = variable.ranges[difficulty];
    if (!range) continue;
    const [min, max] = range;
    const value = values[name] ?? (min + max) / 2;
    const clamped = Math.min(max, Math.max(min, value));
    const step = variable.step > 0 ? variable.step : 1;
    result[name] = Math.min(max, Math.max(min, Number((min + Math.round((clamped - min) / step) * step).toFixed(6))));
  }
  return result;
}

/** Ao trocar de nível, cada número mantém a posição relativa na faixa (meio da faixa de pleno → meio da de sênior). */
export function rescaleVariables(options: ChallengeOptions, from: Difficulty, to: Difficulty, values: Record<string, number>): Record<string, number> {
  const moved: Record<string, number> = {};
  for (const [name, variable] of Object.entries(options.variables)) {
    const source = variable.ranges[from];
    const target = variable.ranges[to];
    const value = values[name];
    if (!target || value === undefined) continue;
    const position = source && source[1] > source[0] ? Math.min(1, Math.max(0, (value - source[0]) / (source[1] - source[0]))) : 0.5;
    moved[name] = target[0] + position * (target[1] - target[0]);
  }
  return clampVariables(options, to, moved);
}
