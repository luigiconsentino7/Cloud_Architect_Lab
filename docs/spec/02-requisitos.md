# 02 — Requisitos

Fases: **MVP** (Fase 1), **F2**, **F3**, **F4**, **F5** — ver [10 — Roadmap](10-roadmap-e-riscos.md).

## Requisitos funcionais

### Novo desafio e chat (CHAT, DES)

| ID | Requisito | Fase |
|---|---|---|
| RF-CHAT-01 | Tela de novo desafio com caixa de contexto em linguagem livre e histórico de conversas | MVP |
| RF-CHAT-02 | **Seletor de provedor** (AWS, Azure, GCP, Genérico) visível antes de gerar o desafio; padrão = último usado | MVP |
| RF-CHAT-03 | Seleção de dificuldade (Júnior, Pleno, Sênior, Staff, Automática) e modo (Treino; Entrevista e Plantão na F3) | MVP |
| RF-CHAT-04 | Com IA ligada: respostas em streaming e até 2 perguntas de calibração antes de gerar | MVP |
| RF-CHAT-05 | Mascaramento de dados sensíveis (CPF/CNPJ, e-mails, telefones, IPs, hostnames internos, chaves, números de cartão) sempre que o conteúdo for sair da máquina, com aviso de quantos itens foram mascarados | F2 |
| RF-DES-01 | Desafio estruturado: título, narrativa, domínio, dificuldade, provedor, casos de uso com capacidades exigidas, SLOs, workload, restrições (regulatórias, orçamento, região), competências, XP; partes ocultas: cenários ocultos, solução de referência | MVP |
| RF-DES-02 | Todo desafio vem de uma **semente** curada; antes de aparecer, passa por validação de schema, checagens semânticas e pelo motor rodando a solução de referência (nota ≥ 85) | MVP |
| RF-DES-03 | Busca na biblioteca de sementes por texto, domínio, tags, provedor e dificuldade (funciona sem IA) | MVP |
| RF-DES-04 | Variação procedural determinística: volumetria, SLOs, restrições e "pegadinhas" dentro das faixas da semente | MVP |
| RF-DES-05 | Ações "Outro" e "Ajustar" no cartão do desafio | MVP |
| RF-DES-06 | Personalização por IA (modos `ollama` e `anthropic`): a IA produz um delta pequeno sobre a semente, nunca o desafio inteiro | MVP |
| RF-DES-07 | Ponte manual com o claude.ai: exportar prompt pronto e importar a resposta colada, com a mesma validação | F2 |

### Autoria de conteúdo (AUT)

| ID | Requisito | Fase |
|---|---|---|
| RF-AUT-01 | Sementes, explicações das regras, catálogo e prompts são arquivos versionados no repositório, validados por JSON Schema | MVP |
| RF-AUT-02 | CLI `lab` com `challenge validate`, `rules validate`, `catalog validate` e `export <tentativa>` | MVP |
| RF-AUT-03 | Skills do Claude Code no repositório para criar sementes, calibrar o catálogo e revisar avaliações exportadas | MVP |
| RF-AUT-04 | Biblioteca inicial com ≥ 12 sementes validadas, cobrindo todas as trilhas | MVP |

### Entrevistador (ENT)

| ID | Requisito | Fase |
|---|---|---|
| RF-ENT-01 | Sem IA: painel com esclarecimentos curados da semente e busca por palavra-chave | MVP |
| RF-ENT-02 | Com IA: chat que responde perguntas de esclarecimento usando só a parte pública do desafio e os esclarecimentos curados | MVP |
| RF-ENT-03 | Dicas curadas em 3 níveis (direção → conceito → sugestão concreta), com custo crescente de XP | MVP |
| RF-ENT-04 | Modo Entrevista: perguntas de follow-up após a submissão | F3 |

### Canvas (CAN)

| ID | Requisito | Fase |
|---|---|---|
| RF-CAN-01 | Canvas Excalidraw com desenho livre, zoom, pan, desfazer/refazer, atalhos, tema claro e escuro | MVP |
| RF-CAN-02 | Paleta de componentes do provedor da tentativa, com busca e categorias, mais "Externos do desafio" (SPI, bandeira, B3…) | MVP |
| RF-CAN-03 | Componentes inseridos carregam `catalogId`, ID estável e configuração | MVP |
| RF-CAN-04 | Inspector de propriedades gerado a partir do schema de configuração do componente, com validação | MVP |
| RF-CAN-05 | Setas vinculadas viram conexões; tipo de interação inferido (síncrona, assíncrona, acesso a dados…) e editável; seta tracejada sugere assíncrona | MVP |
| RF-CAN-06 | Fronteiras como frames: região e AZ (MVP); rede/sub-redes (F2) | MVP |
| RF-CAN-07 | Elementos livres viram anotações; formas com rótulo reconhecível ("Redis", "Kafka") geram sugestão de mapeamento aplicável em 1 clique | MVP |
| RF-CAN-08 | Autosave (debounce de 2 s) com versão para concorrência otimista e rascunho local no navegador | MVP |
| RF-CAN-09 | Painel de problemas com validação ao vivo (≤ 300 ms após a mudança) | MVP |
| RF-CAN-10 | Exportar PNG, SVG e JSON | MVP |
| RF-CAN-11 | Clicar num achado ou ponto de melhoria destaca e centraliza os elementos no canvas | MVP |
| RF-CAN-12 | Capacidade estimada ao vivo por componente (modelo analítico) | F2 |
| RF-CAN-13 | Replay da simulação sobre o canvas (cor por saturação, contadores de fila, linha do tempo) | F2 |

### Fluxos (FLX)

| ID | Requisito | Fase |
|---|---|---|
| RF-FLX-01 | Editor de fluxos por caso de uso: selecionar o UC e clicar nas setas em ordem | MVP |
| RF-FLX-02 | Sugestão automática de fluxos por busca no grafo (com IA: ranking das alternativas) | MVP |
| RF-FLX-03 | Passos numerados visíveis no canvas | MVP |

### Avaliação (AVA)

| ID | Requisito | Fase |
|---|---|---|
| RF-AVA-01 | **Validar**: compilação + regras, sem consumir tentativa | MVP |
| RF-AVA-02 | **Simular rascunho** (Treino): cenários públicos de carga, sem nota | MVP |
| RF-AVA-03 | **Submeter**: pipeline assíncrono com progresso por etapa em tempo real | MVP |
| RF-AVA-04 | Testes funcionais por caso de uso: caminho, capacidades exigidas, ordem dos passos | MVP |
| RF-AVA-05 | Cenários de carga: baseline, pico, spike (MVP); soak (F2) | MVP |
| RF-AVA-06 | Cenários de caos: perda de instâncias, queda de AZ, dependência lenta ou indisponível (MVP); queda de região (F2) | MVP |
| RF-AVA-07 | Cenários de integridade: entrega duplicada e retry do cliente (MVP); crash após efeito, crash entre gravar e publicar, ordenação, leitura obsoleta (F2) | MVP |
| RF-AVA-08 | Regras de segurança e compliance (conjunto básico no MVP) | MVP |
| RF-AVA-09 | Custo mensal estimado com breakdown por componente | MVP |
| RF-AVA-10 | Revisão determinística: resumo, pontos fortes, pontos de melhoria com elementos e evidências, perguntas de follow-up, comparação com a referência | MVP |
| RF-AVA-11 | Narrativa por IA sobre a revisão determinística (modos com IA), sem alterar a nota | MVP |
| RF-AVA-12 | Nota por dimensão, final e rank, com a contribuição de cada item visível | MVP |
| RF-AVA-13 | Manifesto de avaliação salvo; reavaliar o mesmo snapshot reproduz o resultado | MVP |
| RF-AVA-14 | Solução de referência revelada após a submissão (MVP); diff estrutural com a tentativa (F2) | MVP |
| RF-AVA-15 | Comparação entre tentativas (delta por dimensão e por SLO) | F2 |

### Gamificação (GAM)

| ID | Requisito | Fase |
|---|---|---|
| RF-GAM-01 | XP por avaliação, creditado de forma idempotente | MVP |
| RF-GAM-02 | Níveis e títulos | MVP |
| RF-GAM-03 | Competências com rating e radar | MVP |
| RF-GAM-04 | Conquistas: 10 no MVP, 25+ na F2 | MVP |
| RF-GAM-05 | Streak diário com congelamento | F2 |
| RF-GAM-06 | Trilhas temáticas com chefão | F2 |
| RF-GAM-07 | Recomendador de próximo desafio | F2 |
| RF-GAM-08 | Modos Entrevista, Plantão e Desafio diário | F3 |
| RF-GAM-09 | Flashcards gerados dos pontos de melhoria, com repetição espaçada | F3 |

### IA em execução e configuração (IA, CFG)

| ID | Requisito | Fase |
|---|---|---|
| RF-IA-01 | Modo de IA configurável: `auto` (padrão: usa Ollama se estiver disponível, senão `none`), `none`, `ollama`, `anthropic` | MVP |
| RF-IA-02 | **Todo o loop principal funciona no modo `none`** | MVP |
| RF-IA-03 | Indicador do modo de IA ativo na interface | MVP |
| RF-IA-04 | Adaptador Claude API desligado por padrão; para ligar, exige chave em segredo local e teto mensal de gasto > 0; bloqueia ao atingir o teto | F2 |
| RF-CFG-01 | Backup automático diário do banco (7 cópias) e exportação/importação completa em JSON | MVP |
| RF-CFG-02 | Modo remoto opcional (Cloudflare Tunnel + Access) com validação do token do Access | F2 |

## Requisitos não funcionais

| ID | Categoria | Requisito | Meta |
|---|---|---|---|
| RNF-01 | Desempenho | Validação ao vivo (compilação + regras) | ≤ 300 ms p95, grafos de até 60 nós |
| RNF-02 | Desempenho | Pipeline completo, conjunto padrão de cenários | ≤ 30 s p95 em 4 núcleos, pico de até 5 mil req/s, 60 nós |
| RNF-03 | Desempenho | Chat com IA local | primeiro token ≤ 5 s com o modelo escolhido no spike S4 |
| RNF-04 | Reprodutibilidade | Mesmo manifesto ⇒ mesmos resultados e mesma nota, byte a byte | 100% |
| RNF-05 | Custo | Custo recorrente obrigatório | **zero**: nenhuma dependência paga no caminho padrão |
| RNF-06 | Offline | Loop completo sem internet nos modos `none` e `ollama` | 100% |
| RNF-07 | Privacidade | Nos modos `none` e `ollama`, nenhum dado sai da máquina | 100% |
| RNF-08 | Instalação | Rodar com .NET SDK ou executável único, sem Docker obrigatório | `dotnet run` ou 1 arquivo |
| RNF-09 | Durabilidade | Autosave sem perda de mais de 5 s de trabalho; backup diário | — |
| RNF-10 | Segurança | Escuta só em loopback no modo local; proteção contra DNS rebinding (validação do Host); no modo remoto, nenhuma requisição sem token válido do Cloudflare Access | — |
| RNF-11 | Observabilidade | Traces, métricas e logs OpenTelemetry por etapa do pipeline; eventos/s da simulação | — |
| RNF-12 | Extensibilidade | Novo serviço de um arquétipo existente = só YAML; nova semente = só YAML | — |
| RNF-13 | Manutenibilidade | Fronteiras de módulos verificadas por testes de arquitetura; cobertura do motor ≥ 85% | — |
| RNF-14 | Portabilidade | Bibliotecas do motor sem I/O e compatíveis com trimming/AOT e WebAssembly | — |
| RNF-15 | Usabilidade | pt-BR; tema claro/escuro; canvas para telas ≥ 1280 px; páginas de progresso responsivas | — |
| RNF-16 | Limites | Cena ≤ 5 MB; ≤ 150 nós; ≤ 400 arestas; orçamento de eventos e de tempo por execução de simulação | — |

## Critérios de aceite do MVP

**Cenário 1 — desafio sem IA**
- Dado o modo de IA `none` e o provedor AWS
- Quando escrevo "quero praticar PIX" e escolho Sênior
- Então vejo 3 desafios da trilha PIX com ≥ 3 casos de uso, ≥ 3 SLOs, volumetria, orçamento e restrição de residência de dados
- E cada um tem, no manifesto, a nota da solução de referência (≥ 85) para a variação sorteada

**Cenário 2 — desenho tipado e desenho livre**
- Dado o workspace aberto
- Quando arrasto "SQS" da paleta e ligo uma Lambda a ela
- Então a seta é inferida como `async-publish` e o inspector mostra a configuração da fila
- E um retângulo desenhado à mão com o texto "Redis" gera a sugestão "mapear para ElastiCache"

**Cenário 3 — avaliação completa**
- Dado um desenho com fluxos definidos para todos os casos de uso
- Quando submeto
- Então recebo, em ≤ 30 s: resultado funcional por UC, ≥ 2 cenários de carga, ≥ 1 de caos, ≥ 1 de integridade, custo mensal, nota por dimensão e ≥ 3 pontos de melhoria que destacam elementos no canvas
- E o XP é creditado uma única vez, mesmo que eu recarregue a página ou o job seja reprocessado

**Cenário 4 — reprodutibilidade**
- Dada uma tentativa já avaliada
- Quando reavalio o mesmo snapshot
- Então resultados e nota são idênticos

**Cenário 5 — defeito clássico detectado**
- Dado um desenho em que o consumidor de pagamentos grava no banco sem idempotência
- Quando o cenário de entrega duplicada roda
- Então o relatório mostra "N efeitos duplicados" no UC monetário, a dimensão Consistência fica abaixo de 50, a trava de nota é aplicada e há um ponto de melhoria apontando o consumidor (regra DAT-002)

**Cenário 6 — custo zero e offline**
- Dado o computador sem internet e o modo `none`
- Quando faço o ciclo completo (desafio → desenho → submissão → resultado → XP)
- Então tudo funciona e nenhuma requisição de rede sai da máquina
