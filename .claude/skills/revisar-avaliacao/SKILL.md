---
name: revisar-avaliacao
description: Escreve uma revisão aprofundada, em markdown, de uma avaliação do Cloud Architect Lab a partir do export (challenge.json, ir.json, evaluation.json) — uma segunda opinião de mentor sobre o desenho, além da revisão determinística do app. Use quando o usuário pedir para revisar uma tentativa, comentar a nota ou discutir o desenho exportado (pasta exports/<id>/).
---

# Revisar uma avaliação

> Comandos do CLI: `./lab.sh <args>` (Linux, macOS, Git Bash) ou `lab <args>` no Windows (`lab.cmd`), na raiz do repositório.

O app dá a nota e a revisão determinística; esta skill é a conversa de mentor que vem depois. **A nota não muda**:
ela é do motor, e a revisão aprofundada explica, aprofunda e propõe próximos passos.

## Pegar o export

- Pelo app: no resultado de uma submissão, o export fica em `GET /api/attempts/<id>/export`; salve em
  `exports/<id>/` como `challenge.json`, `ir.json` e `evaluation.json`.
- Pelo CLI, para uma referência ou uma cena salva:
  `./lab.sh evaluate <semente> --scene cena.json --difficulty senior --out exports/` (gera os mesmos três arquivos).

`exports/` está no `.gitignore`: os desenhos do usuário não vão para o repositório.

## O que ler

| Arquivo | Use para |
|---|---|
| `challenge.json` | Requisitos: casos de uso, SLOs, cenários (inclusive os ocultos), restrições, orçamento, referência |
| `ir.json` | O desenho compilado: nós (arquétipo, configuração, zonas), conexões (timeouts, retries, gatilhos) e fluxos |
| `evaluation.json` | `score` (dimensões, checks por cenário, teto), `findings`, `simulation` (métricas e gargalos por cenário), `cost`, `review` |

## Como escrever

1. Comece pelo que decidiu a nota: checks reprovados (`score.checks`), teto aplicado (`score.cap`) e as melhorias de
   maior impacto (`review.improvements`, já ordenadas).
2. Para cada ponto, ligue **evidência → causa → correção**, com os números da simulação (p99, taxa de sucesso,
   duplicidades, utilização, custo) e os ids dos elementos do desenho. Cite só números que estão no export.
3. Compare com a referência (`challenge.reference` e `review.reference`): padrões que faltaram, componentes a mais e
   a diferença de custo. Explique o trade-off, não só "faça igual".
4. Feche com perguntas de follow-up de entrevista e 2 ou 3 tópicos de estudo com links oficiais (os de
   `review.study` servem de ponto de partida).

Salve em `exports/<id>/revisao.md` e mostre um resumo ao usuário. Não altere arquivos de conteúdo nem de código
nesta skill.
