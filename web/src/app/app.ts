import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ApiService } from './core/api.service';
import { fmt } from './core/format';
import { ProgressSummary } from './core/models';
import { RealtimeService } from './core/realtime.service';

@Component({
  selector: 'lab-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly api = inject(ApiService);
  protected readonly realtime = inject(RealtimeService);
  protected readonly fmt = fmt;
  protected readonly progress = signal<ProgressSummary | null>(null);
  protected readonly version = signal<string | null>(null);

  constructor() {
    void this.realtime.start();
    void this.api.health().then((h) => this.version.set(h.version)).catch(() => undefined);
    effect(() => {
      this.realtime.xpVersion();
      void this.api.me().then((me) => this.progress.set(me.progress)).catch(() => undefined);
    });
  }

  protected percent(p: ProgressSummary): number {
    const span = Math.max(1, p.nextLevelXp - p.levelStartXp);
    return Math.min(100, Math.max(0, ((p.totalXp - p.levelStartXp) / span) * 100));
  }
}
