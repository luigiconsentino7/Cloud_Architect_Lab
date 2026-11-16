# 11 — Estado da implementação

Situação em 16/11/2026: a Fase 1 (MVP) está implementada, com os modos de IA `none` e `ollama`. Este documento diz o
que já existe, onde a implementação se afastou da spec (e por quê) e o que ainda falta. Quando a spec e o código
discordam, vale o que está descrito aqui.

## O que já existe

| Área | Implementado |
|---|---|
| Motor | IR agnóstica de provedor e compilador cena → IR (componentes, setas, frames de região/AZ, formas sem tipo com sugestão de mapeamento); 27 regras de arquitetura e testes funcionais dos fluxos; simulação de eventos discretos (carga, pico, spike, queda de instância e de zona, dependência lenta, duplicidade e ordenação) com failover de bancos e de caches; custo mensal; nota por dimensão, travas, rank, revisão determinística e XP, tudo como em [05](05-motor-de-avaliacao.md) |
| Conteúdo | Catálogo com AWS, Azure, GCP e Genérico; 12 sementes em 7 trilhas (PIX, ledger, cartões, mercado de capitais, Open Finance, risco, investimentos), com variação por dificuldade e pegadinhas; textos curados das regras com links oficiais; prompts versionados |
| CLI `lab` | `challenge list/validate/show/scene`, `rules validate`, `catalog validate`, `evaluate` (com export para `exports/<id>/`) |
| Backend | Monólito modular (Profile, Challenges, Design, Evaluation, Gamification, Conversations) que só se fala por contratos, verificado por ArchUnitNET; SQLite/EF Core com migrations e backup; jobs com lease, retentativa e backoff; outbox; SignalR; proteção de localhost (host, `X-Lab-Client` e `Origin`) |
| Frontend | Angular 22 com o Excalidraw 0.18 como ilha React; tela inicial com chat de contexto e biblioteca; workspace com enunciado, paleta do provedor, inspector gerado pelo JSON Schema, editor e sugestão de fluxos, problemas, dicas e entrevistador; resultado com dimensões, cenários, gráficos, custo, pontos de melhoria que destacam o elemento no canvas, referência desenhada e comentário do mentor; perfil com nível, competências, conquistas e histórico |
| Gamificação | XP creditado só pela melhoria sobre a melhor tentativa, níveis e títulos, streak, competências (média móvel), 10 conquistas do MVP |
| IA | Modos `auto`, `none` e `ollama`: contexto → desafio (delta validado), entrevistador com streaming e narrativa da revisão conferida contra os números da revisão; tudo cai para o modo determinístico se o Ollama não estiver rodando ou falhar |
| Qualidade | 148 testes .NET (motor, conteúdo, IA, integração da API e arquitetura), Vitest no frontend, jornadas E2E com Playwright nos modos sem IA e com um Ollama simulado, CI no GitHub Actions e executável único publicado pela `main` |
| Autoria | Skills do Claude Code `novo-desafio`, `calibrar-catalogo` e `revisar-avaliacao` |

Do backlog do MVP ([10](10-roadmap-e-riscos.md#backlog-do-mvp-em-ordem)), os itens 1–2, 4–6, 9 e 11–21 estão
completos (as competências aparecem em barras, não num radar). Os itens 3, 7, 8 e 10 estão parciais: região e AZ são
lidas dos frames do Excalidraw, mas a paleta não tem um atalho para criá-las; os demais estão em "O que falta".

## Desvios em relação à spec

### IA ([06](06-ia.md))

| Spec | Implementação | Por quê |
|---|---|---|
| Modelo padrão "definido no spike S4" | `qwen2.5:3b`, configurável em `Ai:Ollama:Model` | O spike S4 (evals na sua máquina) ainda não rodou; o modelo é um ponto de partida de 3B com boa saída estruturada |
| Opções `Ai:Ollama:{Endpoint, Model}` e `keep_alive` | `Ai:Ollama:{Endpoint, Model, ContextTokens, TimeoutSeconds}`; `keep_alive` fica no padrão do Ollama (5 min) | O contexto de 8 mil tokens vai como `num_ctx`; o tempo limite faz o app cair para o modo sem IA em vez de travar |
| `IInterviewer.ReplyAsync` devolve `IAsyncEnumerable<string>` | `AnswerAsync(request, onDelta, ct)` devolve a resposta final com a origem (`ia` ou `curado`) e recebe os pedaços por callback | A resposta precisa dizer de onde veio (selo na interface) e os pedaços vão direto para o SignalR |
| `IReviewNarrator` devolve sempre uma narrativa | Devolve nula quando a narrativa cita número que a revisão não tem (depois de uma chance de corrigir); falha do modelo sobe para o job tentar de novo | Só texto conferido chega à tela; falha transitória do Ollama é retentada com backoff |
| Narrador confere números e componentes citados | Confere os números; componentes não são checados | Nomes de componentes são texto livre do usuário, sem um jeito confiável de conferir; o prompt proíbe inventar |
| A narrativa de IA substitui o enunciado | A narrativa entra antes do enunciado curado, que continua com os números | Os números do desafio vêm das variáveis; o modelo não pode contradizê-los |
| Portas `ILabelMapper` e `IFlowRanker` | Não implementadas: o mapeamento usa os aliases do catálogo e a sugestão de fluxos é a busca no grafo | Ficam para quando os evals mostrarem ganho real com o modelo local |

### API e tempo real ([08](08-dados-e-api.md))

- `POST /api/conversations` recebe `{kind, challengeId?}`: `contexto` (chat da tela inicial) ou `entrevista` (uma por
  desafio; pedir de novo devolve a mesma). Provedor, dificuldade e modo vão em cada mensagem.
- `POST /api/conversations/{id}/messages` responde com a mensagem do usuário e a do assistente; no entrevistador, os
  pedaços chegam antes pelo `ChatDelta` (`conversationId`, `delta`). `GET /api/conversations` e
  `GET /api/conversations/{id}` listam e mostram as conversas.
- `POST /api/challenges` é síncrono (201 com o desafio): montar uma semente leva milissegundos, então não há
  `ChallengeReady`/`ChallengeFailed`. A personalização pela IA acontece na mensagem do chat de contexto.
- `POST /api/challenges/{id}/variations` faz o "Outro" (nova variação da mesma semente); "Ajustar" com instrução livre
  não existe.
- `flows/suggest` e `label-mappings` não são endpoints: a sugestão de fluxos roda no navegador (busca no grafo) e as
  sugestões de mapeamento vêm nos diagnósticos do `validate`.
- O histórico é `GET /api/evaluations`; `POST /api/evaluations/{id}/narrative` pede (de novo) a narrativa.
- `NarrativeReady` leva também o estado (`ready`, `rejected`, `unavailable`, `failed`).
- A referência chega ao frontend como cena pronta (o motor desenha com layout em camadas), não como IR com dagre/ELK.

### Outros

- **Busca guiada:** em memória, sem acentos e ignorando palavras vazias, com o motivo da escolha (termos casados), em
  vez de FTS5 no SQLite. Com 12 sementes, é instantânea e não precisa de índice.
- **Validação:** sob demanda (botão Validar), não ao vivo a cada traço; simular e submeter compilam o desenho do mesmo jeito no servidor.
- **Canvas:** os textos são medidos de novo quando a fonte do Excalidraw termina de carregar (o Excalidraw só
  redesenha), e abrir um desenho não conta como edição: o autosave só roda quando o usuário muda algo.
- **Testes:** xUnit v3 e Shouldly; os testes de propriedades (CsCheck) e de snapshot (Verify) da [09](09-qualidade-seguranca-operacao.md)
  viraram testes de determinismo, teoria e cenários escritos à mão.
- **Observabilidade:** tracing OpenTelemetry só quando `OTEL_EXPORTER_OTLP_ENDPOINT` está definido; as métricas da
  spec ainda não existem.
- **Segurança:** sem cabeçalho CSP por enquanto; o Excalidraw e as fontes são servidos pelo próprio app.
- **Catálogo:** os valores são ilustrativos, com link oficial e data de revisão por serviço; a calibração fina é
  trabalho contínuo com a skill `calibrar-catalogo`.

## O que falta

**Para fechar o MVP**

- Botões "Outro" e "Ajustar" na tela do desafio (o "Outro" já existe na API).
- Aplicar a sugestão de mapeamento de uma forma sem tipo com um clique (hoje ela aparece nos problemas).
- Validação ao vivo enquanto desenha.
- Importação do backup (a exportação existe).
- Spike S4: evals do personalizador, do entrevistador e do narrador com 2–3 modelos locais para escolher o padrão.

**Fases seguintes ([10](10-roadmap-e-riscos.md))**

- Fase 2: replay da simulação no canvas, comparação entre tentativas, cenários extras (soak, queda de região, crash
  entre gravar e publicar), recomendador, 25+ conquistas, ponte manual com o claude.ai, motor em WebAssembly no CI.
- Fase 3: modo Entrevista com cronômetro, Plantão, desafio diário, flashcards.
- Futuro: adaptador da Claude API (opt-in, com teto) e servidor MCP para o Claude Desktop.
