import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export interface ChartSeries {
  name: string;
  values: number[];
  color: string;
}

/** Gráfico de linhas em SVG puro (séries por segundo da simulação), sem biblioteca de gráficos. */
@Component({
  selector: 'lab-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <figure>
      <figcaption>{{ title() }}</figcaption>
      <svg [attr.viewBox]="'0 0 ' + width + ' ' + height" preserveAspectRatio="none" role="img" [attr.aria-label]="title()">
        <line x1="0" [attr.y1]="height - 1" [attr.x2]="width" [attr.y2]="height - 1" class="axis" />
        @for (s of paths(); track s.name) {
          <polyline [attr.points]="s.points" [attr.stroke]="s.color" fill="none" stroke-width="1.6" vector-effect="non-scaling-stroke" />
        }
      </svg>
      <div class="legend">
        @for (s of paths(); track s.name) {
          <span><i [style.background]="s.color"></i>{{ s.name }} (máx. {{ s.max }})</span>
        }
      </div>
    </figure>
  `,
  styles: `
    figure { margin: 0; }
    figcaption { font-size: 12px; color: var(--muted); margin-bottom: 4px; }
    svg { width: 100%; height: 120px; background: var(--surface-2); border-radius: 6px; }
    .axis { stroke: var(--line); }
    .legend { display: flex; flex-wrap: wrap; gap: 10px; font-size: 11.5px; color: var(--muted); margin-top: 4px; }
    .legend i { display: inline-block; width: 10px; height: 3px; margin-right: 4px; vertical-align: middle; }
  `,
})
export class ChartComponent {
  readonly title = input('');
  readonly series = input<ChartSeries[]>([]);
  protected readonly width = 600;
  protected readonly height = 120;

  protected readonly paths = computed(() =>
    this.series().map((s) => {
      const max = Math.max(1e-9, ...s.values);
      const step = this.width / Math.max(1, s.values.length - 1);
      const points = s.values.map((v, i) => `${(i * step).toFixed(1)},${(this.height - 4 - (v / max) * (this.height - 10)).toFixed(1)}`).join(' ');
      return { name: s.name, color: s.color, points, max: new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 }).format(max) };
    }),
  );
}
