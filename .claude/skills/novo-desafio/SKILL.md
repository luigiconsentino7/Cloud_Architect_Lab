---
name: novo-desafio
description: Cria ou corrige uma semente de desafio de System Design do mercado financeiro em content/challenges/<trilha>/*.yaml e a valida com o CLI lab até passar. Use quando pedirem um desafio novo (ex.: "crie uma semente da trilha Cartões, nível Sênior, sobre autorização com stand-in"), uma pegadinha nova, ou quando `lab challenge validate` reprovar uma semente.
---

# Novo desafio (semente)

> Comandos do CLI: `./lab.sh <args>` (Linux, macOS, Git Bash) ou `lab <args>` no Windows (`lab.cmd`), na raiz do repositório.

Uma semente é o desafio curado de onde o app instancia variações: enunciado, casos de uso, workload, plano de testes,
restrições, dicas, esclarecimentos e a **solução de referência**, que o validador simula e que precisa tirar 85 ou mais
nos cantos do espaço de variação. Formato de referência: `content/challenges/pix/consulta-dict.yaml` (a mais curta)
e `content/challenges/pix/envio-pix.yaml` (a mais completa, com pegadinhas e fluxo assíncrono).

## Antes de escrever

1. Leia uma ou duas sementes da mesma trilha (`content/challenges/<trilha>/`) para manter o tom e a escala dos números.
2. Confira os arquétipos e componentes disponíveis em `content/catalog/archetypes.yaml` e `content/catalog/<provedor>.yaml`.
   A referência usa arquétipos (`archetype: data.cache`), não serviços de um provedor; o instanciador troca pelo padrão
   de cada nuvem. Use `providers: { azure: { component: azure.cosmosdb } }` num nó só quando o padrão não servir.
3. Rode `./lab.sh challenge list` para ver ids e trilhas existentes (não repita um id).

## Guia de estilo

- Português do Brasil, frases curtas, voz de quem trabalha no mercado financeiro: termos do domínio (SPI, DICT, STR,
  liquidação, partidas dobradas, stand-in, book de ofertas) usados corretamente, sem explicar o óbvio.
- Empresas e pessoas **fictícias** (ex.: FinBank, Carteira Ágil). Nada de dados reais de clientes ou do empregador.
- Números plausíveis e coerentes entre si: volumes por segundo, picos (Black Friday, quinto dia útil, abertura do
  pregão), latências de sistemas externos, orçamento em US$ por mês.
- Os números do enunciado vêm das variáveis via template (`{{ baselineRps | n0 }}`), nunca digitados à mão.
- Cada dica revela mais que a anterior (nível 1 pergunta, nível 3 quase entrega o padrão). Esclarecimentos respondem
  dúvidas de requisito, não de solução.

## Estrutura (campos principais)

| Campo | O que é |
|---|---|
| `id`, `version`, `title`, `summary`, `track`, `domain`, `tags`, `difficulties` | Identidade; `id` = `<trilha>/<slug>` |
| `variables.<nome>` | `description`, `default`, `step` e `ranges` por dificuldade (`junior`, `pleno`, `senior`, `staff`) |
| `twists[]` | Pegadinhas: `id`, `title`, `description`, `minDifficulty` e `ops` (`merge`, `append`, `set`; caminhos aceitam seletor `[id]`, ex.: `testPlan.slos[SLO-2].scenarios`) |
| `narrative` | Enunciado com templates |
| `useCases[]` | `id` (UC-n), `entryClient`, `monetary`, `requiresOrdering`, `requiredCapabilities[]`, `negativePaths[]` |
| `externalSystems[]` | `id` (`external:<nome>`), latência p50/p99, disponibilidade, `rateLimitRps`, `idempotent`, `sideEffect` |
| `workload` | `baselineRps`, `mix` por caso de uso, `cacheability`, `hotKeys`, `dailyPattern`, `storedGb` |
| `testPlan` | `scenarios[]` (load, chaos, integrity; `visibility: public/hidden`) e `slos[]` (métrica, `max`/`min`, cenários) |
| `constraints` | `budgetUsdMonth`, residência no Brasil, compliance, criptografia, auditoria, RTO/RPO |
| `competencies`, `hints[]` (3 níveis), `clarifications[]` (pergunta, palavras-chave, resposta) | Gamificação e entrevistador |
| `reference` | `rationale`, `nodes[]` (`archetype` ou `component`; `external` para sistemas externos), `edges[]` (com `config`: timeout, retries, circuit breaker, trigger, ops) e `flows` por caso de uso |

**Templates:** `{{ expressão }}` com `+ - * /`, `ceil`, `floor`, `round`, `abs`, `min`, `max` e as variáveis; formatos
`| n0`, `| n1`, `| n2`, `| pct`. Unidades em texto são normalizadas: `120s`, `700ms`, `99.95%`.

**Dimensione a referência pelas variáveis** (ex.: `instances.min: "{{ max(3, ceil(baselineRps / 180)) }}"`): ela precisa
passar no menor e no maior valor de cada faixa, com e sem cada pegadinha.

## Validar e iterar

```bash
./lab.sh challenge validate content/challenges/<trilha>/<arquivo>.yaml --quick   # rápido: AWS, variação padrão
./lab.sh challenge validate content/challenges/<trilha>/<arquivo>.yaml           # completo: 3 nuvens × cantos × pegadinhas
./lab.sh challenge show <id> --difficulty senior --provider gcp                  # o desafio instanciado
./lab.sh evaluate <id> --reference --difficulty senior                           # avaliação completa da referência
```

Leia os erros do validador e corrija a causa, não o sintoma:

- **Referência abaixo de 85:** veja o achado e o gargalo na saída (`evaluate --reference`). Capacidade (instâncias,
  partições, shards), timeouts em cascata menores que o SLO, idempotência e ordenação são as causas mais comuns.
  Não afrouxe o SLO para a referência passar, a não ser que o SLO esteja irreal para o domínio.
- **SLO impossível:** latência do sistema externo maior que o SLO, ou timeout igual ao p99 do externo.
- **Orçamento estourado:** recalibre `budgetUsdMonth` a partir do custo da referência (tipicamente referência × 1,3–1,5).
- **Template inválido ou variável fora da faixa:** confira nomes e `ranges` de cada dificuldade.

Termine com o validador completo passando e mostre ao usuário: o resumo do desafio, as faixas, as pegadinhas e a
tabela de notas da referência por nuvem e dificuldade. O commit é do usuário, depois de revisar o diff.
