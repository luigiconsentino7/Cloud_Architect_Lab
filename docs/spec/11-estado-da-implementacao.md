# 11 — Estado da implementação

Situação em 16/11/2026: a Fase 1 (MVP) está completa, com os modos de IA `none` e `ollama`. Este documento diz o que
já existe, onde a implementação se afastou da spec (e por quê) e o que ficou para as próximas fases. Quando a spec e
o código discordam, vale o que está descrito aqui.

## O que já existe

| Área | Implementado |
|---|---|
| Motor | IR agnóstica de provedor e compilador cena → IR (componentes, setas, frames de região/AZ, formas sem tipo com sugestão de mapeamento); 27 regras de arquitetura e testes funcionais dos fluxos; simulação de eventos discretos (carga, pico, spike, queda de instância e de zona, dependência lenta, duplicidade e ordenação) com failover de bancos e de caches; custo mensal; nota por dimensão, travas, rank, revisão determinística e XP, tudo como em [05](05-motor-de-avaliacao.md) |
| Conteúdo | Catálogo com AWS, Azure, GCP e Genérico; 12 sementes em 7 trilhas (PIX, ledger, cartões, mercado de capitais, Open Finance, risco, investimentos), com variação por dificuldade e pegadinhas; textos curados das regras com links oficiais; prompts versionados; datasets de evals de IA |
| CLI `lab` | `challenge list/validate/show/scene`, `rules validate`, `catalog validate`, `evaluate` (com export para `exports/<id>/`) e `ai eval` (spike S4) |
| Backend | Monólito modular (Profile, Challenges, Design, Evaluation, Gamification, Conversations) que só se fala por contratos, verificado por ArchUnitNET; SQLite/EF Core com migrations e backup; jobs com lease, retentativa e backoff; outbox; SignalR; proteção de localhost (host, `X-Lab-Client` e `Origin`) e CSP restritiva |
| Frontend | Angular 22 com o Excalidraw 0.18 como ilha React; tela inicial com chat de contexto e biblioteca; tela do desafio com enunciado, "Outro" e "Ajustar", paleta do provedor com fronteiras de AZ e região, inspector gerado pelo JSON Schema, editor e sugestão de fluxos, problemas com validação ao vivo e mapeamento de formas desenhadas à mão com um clique, dicas e entrevistador; resultado com dimensões, cenários, gráficos, custo, pontos de melhoria que destacam o elemento no canvas, referência desenhada e comentário do mentor; perfil com nível, radar de competências, conquistas, histórico e backup (exportar e importar) |
| Gamificação | XP creditado só pela melhoria sobre a melhor tentativa, níveis e títulos, streak, competências (média móvel), 10 conquistas do MVP |
| IA | Modos `auto`, `none` e `ollama`: contexto → desafio (delta validado), "Ajustar" por instrução em texto, entrevistador com streaming, narrativa da revisão conferida contra os números da revisão e mapeamento de formas sem tipo; tudo cai para o modo determinístico se o Ollama não estiver rodando ou falhar |
| Qualidade | 164 testes .NET (motor, conteúdo e evals, IA, integração da API e arquitetura), 24 testes Vitest no frontend, duas jornadas E2E com Playwright (modo sem IA e modo com um Ollama simulado, usando o cliente real do OllamaSharp), CI no GitHub Actions e executável único publicado pela `main` |
| Autoria | Skills do Claude Code `novo-desafio`, `calibrar-catalogo` e `revisar-avaliacao` |

Todos os 21 itens do backlog do MVP ([10](10-roadmap-e-riscos.md#backlog-do-mvp-em-ordem)) estão implementados.

## Desvios em relação à spec

### IA ([06](06-ia.md))

| Spec | Implementação | Por quê |
|---|---|---|
| Modelo padrão "definido no spike S4" | `qwen2.5:3b`, configurável em `Ai:Ollama:Model`; o spike S4 é o comando `lab ai eval`, que compara modelos na máquina do usuário e sugere o padrão | Os evals precisam da GPU da máquina de referência; o comando existe e foi validado contra um Ollama simulado |
| Opções `Ai:Ollama:{Endpoint, Model}` e `keep_alive` | `Ai:Ollama:{Endpoint, Model, ContextTokens, TimeoutSeconds}`; `keep_alive` fica no padrão do Ollama (5 min) | O contexto de 8 mil tokens vai como `num_ctx`; o tempo limite faz o app cair para o modo sem IA em vez de travar |
| `IInterviewer.ReplyAsync` devolve `IAsyncEnumerable<string>` | `AnswerAsync(request, onDelta, ct)` devolve a resposta final com a origem (`ia` ou `curado`) e recebe os pedaços por callback | A resposta precisa dizer de onde veio (selo na interface) e os pedaços vão direto para o SignalR |
| `IReviewNarrator` devolve sempre uma narrativa | Devolve nula quando a narrativa cita número que a revisão não tem (depois de uma chance de corrigir); falha do modelo sobe para o job tentar de novo | Só texto conferido chega à tela; falha transitória do Ollama é retentada com backoff |
| Narrador confere números e componentes citados | Confere os números; componentes não são checados | Nomes de componentes são texto livre do usuário, sem um jeito confiável de conferir; o prompt proíbe inventar |
| A narrativa de IA substitui o enunciado | A narrativa entra antes do enunciado curado, que continua com os números | Os números do desafio vêm das variáveis; o modelo não pode contradizê-los |
| `ILabelMapper` sugere para qualquer forma | Os aliases do catálogo resolvem o que casam; a IA só é consultada para rótulos que não casaram, sob demanda (botão), e o schema só aceita ids da paleta ou "nenhum" | Cada consulta leva segundos num modelo local: a validação ao vivo não pode esperar por ela |
| Porta `IFlowRanker` | Não implementada: a sugestão de fluxos é a busca no grafo | Os evals ainda não mostraram ganho real com o modelo local |

### API e tempo real ([08](08-dados-e-api.md))

- `POST /api/conversations` recebe `{kind, challengeId?}`: `contexto` (chat da tela inicial) ou `entrevista` (uma por
  desafio; pedir de novo devolve a mesma). Provedor, dificuldade e modo vão em cada mensagem.
- `POST /api/conversations/{id}/messages` responde com a mensagem do usuário e a do assistente; no entrevistador, os
  pedaços chegam antes pelo `ChatDelta` (`conversationId`, `delta`). `GET /api/conversations` e
  `GET /api/conversations/{id}` listam e mostram as conversas.
- `POST /api/challenges` é síncrono (201 com o desafio): montar uma semente leva milissegundos, então não há
  `ChallengeReady`/`ChallengeFailed`. A personalização pela IA acontece na mensagem do chat de contexto.
- `POST /api/challenges/{id}/variations` faz o "Outro" (corpo vazio) e o "Ajustar" (nuvem, nível, modo, pegadinhas,
  variáveis ou `instruction` para a IA local); `GET /api/challenges/{id}/options` traz as faixas e as pegadinhas da
  semente para a tela de ajuste.
- `flows/suggest` não é endpoint: a sugestão de fluxos roda no navegador (busca no grafo). `label-mappings` existe e
  junta as sugestões do catálogo e as da IA.
- O histórico é `GET /api/evaluations`; `POST /api/evaluations/{id}/narrative` pede (de novo) a narrativa.
- `NarrativeReady` leva também o estado (`ready`, `rejected`, `unavailable`, `failed`).
- O backup (`/api/backup/export` e `/api/backup/import`) é um JSON versionado com todas as tabelas do usuário; a
  importação recusa formato estranho ou versão mais nova, copia o banco atual para `backups/` e substitui tudo numa
  transação.
- A referência chega ao frontend como cena pronta (o motor desenha com layout em camadas), não como IR com dagre/ELK.

### Outros

- **Busca guiada:** em memória, sem acentos e ignorando palavras vazias, com o motivo da escolha (termos casados), em
  vez de FTS5 no SQLite. Com 12 sementes, é instantânea e não precisa de índice. A mesma busca escolhe os candidatos
  do personalizador no app e nos evals.
- **Validação ao vivo:** roda depois de cada autosave do desenho ou dos fluxos (o autosave já espera o usuário parar),
  não a cada traço.
- **Canvas:** os textos são medidos de novo quando a fonte do Excalidraw termina de carregar (o Excalidraw só
  redesenha); abrir um desenho não conta como edição, e o autosave só roda quando algo muda de fato.
- **Fontes do Excalidraw:** a versão 0.18 sempre acrescenta o CDN esm.sh como segunda origem de cada fonte, e o
  Chrome chegava a pedi-la. Um ajuste no `FontFace` antes de carregar o Excalidraw tira o CDN: todas as fontes vêm do
  próprio app.
- **CSP:** `default-src 'self'`, scripts só do app (mais `'wasm-unsafe-eval'` para a exportação de imagens do
  Excalidraw), estilos inline permitidos (componentes Angular e Excalidraw), WebSocket do SignalR no próprio host e
  `frame-ancestors 'none'`. O CSS crítico embutido do Angular foi desligado porque exige um script inline.
- **Testes:** xUnit v3 e Shouldly; os testes de propriedades (CsCheck) e de snapshot (Verify) da [09](09-qualidade-seguranca-operacao.md)
  viraram testes de determinismo, teoria e cenários escritos à mão.
- **Observabilidade:** tracing OpenTelemetry só quando `OTEL_EXPORTER_OTLP_ENDPOINT` está definido; as métricas da
  spec ainda não existem.
- **Catálogo:** os valores são ilustrativos, com link oficial e data de revisão por serviço; a calibração fina é
  trabalho contínuo com a skill `calibrar-catalogo`.

## O que fica para as próximas fases ([10](10-roadmap-e-riscos.md))

- Rodar `lab ai eval` na máquina de referência (RTX 3050, 4 GB) com 2–3 modelos de 3–4B e fixar o modelo padrão.
- Fase 2: replay da simulação no canvas, comparação entre tentativas, cenários extras (soak, queda de região, crash
  entre gravar e publicar), recomendador, 25+ conquistas, ponte manual com o claude.ai, porta `IFlowRanker`, métricas
  OpenTelemetry e motor em WebAssembly no CI.
- Fase 3: modo Entrevista com cronômetro, Plantão, desafio diário, flashcards.
- Futuro: adaptador da Claude API (opt-in, com teto) e servidor MCP para o Claude Desktop.
