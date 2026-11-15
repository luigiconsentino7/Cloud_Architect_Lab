# 07 — Gamificação

## Princípios

- **XP mede aprendizado, não tempo de tela.** Ganha-se XP por nota e por melhoria, nunca por clicar ou por ficar no app.
- **Desafio na medida certa.** O recomendador mira uma nota esperada entre 70 e 80, a zona em que se aprende mais.
- **Progresso visível.** Radar de competências, histórico e "melhor tentativa" mostram a evolução.
- **Sem dark patterns.** Nada de notificação insistente, nada de perder progresso por um dia sem estudar (o streak tem congelamento).

## XP

```text
XP_base   = { Júnior: 100, Pleno: 200, Sênior: 350, Staff: 500 }[dificuldade]

XP_bruto  = XP_base × (nota / 100) ^ 1,5 × multiplicadores
  primeira tentativa do desafio: × 1,2
  modo Entrevista: × 1,3        modo Plantão: × 1,2
  rank S: × 1,25                rank A: × 1,1

dicas     = −10% (nível 1), −20% (nível 2), −35% (nível 3), acumulativo; mínimo de 30% do XP_bruto

XP_creditado = max(0, XP_bruto(tentativa atual) − maior XP_bruto já creditado neste desafio)
```

- O expoente 1,5 recompensa mais as notas altas.
- Só a melhoria rende XP numa nova tentativa, o que impede "farmar" o mesmo desafio.
- Exemplos: Sênior, nota 78, primeira tentativa, sem dicas → 350 × 0,689 × 1,2 = **289 XP**. No exemplo do [motor](05-motor-de-avaliacao.md#exemplo-ponta-a-ponta-ilustrativo): nota 69 de primeira → 241 XP; depois nota 88 (rank A) → XP_bruto 318, creditado 318 − 241 = **77 XP**.

## Níveis e títulos

| Nível | Título | XP acumulado |
|---|---|---|
| 1–2 | Estagiário(a) de Nuvem | 0 · 150 |
| 3–5 | Júnior I–III | 400 · 800 · 1.300 |
| 6–8 | Pleno I–III | 2.000 · 2.900 · 4.000 |
| 9–11 | Sênior I–III | 5.400 · 7.100 · 9.100 |
| 12–14 | Staff I–III | 11.500 · 14.300 · 17.500 |
| 15–17 | Principal I–III | 21.200 · 25.400 · 30.000 |
| 18+ | Arquiteto(a) Distinto(a) | 35.000, depois +6.000 por nível |

Com ~290 XP por desafio Sênior e 3 desafios por semana, Sênior I chega em cerca de 6 semanas, Staff em 3 meses e Principal em 6 meses.

## Competências

| Competência | Alimentada por |
|---|---|
| Escalabilidade | Performance e escala; regras ARQ-008, ARQ-009, ARQ-010 |
| Resiliência | Cenários de caos; regras ARQ-001 a ARQ-005 |
| Consistência de dados | Cenários de integridade; regras DAT |
| Mensageria e eventos | Uso de filas e streams; DAT-004, ARQ-004, ARQ-008 |
| Baixa latência | SLOs de p99 em desafios marcados com foco em latência |
| Segurança e compliance | Regras SEC |
| Custo (FinOps) | Dimensão Custo |
| Observabilidade e operação | Regras OPS |
| Comunicação (F3, requer IA) | Respostas às perguntas de follow-up do modo Entrevista |

**Rating por competência (0–100):**

```text
desempenho = nota_da_dimensão × fator_dificuldade        # Júnior 0,7 · Pleno 0,85 · Sênior 1,0 · Staff 1,15 (máx. 100)
k          = max(0,15; 0,5 / √n)                          # n = avaliações que tocaram a competência
rating     = rating + k × (desempenho − rating)
```

O radar mostra o rating e uma barra de confiança que cresce com `n`. O rating começa rápido e estabiliza com o tempo.

## Rank por desafio

S ≥ 90 · A ≥ 80 · B ≥ 70 · C ≥ 60 · D < 60, com cor e animação próprias na tela de resultado.

## Conquistas

| Conquista | Critério |
|---|---|
| Primeiro Deploy | Concluir a primeira avaliação |
| Zero Duplicidade | 0 efeitos duplicados num cenário de integridade de UC monetário |
| Idempotência Mestre | 5 desafios monetários seguidos sem violação de integridade |
| Outbox na Veia | Usar outbox ou CDC corretamente em 3 desafios |
| Sobrevivente do Dia de Pagamento | Passar em todos os SLOs do cenário de pico 5x |
| Black Friday | Passar num spike de 10x ou mais |
| AZ? Que AZ? | Nenhum SLO violado na queda de AZ |
| Região Caiu, Eu Não (F2) | Passar no cenário de queda de região |
| Cinco Noves | Sucesso ≥ 99,999% em todos os cenários de um desafio |
| Mão de Vaca Consciente | Custo ≤ 70% do orçamento com rank ≥ A |
| Poliglota de Nuvem | Mesmo desafio com rank ≥ B em AWS, Azure e GCP |
| Sem Rodinhas | Rank ≥ A sem dicas num desafio Sênior ou Staff |
| De Primeira | Rank S na primeira tentativa |
| Persistente | Melhorar a nota em 30 pontos ou mais entre tentativas |
| Fila Zerada | Backlog volta a zero em até 60 s depois do spike |
| Latência de Pregão | p99 < 10 ms num desafio de mercado de capitais |
| Chefão da Trilha (F2) | Vencer o desafio final de uma trilha |
| Maratonista (F2) | Streak de 30 dias |
| Plantonista (F3) | Resolver 5 desafios do modo Plantão |
| Entrevista Aprovada (F3) | Rank ≥ A no modo Entrevista |
| Generalista | Rating ≥ 60 em todas as competências |
| Especialista | Rating ≥ 85 numa competência |

Conquistas bloqueadas aparecem com uma pista do critério. As 10 do MVP: Primeiro Deploy, Zero Duplicidade, Outbox na Veia, Sobrevivente do Dia de Pagamento, Black Friday, AZ? Que AZ?, Mão de Vaca Consciente, Poliglota de Nuvem, Sem Rodinhas, Persistente.

## Streak (F2)

- Um dia conta se você concluir ao menos uma avaliação (ou, na F3, uma revisão de flashcards).
- A cada 7 dias seguidos você ganha um congelamento (máximo de 2 acumulados), gasto automaticamente num dia sem estudo.
- Fuso: America/Sao_Paulo.

## Trilhas do mercado financeiro (F2)

| Trilha | Desafios (exemplos) | Chefão |
|---|---|---|
| PIX e pagamentos instantâneos | Consulta de chave (DICT) com cache · Envio com idempotência · Recebimento e notificação · Devolução (MED) · Conciliação diária | PIX na Black Friday: 30 mil TPS, SPI degradado e conta quente de varejista |
| Core bancário e ledger | Ledger de partidas dobradas · Extrato com CQRS · Saldo de conta quente · Fechamento diário | Migração de ledger sem downtime |
| Cartões | Autorização com stand-in · Antifraude em tempo real · Liquidação com bandeiras · Tokenização (PCI) | Autorizador multi-região com p99 < 150 ms |
| Mercado de capitais | Distribuição de cotações · Gestão de ordens (OMS) · Matching engine · Histórico de market data | Abertura do pregão: 10x mensagens em 1 minuto |
| Open Finance | APIs de consentimento · Compartilhamento com FAPI e mTLS · Rate limiting por participante | Iniciação de pagamento ponta a ponta |
| Risco e antifraude | Feature store online · Scoring em streaming · Regras + modelo com fallback | Fraude detectada em < 50 ms sob ataque coordenado |
| Investimentos | Posição consolidada · Rentabilidade diária em batch · Aporte programado | Fechamento de fundos em D+0 |

A biblioteca do MVP tem ≥ 12 sementes cobrindo todas as trilhas; o restante é produzido com a fábrica de desafios ([06](06-ia.md#camada-1--autoria-com-o-claude-code-a-fábrica-de-desafios)).

## Regras dos modos (F3)

- **Entrevista:** cronômetro de 45 min; ao esgotar, o app submete sozinho. Depois da nota, 2 ou 3 perguntas de follow-up (curadas, escolhidas pelos achados). Com IA ligada, suas respostas são avaliadas por rubrica (correção, trade-offs, clareza) e alimentam a competência Comunicação — fora da nota do desenho. Sem IA, a autoavaliação usa a resposta-modelo curada.
- **Plantão:** a semente traz uma arquitetura quebrada e um incidente (ex.: "a fila acumula no dia de pagamento"). Nota = % de SLOs restabelecidos no cenário do incidente + nenhuma regressão no baseline + bônus por mudança mínima (menos componentes alterados) e por tempo.
- **Desafio diário:** a semente e a variação do dia são sorteadas de forma determinística a partir da data; sem IA, sem custo.

## Recomendador (F2)

Pontuação de cada candidato (semente × variação):

```text
score = 0,40 × fraqueza (100 − rating das competências-alvo)
      + 0,20 × novidade (domínio não praticado nos últimos 14 dias)
      + 0,25 × encaixe de dificuldade (nota esperada perto de 75)
      + 0,15 × repetição espaçada (conceitos com achados nos últimos 30 dias)
```

A Home mostra os 3 melhores, cada um com o motivo ("para treinar Resiliência").

## Ledger de XP

XP é dinheiro do jogo, então é tratado como num core bancário — praticando o que o app ensina:

- Tabela `xp_entries` só de inserção: `(id, perfil, quantidade, motivo, tipo_origem, id_origem, criado_em)`, com unicidade em `(perfil, motivo, id_origem)`. Creditar duas vezes o mesmo evento é impossível.
- Saldo, nível e streak ficam materializados em `user_progress` e podem ser recalculados a partir do ledger a qualquer momento.

## Elementos de interface

Barra de XP no topo, modal de level-up com animação, toast de conquista, radar no perfil, linha do tempo de tentativas, grade de conquistas e, no resultado, a soma de XP detalhada (base, multiplicadores, dicas, desconto da melhor tentativa anterior).
