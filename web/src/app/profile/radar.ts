/** Geometria do radar: o eixo i aponta para cima e gira no sentido horário; valores de 0 a 100. */
export function radarPoint(index: number, count: number, value: number, radius: number, center: number): [number, number] {
  const angle = -Math.PI / 2 + (2 * Math.PI * index) / count;
  const r = (Math.max(0, Math.min(100, value)) / 100) * radius;
  return [round(center + r * Math.cos(angle)), round(center + r * Math.sin(angle))];
}

/** Pontos de um polígono SVG (`x,y x,y …`) para os valores dados. */
export function radarPolygon(values: number[], radius: number, center: number): string {
  return values.map((v, i) => radarPoint(i, values.length, v, radius, center).join(',')).join(' ');
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}
