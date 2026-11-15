# 10 — Roadmap e riscos

Durações em semanas de dedicação integral, estimativa grosseira. Em tempo parcial (~10 h/semana), multiplique por ~4. Toda fase termina com algo jogável e custo recorrente zero.

## Fase 0 — Fundação e spikes (1–2 semanas)

| Spike | O que provar | Critério de saída |
|---|---|---|
| **S1** Canvas semântico | Excalidraw em React com paleta (5 serviços AWS), `customData`, setas vinculadas, inspector; compilação cena → IR no backend | 6 componentes + 6 setas geram a IR correta (teste de snapshot); "Redis" desenhado à mão gera a sugestão de mapeamento |
| **S2** Motor de simulação | Núcleo de eventos discretos + testes golden + 1 cenário de carga num grafo de 5 nós | Erro < 3% contra a teoria; ≥ 2 milhões de eventos/s por núcleo; determinismo por semente. Decide entre motor próprio e SimSharp (MIT) como base |
| **S3** Fábrica de desafios | Schema da semente + `lab challenge validate` + skill `novo-desafio`, usando o Claude Code | 3 sementes criadas com o Claude Code passam no validador, com a referência rodando no motor do S2 |
| **S4** IA local | Ollama com 2–3 modelos candidatos no seu hardware: delta de personalização e entrevistador | ≥ 80% de deltas válidos de primeira; primeiro token ≤ 5 s. Se nenhum modelo passar, o MVP sai com `none` e ponte manual |

Também na Fase 0: solução .NET, estrutura de módulos, testes de arquitetura, CI e o esqueleto do frontend.

## Fase 1 — MVP: o loop completo, custo zero (6–8 semanas)

- Biblioteca com ≥ 12 sementes validadas, busca guiada e variação procedural.
- Tela de novo desafio com seletor de provedor; modo `auto` (Ollama se disponível, senão `none`).
- Canvas com paleta de AWS, Azure, GCP e Genérico (~15 serviços cada), inspector, fronteiras de região e AZ, mapeamento assistido, autosave, validação ao vivo.
- Fluxos: editor + sugestão.
- Pipeline: compilação, as ~28 regras de [05](05-motor-de-avaliacao.md#etapa-2--regras), testes funcionais, carga (baseline, pico, spike), caos (instâncias, AZ, dependência), integridade (duplicidade, retry), custo.
- Revisão determinística; nota por dimensão; resultado com gráficos e pontos de melhoria clicáveis; solução de referência.
- Entrevistador com esclarecimentos e dicas curados (chat livre com Ollama, se disponível).
- XP, níveis, radar de competências, 10 conquistas.
- `lab export` + skill `revisar-avaliacao`.
- Executável único para Windows, macOS e Linux; backup automático.

### Backlog do MVP (em ordem)

1. Abro o app pelo navegador em `localhost`, sem instalar mais nada além dele.
2. Escrevo um contexto, escolho provedor e dificuldade e recebo 3 desafios adequados.
3. Aceito, peço outro ou ajusto o desafio.
4. Abro o workspace e vejo a paleta do provedor.
5. Insiro componentes tipados e os configuro no inspector.
6. Conecto componentes; a interação é inferida e editável.
7. Desenho região e AZ como frames.
8. Formas desenhadas à mão com rótulo recebem sugestão de mapeamento.
9. Meu trabalho é salvo automaticamente e sobrevive a um fechamento acidental.
10. Vejo problemas ao vivo enquanto desenho.
11. Defino os fluxos de cada caso de uso, ou aceito a sugestão.
12. Consulto esclarecimentos e peço dicas (pagando XP).
13. Simulo o rascunho nos cenários públicos.
14. Submeto e acompanho o progresso das etapas.
15. Vejo nota, dimensões, testes, gráficos e custo.
16. Clico num ponto de melhoria e o elemento é destacado.
17. Vejo a solução de referência.
18. Ganho XP uma única vez, subo de nível e desbloqueio conquistas.
19. Vejo meu radar de competências e o histórico.
20. Faço uma nova tentativa e vejo a melhoria.
21. Exporto uma tentativa para uma revisão aprofundada com o Claude Code.

## Fase 2 — Profundidade (4–6 semanas)

- Replay da simulação no canvas; comparação entre tentativas; diff com a referência.
- Cenários: soak, queda de região, crash do consumidor, crash entre gravar e publicar, ordenação, leitura obsoleta.
- Capacidade estimada ao vivo (modelo analítico).
- Trilhas, streak, recomendador, 25+ conquistas.
- Fronteiras de rede e regras SEC completas.
- Ponte manual com o claude.ai.
- Adaptador Claude API (opt-in, teto obrigatório) — implementado e desligado por padrão.
- Modo remoto com Cloudflare Tunnel + Access.
- Testes do motor em WebAssembly no CI.

## Fase 3 — Modos (3–4 semanas)

- Entrevista (cronômetro, follow-ups, competência Comunicação).
- Plantão (arquiteturas quebradas).
- Desafio diário.
- Flashcards com repetição espaçada a partir dos pontos de melhoria.
- Interface em inglês (se decidido).

## Fase 4 — Sandbox real local (opcional, 4+ semanas)

IR → `docker-compose` com emuladores; carga com k6 ou NBomber; falhas com Toxiproxy; métricas OpenTelemetry convertidas para o mesmo schema e para a mesma nota.

## Fase 5 — Versão estática no Cloudflare Pages (opcional, 3–4 semanas)

Motor compilado para WebAssembly (AOT), conteúdo embutido, dados no IndexedDB, exportação/importação; hospedagem gratuita em `*.pages.dev`.

## Riscos

| # | Risco | Prob. | Impacto | Mitigação |
|---|---|---|---|---|
| R1 | Simulação pouco fiel gera falsa confiança | Média | Alto | Premissas exibidas no resultado; catálogo com fonte e data; testes golden; relatório de sensibilidade; rótulo "modelo educacional" |
| R2 | Motor de simulação vira um buraco de tempo | Alta | Alto | Spike S2 com critérios de saída; MVP com subconjunto de arquétipos; modelo analítico como atalho |
| R3 | Escopo grande para uma pessoa | Alta | Alto | Fases jogáveis; o MVP é o loop completo com profundidade mínima |
| R4 | Sementes impossíveis ou triviais | Média | Alto | Validador roda a referência nos cantos da variação; faixas por dificuldade; revisão humana do diff |
| R5 | Esforço de autoria da biblioteca | Média | Médio | Fábrica de desafios com Claude Code + validador; começar com 12 sementes e crescer por trilha |
| R6 | Modelo local fraco ou hardware insuficiente | Média | Médio | O modo `none` cobre todo o loop; delta pequeno em vez de documento inteiro; evals no S4; ponte manual com o claude.ai |
| R7 | Fricção de tipar componentes tira a liberdade do desenho | Média | Médio | Mapeamento em 1 clique; anotações livres continuam valendo; problemas explicam o que falta |
| R8 | Excalidraw muda API ou formato | Baixa | Médio | Versão fixada; adaptador; testes de snapshot |
| R9 | Uso indevido da assinatura (automatizar o plano Pro no backend) | Baixa | Alto | Proibido pela arquitetura (ADR-007): o app nunca chama Claude Code nem credenciais do plano; Claude só via chave de API, opt-in |
| R10 | Custo inesperado | Baixa | Médio | Nada pago no caminho padrão; Claude API desligada e com teto; nenhuma nuvem com cartão de crédito |
| R11 | Vazamento de dados do empregador | Baixa | Alto | Modo local por padrão; aviso; mascaramento quando algo sai da máquina |
| R12 | Termos de uso de ícones e marcas | Baixa | Baixo | Uso pessoal; revisar os termos antes de publicar; ícones genéricos como alternativa |
