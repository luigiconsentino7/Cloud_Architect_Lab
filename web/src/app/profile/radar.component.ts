import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { radarPoint, radarPolygon } from './radar';

export interface RadarItem {
  label: string;
  value: number;
}

/** Radar de competências (0 a 100), em SVG puro: grade a cada 25 pontos, um eixo por competência. */
@Component({
  selector: 'lab-radar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg [attr.viewBox]="'0 0 ' + size + ' ' + size" role="img" [attr.aria-label]="description()">
      @for (level of levels; track level) {
        <polygon class="grid" [attr.points]="ring(level)" />
      }
      @for (axis of axes(); track axis.label) {
        <line class="axis" [attr.x1]="center" [attr.y1]="center" [attr.x2]="axis.end[0]" [attr.y2]="axis.end[1]" />
        <text class="label" [attr.x]="axis.text[0]" [attr.y]="axis.text[1]" [attr.text-anchor]="axis.anchor" dominant-baseline="middle">
          {{ axis.label }}
        </text>
      }
      <polygon class="area" [attr.points]="area()" />
      @for (axis of axes(); track axis.label) {
        <circle class="dot" [attr.cx]="axis.point[0]" [attr.cy]="axis.point[1]" r="3.5">
          <title>{{ axis.label }}: {{ axis.value }}</title>
        </circle>
      }
    </svg>
  `,
  styles: `
    :host { display: block; }
    svg { width: 100%; max-width: 380px; display: block; margin: 0 auto; overflow: visible; }
    .grid { fill: none; stroke: var(--line); stroke-width: 1; }
    .axis { stroke: var(--line); stroke-width: 1; }
    .area { fill: var(--accent); fill-opacity: 0.18; stroke: var(--accent); stroke-width: 2; }
    .dot { fill: var(--accent); }
    .label { font-size: 11px; fill: var(--muted); }
  `,
})
export class RadarComponent {
  readonly items = input.required<RadarItem[]>();

  protected readonly size = 320;
  protected readonly center = 160;
  protected readonly radius = 105;
  protected readonly levels = [25, 50, 75, 100];

  protected readonly area = computed(() => radarPolygon(this.items().map((i) => i.value), this.radius, this.center));
  protected readonly description = computed(() => this.items().map((i) => `${i.label} ${Math.round(i.value)}`).join(', '));
  protected readonly axes = computed(() => {
    const items = this.items();
    return items.map((item, index) => {
      const end = radarPoint(index, items.length, 100, this.radius, this.center);
      const text = radarPoint(index, items.length, 100, this.radius + 16, this.center);
      const dx = text[0] - this.center;
      return {
        label: item.label,
        value: Math.round(item.value),
        end,
        text,
        point: radarPoint(index, items.length, item.value, this.radius, this.center),
        anchor: Math.abs(dx) < 8 ? 'middle' : dx > 0 ? 'start' : 'end',
      };
    });
  });

  protected ring(level: number): string {
    return radarPolygon(this.items().map(() => level), this.radius, this.center);
  }
}
