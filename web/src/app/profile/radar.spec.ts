import { radarPoint, radarPolygon } from './radar';

describe('radar', () => {
  it('o primeiro eixo aponta para cima e os outros giram no sentido horário', () => {
    expect(radarPoint(0, 4, 100, 100, 150)).toEqual([150, 50]);
    expect(radarPoint(1, 4, 100, 100, 150)).toEqual([250, 150]);
    expect(radarPoint(2, 4, 50, 100, 150)).toEqual([150, 200]);
  });

  it('prende os valores entre 0 e 100 e monta o polígono', () => {
    expect(radarPoint(3, 4, 140, 100, 150)).toEqual([50, 150]);
    expect(radarPolygon([0, 0, 0], 100, 150)).toBe('150,150 150,150 150,150');
  });
});
