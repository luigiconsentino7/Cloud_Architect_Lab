import { ChallengeOptions } from '../core/models';
import { allowedTwists, clampVariables, rescaleVariables } from './adjust';

const options: ChallengeOptions = {
  challengeId: 'c1',
  seedId: 'pix/envio-pix',
  provider: 'aws',
  difficulty: 'senior',
  mode: 'treino',
  difficulties: ['pleno', 'senior', 'staff'],
  twists: [
    { id: 'spi-degradado', title: 'SPI degradado', description: '', minDifficulty: 'senior' },
    { id: 'orcamento-apertado', title: 'Orçamento apertado', description: '', minDifficulty: 'junior' },
  ],
  variables: { baselineRps: { description: 'TPS', step: 50, ranges: { pleno: [300, 600], senior: [700, 1400] } } },
  currentVariables: { baselineRps: 1000 },
  currentTwists: ['spi-degradado'],
  aiAvailable: false,
};

describe('ajuste do desafio', () => {
  it('leva os números para a faixa do nível novo, no passo da variável', () => {
    expect(clampVariables(options, 'pleno', { baselineRps: 1000 })).toEqual({ baselineRps: 600 });
    expect(clampVariables(options, 'senior', { baselineRps: 1023 })).toEqual({ baselineRps: 1000 });
    expect(clampVariables(options, 'senior', {})).toEqual({ baselineRps: 1050 });
  });

  it('ao trocar de nível, o número mantém a posição relativa na faixa', () => {
    expect(rescaleVariables(options, 'pleno', 'senior', { baselineRps: 450 })).toEqual({ baselineRps: 1050 });
    expect(rescaleVariables(options, 'senior', 'pleno', { baselineRps: 1400 })).toEqual({ baselineRps: 600 });
  });

  it('mostra só as pegadinhas que valem no nível', () => {
    expect(allowedTwists(options, 'pleno').map((t) => t.id)).toEqual(['orcamento-apertado']);
    expect(allowedTwists(options, 'senior').map((t) => t.id)).toEqual(['spi-degradado', 'orcamento-apertado']);
  });
});
