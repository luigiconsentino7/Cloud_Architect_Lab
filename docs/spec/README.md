# Cloud Architect Lab — Especificação v0.1

| | |
|---|---|
| **Status** | MVP implementado (ver [11 — Estado da implementação](11-estado-da-implementacao.md)) |
| **Data** | 15/11/2026 |
| **Requisitos fixos** | Backend em .NET · **nenhum custo recorrente obrigatório** |
| **Demais tecnologias** | Propostas com alternativas e justificativas, abertas a questionamento (ver [ADRs](03-arquitetura.md#decisões-de-arquitetura-adrs)) |
| **Máquina de referência** | 16 GB de RAM, NVIDIA RTX 3050 Laptop (4 GB de VRAM); uso só no desktop |

## Resumo em uma página

O Cloud Architect Lab é um app de estudo de System Design para quem trabalha no mercado financeiro. Ele gira em torno de um loop curto e repetível:

1. **Contexto → desafio.** Você escreve o que quer praticar ("trabalho numa corretora e quero praticar distribuição de cotações") e escolhe a nuvem num botão: **AWS, Azure, GCP ou Genérico**. O app devolve um desafio estruturado: narrativa, casos de uso, SLOs, volumetria, restrições regulatórias e orçamento.
2. **Desenho.** Você resolve num canvas estilo Excalidraw. Os componentes vêm de uma paleta do provedor escolhido e carregam tipo e configuração (ex.: SQS FIFO com DLQ após 5 tentativas). O desenho livre continua permitido: o que não for componente vira anotação.
3. **Testes "como se fosse produção".** Ao submeter, o desenho é compilado num grafo executável e passa por um pipeline: validação estrutural → regras de arquitetura → testes funcionais dos fluxos → carga (baseline, pico, spike) → caos (queda de instância, AZ ou região; dependência lenta) → integridade de dados (duplicidade, perda, ordenação) → custo mensal.
4. **Nota e feedback.** Nota de 0 a 100 por dimensão, rank (S a D), testes aprovados e reprovados com números, e pontos de melhoria clicáveis que destacam o elemento no canvas.
5. **Progresso.** XP, níveis, radar de competências, conquistas, trilhas do mercado financeiro (PIX, ledger, cartões, mercado de capitais, Open Finance…) e um recomendador que escolhe o próximo desafio pelas suas competências mais fracas.

### Três ideias que sustentam o projeto

1. **Um diagrama não roda — então o app simula.** Para testar o desenho "como se tivesse uso real" sem provisionar nada, um **motor de simulação determinístico** (teoria de filas + simulação de eventos discretos) executa o grafo do seu desenho sobre um **catálogo calibrado** de serviços de nuvem: limites, latências, autoscaling, modos de falha e preços.
2. **A nota é 100% determinística.** Mesmo desenho, mesma nota, sempre. Quando há IA ligada, ela escreve narrativa e perguntas de follow-up, mas nunca altera a nota.
3. **Custo zero.** O app roda na sua máquina (processo .NET único + SQLite). O conteúdo que pede IA de alta qualidade — desafios, soluções de referência, dicas, explicações das regras — é criado em **tempo de autoria com o Claude Code do seu plano Pro**, direto no repositório, e validado pelo motor. Em tempo de execução, a IA é opcional: nenhuma (padrão), modelo local via Ollama (gratuito) ou Claude API (paga, opt-in).

> **Por que o plano Pro não é a IA "por trás" do app:** a documentação da Anthropic determina que aplicações que usam o Claude — inclusive via Agent SDK — autentiquem com chave de API do Claude Console, cobrada à parte, e não permite rotear requisições de aplicações pelas credenciais dos planos Free, Pro ou Max. O plano Pro serve para o uso normal do claude.ai e do Claude Code, e é exatamente assim que esta spec o usa. Detalhes e fonte em [06 — IA](06-ia.md).

## Decisões principais

| Tema | Proposta | Principal alternativa | Por quê |
|---|---|---|---|
| Backend | .NET 10 LTS, ASP.NET Core, monólito modular em **processo único** (API + worker em background) | API e worker separados; microsserviços | Um usuário, uma máquina, um desenvolvedor; fronteiras de módulo permitem separar depois |
| Frontend | Angular + TypeScript, servido pelo próprio app .NET; o Excalidraw roda como "ilha" React dentro de um componente Angular | React no app inteiro; Blazor | Sua escolha; a ilha React fica isolada atrás de um adaptador |
| Canvas | Excalidraw (MIT) + camada semântica via `customData` | React Flow (editor de grafo) | Preserva o desenho livre e ainda gera um grafo tipado e testável |
| Avaliação | Simulação determinística + regras + revisão gerada a partir de textos curados | Só LLM; infraestrutura real | Reprodutível, rápido, offline e gratuito |
| Modelo do desenho | Grafo intermediário (IR) agnóstico de provedor, baseado em arquétipos; catálogo como dados | Um motor por provedor | Novo serviço ou provedor = um arquivo YAML |
| IA de autoria | Claude Code (incluído no Pro) cria e valida o conteúdo no repositório | Gerar tudo em tempo de execução | Melhor qualidade, uso permitido do plano, zero custo extra |
| IA em execução | Plugável: `ollama` com modelo de 3–4B na sua GPU (o padrão na sua máquina) · `none` (sempre disponível) · `anthropic` (Claude API; futuro, opt-in com teto) | Plano Pro como backend | O plano Pro não pode ser backend; o app não depende de IA para funcionar |
| Banco | SQLite + EF Core 10 (um arquivo) | PostgreSQL | Zero instalação; migra para PostgreSQL se um dia for para a nuvem |
| Jobs | Fila em processo (`Channel<T>`) persistida no SQLite | Hangfire, RabbitMQ | Sem dependências |
| Execução | Local, só no desktop: executável único que abre o navegador | Nuvem com free tier; Cloudflare | Custo zero absoluto, sem domínio nem túnel; o backend .NET não roda no plano gratuito do Cloudflare |
| Futuro | Versão 100% estática no Cloudflare Pages com o motor C# compilado para WebAssembly | — | Hospedagem gratuita sem servidor |

## Documentos

| # | Documento | Conteúdo |
|---|---|---|
| 01 | [Visão e produto](01-visao-e-produto.md) | Persona, objetivos, escopo, jornada, botão de provedor, modos de jogo, telas |
| 02 | [Requisitos](02-requisitos.md) | Requisitos funcionais e não funcionais com IDs, critérios de aceite do MVP |
| 03 | [Arquitetura](03-arquitetura.md) | Contexto, processo, módulos, stack, execução/hospedagem, ADRs |
| 04 | [Canvas, catálogo e IR](04-canvas-catalogo-e-ir.md) | Camada semântica sobre o Excalidraw, catálogo de componentes, grafo intermediário, fluxos |
| 05 | [Motor de avaliação](05-motor-de-avaliacao.md) | Pipeline, regras, testes funcionais, simulação, cenários, custo, nota, exemplo ponta a ponta |
| 06 | [IA](06-ia.md) | O que o plano Pro permite, autoria com Claude Code, modos de IA, guardrails, evals |
| 07 | [Gamificação](07-gamificacao.md) | XP, níveis, competências, conquistas, trilhas, modos, recomendador |
| 08 | [Dados e API](08-dados-e-api.md) | Modelo de dados, máquinas de estado, endpoints, eventos em tempo real |
| 09 | [Qualidade, segurança e operação](09-qualidade-seguranca-operacao.md) | Testes, CI, observabilidade, segurança, privacidade, backup |
| 10 | [Roadmap e riscos](10-roadmap-e-riscos.md) | Fases, spikes, backlog do MVP, riscos |
| 11 | [Estado da implementação](11-estado-da-implementacao.md) | O que já existe, desvios em relação à spec e o que falta |

## Glossário

| Termo | Significado |
|---|---|
| **Desafio** | Problema de System Design com partes públicas (enunciado, casos de uso, SLOs, restrições) e ocultas (cenários ocultos, solução de referência) |
| **Semente** | Desafio curado em YAML no repositório, com faixas de variação; a base de todo desafio apresentado |
| **Tentativa** | Uma resolução de um desafio num provedor: rascunho, fluxos e avaliações |
| **Cena** | O JSON do Excalidraw, ou seja, o desenho |
| **IR (Architecture Graph)** | Grafo tipado e agnóstico de provedor compilado a partir da cena; é o que o motor testa |
| **Arquétipo** | Categoria abstrata de componente (fila, stream, banco relacional, função serverless…) que define o modelo de simulação |
| **Catálogo** | Arquivos versionados que descrevem cada serviço de nuvem: arquétipo, configuração, limites, latências, falhas e preços |
| **Fluxo** | Sequência de passos (arestas) que uma requisição de um caso de uso percorre |
| **Cenário** | Condição de teste (carga, caos ou integridade) com SLOs associados; pode ser público ou oculto |
| **Avaliação** | Execução do pipeline sobre um snapshot da tentativa: achados, resultados de cenários, custo, revisão e nota |
| **Manifesto** | Versões e sementes que tornam uma avaliação reprodutível |

## Decisões confirmadas

| Data | Tema | Decisão | Onde está na spec |
|---|---|---|---|
| 15/11/2026 | Custo | Nenhum custo extra; o plano Pro não pode ser o backend de IA | [06 — IA](06-ia.md) |
| 15/11/2026 | Frontend (Q2) | Angular | [ADR-002](03-arquitetura.md#adr-002--angular-com-o-excalidraw-como-ilha-react) e [integração do canvas](04-canvas-catalogo-e-ir.md#integração-com-o-angular) |
| 15/11/2026 | Hardware (Q3) | 16 GB de RAM, RTX 3050 Laptop com 4 GB de VRAM | IA local com modelo de 3–4B inteiro na GPU ([06](06-ia.md#ollama-local-grátis)) |
| 15/11/2026 | Acesso (Q4) | Só no desktop, por enquanto | Sem túnel, domínio nem login ([ADR-009](03-arquitetura.md#adr-009--local-first-só-no-desktop)) |
| 15/11/2026 | IA em execução (Q6) | Ollama por enquanto; Claude API e integração com o Claude Desktop via MCP ficam para o futuro. Validação, testes e nota nunca usam IA | [06 — IA](06-ia.md#camada-3--execução-com-ia-opcional) |

## Perguntas em aberto

Estas decisões mudam escopo ou experiência e são suas. Enquanto não houver resposta, vale a proposta indicada.

| # | Pergunta | Proposta atual |
|---|---|---|
| Q1 | O app é só para você ou terá outros usuários (amigos, colegas, ranking)? | Só você. Outros usuários reabririam a questão de custo (hospedagem e IA) |
| Q5 | MVP com as três nuvens ou começar por uma? | As três + Genérico, com catálogo enxuto (~15 serviços cada): o motor trabalha por arquétipo, então cada provedor é só dados |
| Q7 | Manter no roadmap o "Sandbox real" (emuladores locais + k6) e a versão estática no Cloudflare Pages? | Sim, ambos opcionais (Fases 4 e 5) |
| Q8 | Idioma do app | pt-BR. Inglês como opção na Fase 3, útil para entrevistas em inglês |
