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
| Modelo padrão "definido no spike S4" | `gemma3:4b`, configurável em `Ai:Ollama:Model`; o `qwen2.5:3b` é a opção rápida. O spike S4 é o comando `lab ai eval`, que compara modelos na máquina do usuário e sugere o padrão | Duas rodadas reais na máquina de referência (RTX 3050 de 4 GB): os dois passam no S4, e o gemma3:4b erra menos e escreve melhor, ao custo de ser ≈ 4× mais lento ([segunda rodada](#spike-s4-segunda-rodada-real-16112026-1751)) |
| Opções `Ai:Ollama:{Endpoint, Model}` e `keep_alive` | `Ai:Ollama:{Endpoint, Model, ContextTokens, TimeoutSeconds, ProbeTimeoutMs}`; `keep_alive` fica no padrão do Ollama (5 min); o endpoint padrão é `http://127.0.0.1:11434` | O contexto de 8 mil tokens vai como `num_ctx`; o tempo limite faz o app cair para o modo sem IA em vez de travar; no Windows, `localhost` tenta o IPv6 primeiro e a primeira conexão perdia segundos |
| Detecção do Ollama no modo `auto` | Checagem paciente (4 s, repetida se só demorou), que mantém a IA ligada por 2 min se o Ollama estava bom e engasgou, lembra de uma falha por 5 s e do sucesso por 30 s | Uma checagem de 1,5 s derrubava a IA por 30 s num engasgo (visto num `lab ai eval` real no Windows: o narrador e o mapeador saíram "reprovados" sem o modelo ter sido chamado) |
| `IInterviewer.ReplyAsync` devolve `IAsyncEnumerable<string>` | `AnswerAsync(request, onDelta, ct)` devolve a resposta final com a origem (`ia` ou `curado`) e recebe os pedaços por callback | A resposta precisa dizer de onde veio (selo na interface) e os pedaços vão direto para o SignalR |
| `IReviewNarrator` devolve sempre uma narrativa | Devolve nula quando a narrativa cita número que a revisão não tem ou tem parágrafo cortado no meio da frase (depois de uma chance de corrigir); falha do modelo sobe para o job tentar de novo | Só texto conferido chega à tela; falha transitória do Ollama é retentada com backoff; no S4 real o qwen2.5:3b fechou o JSON no meio de uma frase ("…como o") |
| Narrador confere números e componentes citados | Confere os números; componentes não são checados | Nomes de componentes são texto livre do usuário, sem um jeito confiável de conferir; o prompt proíbe inventar |
| A narrativa de IA substitui o enunciado | A narrativa entra antes do enunciado curado, que continua com os números | Os números do desafio vêm das variáveis; o modelo não pode contradizê-los |
| `ILabelMapper` sugere para qualquer forma | Os aliases do catálogo resolvem o que casam; a IA só é consultada para rótulos que não casaram, sob demanda (botão), e o schema só aceita ids da paleta ou "nenhum" | Cada consulta leva segundos num modelo local: a validação ao vivo não pode esperar por ela |
| Esquema do personalizador: "a lista de IDs válidos" | JSON Schema por semente (`anyOf`, um ramo por candidata com o `seedId` fixo e só as pegadinhas e variáveis dela), com o esquema simples como reserva se o Ollama recusar o primeiro | No S4 real, em 19 de 24 respostas (10 do qwen e 9 do gemma) o modelo escolheu pegadinhas de outra semente (o esquema simples listava as de todas) e a validação as descartava |
| Mapeador: escolhe um id ou "nenhum" | O modelo responde em três campos, nesta ordem: `funcao` (o que o rótulo faz), `componente` (sim ou não) e só então o `catalogId`; prompt v3 define componente pela função técnica e trata nome de time, squad e área como anotação | No S4 real os dois modelos tipavam anotações como serviço (`time de plataforma` virou `aws.cognito`) quando "nenhum" era uma opção entre 30 ids; com o portão `componente` da v2 o qwen2.5:3b passou a recusar quase tudo (5 de 12) e o gemma3:4b ainda tipava nomes de time (10 de 12) |
| Entrevistador: "diga que não tem essa informação" sobre os cenários ocultos | Prompt v2 manda confirmar que eles existem e que o conteúdo é desconhecido; o eval reprova quem nega | No S4 real o qwen2.5:3b respondeu "não há cenários de teste ocultos", o que é falso, e o eval antigo só olhava vazamento |
| Entrevistador: respostas livres | Prompt v3 (frases completas, sem citar mensagens anteriores, sem frase pronta para copiar, provedor e região contam como dado do enunciado), temperatura 0,3 e no máximo 300 tokens | No S4 real o qwen2.5:3b respondia só "300 req/s", trocou "1.000 req/s" por "8000 req/s" e repetia a frase pronta do prompt ("Não está definido. Pode assumir…") até para o pedido da solução de referência; o gemma3:4b abria a conversa com "como mencionado anteriormente" |
| Personalizador: narrativa e esclarecimentos | Prompt v2 pede saída curta (narrativa de 2 a 3 frases, no máximo 1 esclarecimento, motivo de uma frase) | A geração é por token e o gemma3:4b levava ≈ 15 s para montar um desafio |
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

## Spike S4: primeira rodada real (16/11/2026)

`lab ai eval` na máquina de referência (RTX 3050 Laptop, 4 GB), Ollama local, modelo já carregado. Tempos: mediana por
caso. A rodada usou o personalizador com esquema simples, o mapeador e o entrevistador na versão 1.

| | qwen2.5:3b | gemma3:4b |
|---|---|---|
| Personalizador (12 contextos) | 11/12 (92%) · 3,5 s | 12/12 (100%) · 14,5 s |
| Entrevistador (8 perguntas) | 8/8 · 0,6 s | 8/8 · 2,2 s |
| Narrador (3 revisões) | 3/3 · 6,2 s | 3/3 · 11,3 s |
| Mapeador (8 rótulos) | 5/8 (63%) · 1,0 s | 6/8 (75%) · 3,7 s |
| Primeiro token · tokens/s | 0,1 s · 71,5 | 0,6 s · 18,6 |
| Critérios do S4 | passa | passa |

- O qwen2.5:3b deve caber inteiro nos 4 GB e roda 4× mais rápido; o gemma3:4b provavelmente passa dos 4 GB e divide
  camadas com a CPU (inferência pela velocidade, não medida), ficando na beirada dos 15 tokens/s. Os dois passam no
  S4; a escolha é precisão × velocidade.
- Pontos fracos vistos nos dois: pegadinhas de outras sementes (corrigido com o esquema por semente) e anotações
  tipadas como serviço (corrigido com a decisão `componente`). Só o qwen: uma semente errada (razão contábil virou
  Pix), "PDF" virou `aws.secretsmanager` e a negação dos cenários ocultos.
- Os números não dizem se a escrita em português é boa: o relatório novo traz as narrativas, os comentários do mentor
  e as respostas do entrevistador para serem lidos.
- Decisão provisória, superada pela [segunda rodada](#spike-s4-segunda-rodada-real-16112026-1751): o padrão passou a
  ser `gemma3:4b`.

## Spike S4: segunda rodada real (16/11/2026, 17:51)

Mesma máquina, agora com o personalizador de esquema por semente, o mapeador v2 (decide `componente` antes do id) e o
entrevistador v2, e com o relatório trazendo o que a IA escreveu. O mapeador passou a ter 12 rótulos (4 anotações).

| | qwen2.5:3b | gemma3:4b |
|---|---|---|
| Personalizador (12 contextos) | 12/12 (100%) · 3,2 s | 12/12 (100%) · 15,0 s |
| Entrevistador (8 perguntas) | 7/8 (88%) · 0,5 s | 7/8 (88%) · 2,3 s |
| Narrador (3 revisões) | 3/3 · 6,9 s | 3/3 · 11,1 s |
| Mapeador (12 rótulos) | 5/12 (42%) · 0,8 s | 10/12 (83%) · 3,7 s |
| Primeiro token · tokens/s | 0,1 s · 71,0 | 0,6 s · 18,8 |
| Critérios do S4 | passa | passa |

- **O esquema por semente resolveu o personalizador:** os dois acertaram os 12 contextos e o qwen2.5:3b não precisou de
  nenhum ajuste da validação (antes, 19 de 24 respostas traziam pegadinha de outra semente).
- **Lendo as amostras, o gemma3:4b é o mais confiável.** O qwen2.5:3b responde seco ("300 req/s"), trocou o volume do
  enunciado por outro número ("8000 req/s" em vez de 1.000), usou a frase pronta do prompt ("Não está definido. Pode
  assumir…") para o pedido da solução de referência, cortou um parágrafo do narrador no meio da frase e, no mapeador,
  recusou quase todos os componentes. O gemma3:4b escreve frases completas e explica, mas abriu uma conversa nova com
  "como mencionado anteriormente" e tipou como serviço nomes de time e squad.
- **Um caso do dataset estava errado:** a pergunta sobre "contrato fechado com fornecedor de nuvem" esperava "não está
  definido", mas o enunciado traz o provedor (a resposta do gemma3:4b, "AWS", estava certa). O caso foi trocado por
  "Quantas pessoas formam o time de desenvolvimento?", e o dataset ganhou duas perguntas de volume em outras sementes e
  uma lista de frases proibidas no pedido da solução de referência.
- **O enunciado dado ao entrevistador tinha itens sem texto:** cenários sem descrição saíam como "- Dia normal:" e
  sistemas externos com dois espaços, o que o modelo lia como "não definido". O teste novo percorre todas as sementes e
  níveis (eram ≈ 80 itens assim).
- **Decisão: o padrão passa a ser `gemma3:4b`**, com o `qwen2.5:3b` como a opção rápida (`Ai:Ollama:Model`). Os dois
  passam no S4, então a escolha é de confiabilidade e escrita contra velocidade; para um app em que a nota é
  determinística e a IA só personaliza, explica e comenta, errar menos pesa mais que responder em 0,5 s.

Mudanças a partir desta rodada (ainda não medidas num Ollama real): mapeador v3 (`funcao` → `componente` →
`catalogId`, definição por função técnica, nomes de time como anotação, exemplos com classes que não estão no dataset),
entrevistador v3 (e temperatura 0,3, no máximo 300 tokens), personalizador v2 (saída mais curta, para o gemma3:4b),
narrador que rejeita parágrafo cortado, enunciado sem itens vazios e `lab ai eval --context` para testar uma janela
menor. Limites: cada caso rodou uma vez com temperatura acima de zero, então uma diferença de um caso é ruído; e a
suposição de que o gemma3:4b passa dos 4 GB de VRAM continua sem medida.

## O que fica para as próximas fases ([10](10-roadmap-e-riscos.md))

- Repetir o `lab ai eval` com os prompts da segunda rodada (mapeador v3, entrevistador v3, personalizador v2, narrador
  que rejeita parágrafo cortado), medir o ganho de velocidade do gemma3:4b com `--context 4096` e confirmar o padrão.
- Fase 2: replay da simulação no canvas, comparação entre tentativas, cenários extras (soak, queda de região, crash
  entre gravar e publicar), recomendador, 25+ conquistas, ponte manual com o claude.ai, porta `IFlowRanker`, métricas
  OpenTelemetry e motor em WebAssembly no CI.
- Fase 3: modo Entrevista com cronômetro, Plantão, desafio diário, flashcards.
- Futuro: adaptador da Claude API (opt-in, com teto) e servidor MCP para o Claude Desktop.
