import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, problemMessage } from '../core/api.service';
import { difficultyName, providerName, trackName } from '../core/format';
import { AiStatus, ChatMessage, Conversation, ConversationKind, Difficulty, GameMode, Provider, SeedSuggestion } from '../core/models';
import { RealtimeService } from '../core/realtime.service';

interface CreatedChallenge {
  challengeId: string;
  title: string;
  provider: Provider;
  difficulty: Difficulty;
  twists: string[];
  adjustments: string[];
}

/**
 * Chat com a IA local (ou, sem ela, com a busca guiada e as respostas curadas). Usado na tela inicial (contexto →
 * desafio) e na aba do entrevistador. O selo de cada resposta diz de onde ela veio.
 */
@Component({
  selector: 'lab-chat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <header class="bar">
      <span class="ai" [class.on]="status()?.mode === 'ollama'" [title]="status()?.detail ?? ''">
        <span class="dot"></span>
        {{ status() ? (status()!.mode === 'ollama' ? 'IA local · ' + status()!.model : 'Sem IA · conteúdo curado') : 'Verificando a IA…' }}
      </span>
      @if (kind() === 'contexto' && messages().length > 0) {
        <button type="button" class="link" [disabled]="busy()" (click)="reset()">Nova conversa</button>
      }
    </header>

    <div class="log" #log aria-live="polite">
      @if (messages().length === 0 && !pending()) {
        <p class="muted intro">{{ intro() }}</p>
        @if (status()?.mode === 'none') {
          <p class="muted intro small">{{ status()?.detail }}</p>
        }
      }
      @for (m of messages(); track m.id) {
        <div class="msg" [class.user]="m.role === 'user'">
          <div class="bubble">
            @for (p of paragraphs(m.text); track $index) {
              <p>{{ p }}</p>
            }
            @if (created(m); as c) {
              <div class="created">
                <strong>{{ c.title }}</strong>
                <span class="muted">{{ providerName[c.provider] }} · {{ difficultyName[c.difficulty] }}{{ c.twists.length ? ' · pegadinhas: ' + c.twists.join(', ') : '' }}</span>
                <a class="button primary" [routerLink]="['/desafios', c.challengeId]">Abrir desafio</a>
                @if (c.adjustments.length > 0) {
                  <details>
                    <summary>O que a validação ajustou na resposta da IA</summary>
                    <ul>
                      @for (a of c.adjustments; track $index) {
                        <li>{{ a }}</li>
                      }
                    </ul>
                  </details>
                }
              </div>
            }
            @for (s of suggestions(m); track s.seedId) {
              <div class="suggestion">
                <div>
                  <span class="track">{{ trackName[s.track] ?? s.track }}</span>
                  <strong>{{ s.title }}</strong>
                  <span class="muted">{{ s.summary }}</span>
                </div>
                <button type="button" class="primary" [disabled]="busy()" (click)="startSeed.emit(s)">Começar</button>
              </div>
            }
            @if (matched(m); as q) {
              <p class="muted small">Resposta curada para: “{{ q }}”</p>
            }
          </div>
          @if (m.role === 'assistant') {
            <span class="source" [class.ia]="m.source === 'ia'">{{ sourceLabel(m) }}</span>
          }
        </div>
      }
      @if (pending(); as text) {
        <div class="msg user"><div class="bubble"><p>{{ text }}</p></div></div>
        <div class="msg">
          <div class="bubble typing">
            @if (streaming()) {
              @for (p of paragraphs(streaming()); track $index) {
                <p>{{ p }}</p>
              }
            } @else {
              <p class="muted">{{ status()?.mode === 'ollama' ? 'A IA local está pensando…' : 'Buscando…' }}</p>
            }
          </div>
        </div>
      }
    </div>

    @if (quick().length > 0) {
      <div class="quick">
        @for (q of quick(); track q) {
          <button type="button" class="chip" [disabled]="busy()" (click)="ask(q)">{{ q }}</button>
        }
      </div>
    }

    <form (submit)="submit($event)">
      <textarea
        rows="2"
        [placeholder]="placeholder()"
        [value]="draft()"
        [disabled]="busy()"
        (input)="draft.set($any($event.target).value)"
        (keydown)="onKey($event)"
        aria-label="Mensagem"
      ></textarea>
      <button type="submit" class="primary" [disabled]="busy() || !draft().trim()">Enviar</button>
    </form>
    @if (error()) {
      <p class="error">{{ error() }}</p>
    }
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 8px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); padding: 10px 12px; }
    .bar { display: flex; justify-content: space-between; align-items: center; font-size: 12px; }
    .ai { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); }
    .ai .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--muted); }
    .ai.on { color: var(--text); }
    .ai.on .dot { background: var(--ok, #2f9e44); }
    .link { border: 0; background: none; color: var(--accent); padding: 0; font-size: 12px; }
    .log { display: flex; flex-direction: column; gap: 10px; max-height: var(--chat-height, 46vh); min-height: 90px; overflow-y: auto; padding: 4px 2px; }
    .msg { display: flex; flex-direction: column; align-items: flex-start; max-width: 92%; }
    .msg.user { align-self: flex-end; align-items: flex-end; }
    .bubble { border: 1px solid var(--line); border-radius: 12px; padding: 7px 11px; background: var(--bg, transparent); font-size: 13.5px; }
    .msg.user .bubble { background: var(--accent); border-color: var(--accent); color: white; }
    .bubble p { margin: 3px 0; white-space: pre-wrap; }
    .typing { opacity: 0.85; }
    .source { font-size: 11px; color: var(--muted); margin: 2px 4px; }
    .source.ia { color: var(--accent); }
    .created, .suggestion { margin-top: 8px; padding: 8px 10px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); display: flex; gap: 8px; }
    .created { flex-direction: column; align-items: flex-start; }
    .suggestion { justify-content: space-between; align-items: center; }
    .suggestion div { display: flex; flex-direction: column; gap: 2px; font-size: 13px; }
    .track { font-size: 10.5px; text-transform: uppercase; letter-spacing: 0.05em; color: var(--accent); }
    .quick { display: flex; flex-wrap: wrap; gap: 6px; }
    .chip { font-size: 12px; padding: 3px 10px; border-radius: 999px; }
    form { display: flex; gap: 8px; align-items: flex-end; }
    textarea { flex: 1; font: inherit; font-size: 14px; padding: 8px 10px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); color: var(--text); resize: vertical; }
    .muted { color: var(--muted); }
    .small { font-size: 12px; }
    .intro { margin: 4px 0; }
    .error { color: var(--danger); margin: 0; font-size: 13px; }
    details { font-size: 12px; }
    ul { margin: 4px 0; padding-left: 18px; }
  `,
})
export class ChatComponent {
  readonly kind = input.required<ConversationKind>();
  readonly challengeId = input<string>();
  readonly provider = input<Provider>('aws');
  readonly difficulty = input<Difficulty>();
  readonly mode = input<GameMode>();
  /** Perguntas prontas (no entrevistador, as perguntas curadas do desafio). */
  readonly quick = input<string[]>([]);
  readonly intro = input('');
  readonly placeholder = input('Escreva aqui…');
  /** Sugestão da biblioteca escolhida (a tela inicial monta o desafio). */
  readonly startSeed = output<SeedSuggestion>();

  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  protected readonly providerName = providerName;
  protected readonly difficultyName = difficultyName;
  protected readonly trackName = trackName;

  protected readonly status = signal<AiStatus | null>(null);
  protected readonly conversation = signal<Conversation | null>(null);
  protected readonly messages = signal<ChatMessage[]>([]);
  protected readonly draft = signal('');
  protected readonly pending = signal<string | null>(null);
  protected readonly streaming = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.api.aiStatus().then((s) => this.status.set(s)).catch(() => undefined);
    effect(() => {
      const kind = this.kind();
      const challengeId = this.challengeId();
      untracked(() => void this.open(kind, challengeId));
    });
    const unsubscribe = this.realtime.onChatDelta((e) => {
      if (this.pending() !== null && e.conversationId === this.conversation()?.id) {
        this.streaming.update((text) => text + e.delta);
        this.scroll();
      }
    });
    inject(DestroyRef).onDestroy(unsubscribe);
  }

  protected paragraphs(text: string): string[] {
    return text.split(/\n{2,}/).filter((p) => p.trim().length > 0);
  }

  protected created(m: ChatMessage): CreatedChallenge | null {
    return m.payload?.kind === 'challenge' ? m.payload : null;
  }

  protected suggestions(m: ChatMessage): SeedSuggestion[] {
    return m.payload?.kind === 'suggestions' ? m.payload.suggestions : [];
  }

  protected matched(m: ChatMessage): string | null {
    return m.payload && m.payload.kind === undefined ? (m.payload.matchedQuestion ?? null) : null;
  }

  protected sourceLabel(m: ChatMessage): string {
    switch (m.source) {
      case 'ia':
        return `IA local${m.model ? ` (${m.model})` : ''}`;
      case 'curado':
        return 'Resposta curada';
      case 'biblioteca':
        return 'Busca na biblioteca';
      default:
        return '';
    }
  }

  protected onKey(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void this.ask(this.draft());
    }
  }

  protected submit(event: Event): void {
    event.preventDefault();
    void this.ask(this.draft());
  }

  protected reset(): void {
    this.conversation.set(null);
    this.messages.set([]);
    this.error.set(null);
  }

  protected async ask(text: string): Promise<void> {
    const question = text.trim();
    if (!question || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.pending.set(question);
    this.streaming.set('');
    this.draft.set('');
    this.scroll();
    try {
      let conversation = this.conversation();
      if (!conversation) {
        conversation = await this.api.createConversation(this.kind(), this.challengeId());
        this.conversation.set(conversation);
      }
      const reply = await this.api.sendMessage(conversation.id, {
        text: question,
        provider: this.kind() === 'contexto' ? this.provider() : undefined,
        difficulty: this.difficulty(),
        mode: this.mode(),
      });
      this.messages.update((list) => [...list, reply.user, reply.assistant]);
    } catch (error) {
      this.error.set(problemMessage(error));
      this.draft.set(question);
    } finally {
      this.pending.set(null);
      this.streaming.set('');
      this.busy.set(false);
      this.scroll();
    }
  }

  private async open(kind: ConversationKind, challengeId?: string): Promise<void> {
    try {
      let conversation: Conversation | null = null;
      if (kind === 'entrevista' && challengeId) {
        conversation = await this.api.createConversation(kind, challengeId);
      } else if (kind === 'contexto') {
        const [latest] = await this.api.conversations(kind);
        conversation = latest ? await this.api.conversation(latest.id) : null;
      }
      this.conversation.set(conversation);
      this.messages.set(conversation?.messages ?? []);
      this.scroll();
    } catch (error) {
      this.error.set(problemMessage(error));
    }
  }

  private scroll(): void {
    setTimeout(() => {
      const el = this.log()?.nativeElement;
      if (el) el.scrollTop = el.scrollHeight;
    });
  }
}
