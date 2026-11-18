# Como testar a versão de teste

Guia para testar a primeira versão (0.1.0) do Cloud Architect Lab na sua máquina: baixar, abrir, o que testar e
como relatar o que der errado.

## 1. Baixar e abrir

1. No GitHub, abra **Releases** e pegue a versão de teste mais nova (`Cloud Architect Lab 0.1.0-teste.N`).
2. Baixe o zip do seu sistema e extraia numa pasta sua (por exemplo, `Documentos\CloudArchitectLab`):
   - Windows: `cloud-architect-lab-win-x64.zip`
   - Linux: `cloud-architect-lab-linux-x64.zip`
   - macOS com Apple Silicon: `cloud-architect-lab-osx-arm64.zip`
3. Abra o `Lab.App.exe` (no Linux e no macOS, `./Lab.App` pelo terminal).

Não precisa instalar o .NET nem o Node: o executável já traz tudo.

**Aviso do Windows.** O executável não é assinado, então o Windows pode mostrar "O Windows protegeu o computador".
Clique em **Mais informações** → **Executar assim mesmo**. Isso acontece só na primeira vez de cada versão.

Ao abrir, aparece uma janela preta (é o servidor local) e o navegador abre em `http://localhost:5080`. **Deixe a
janela preta aberta enquanto usa o app**; para encerrar, feche-a. Abrir o executável de novo com o app já aberto só
abre o navegador nele.

A versão aparece no topo da tela, ao lado do nome (`v0.1.0-teste.N (abc1234)`). Cite-a quando relatar um problema.

## 2. IA local (opcional)

O app funciona inteiro sem IA: a busca guiada escolhe o desafio, o entrevistador responde com o conteúdo curado e a
nota é sempre determinística. Para testar com IA, deixe o [Ollama](https://ollama.com/download) aberto com o modelo
padrão baixado:

```powershell
ollama pull gemma3:4b
```

Com o Ollama aberto, o chat da tela inicial mostra "IA local · gemma3:4b". Se você abrir o Ollama com o app já
aberto, recarregue a página (F5).

## 3. O que testar

Faça o roteiro em ordem e anote o que estranhar: erro, tela travada, texto confuso, número que não faz sentido ou nota
que parece injusta.

1. **Escolher um desafio.** Na tela inicial, descreva no chat o que quer estudar (ex.: "trabalho num banco digital e
   o Pix fica lento no dia do salário") e use **Abrir desafio**, ou escolha um na **Biblioteca**. Teste a nuvem
   (AWS, Azure ou GCP), o nível e o modo **Treino** (com simulação de rascunho) ou **Entrevista** (sem rascunho,
   XP ×1,3).
2. **Ler o enunciado.** Na aba **Enunciado**: casos de uso, carga e SLOs, sistemas externos e restrições. Teste
   **Outro** (mesmo tema, números novos) e **Ajustar** (nuvem, nível, números e pegadinhas; com IA, também por texto,
   como "mais volume, com auditoria").
3. **Desenhar.** Na aba **Paleta**, clique nos componentes para pô-los no desenho (também há zonas, região e os
   sistemas externos do desafio), ligue-os com a seta do canvas e configure cada um no painel à direita (instâncias,
   zonas, limites). Desenhe também um retângulo à mão com um texto, como "Redis" ou "fila de pagamentos": em
   **Problemas**, **Mapear para …** o transforma em componente; textos que o app não reconhece aparecem em
   **Anotações** e, com IA, **Sugerir componentes com a IA** sugere o serviço.
4. **Fluxos.** Na aba **Fluxos**, monte o caminho de cada caso de uso: selecione uma seta no desenho e use
   **+ seta selecionada**, ou use **Sugerir pelo desenho**.
5. **Validar e simular.** Acompanhe a contagem de **Problemas** enquanto desenha (clicar num problema destaca o
   elemento no desenho); use **Validar** e, no modo Treino, **Simular** (só os cenários públicos, sem XP) antes de
   submeter.
6. **Entrevistador.** Na aba **Entrevistador**, pergunte sobre o desafio (volume, sistemas externos, o que não está
   definido). Ele não conhece a solução de referência nem os cenários ocultos.
7. **Submeter.** Use **Submeter** e veja a nota: notas por dimensão, cenários e SLOs, custo, pontos fortes, pontos
   de melhoria (clique num deles para ver o porquê, como corrigir e os trade-offs), **Ver a referência** e, com IA,
   o comentário do mentor. Depois, use **Voltar ao desenho** e **Nova tentativa** (começa da submetida), corrija um
   ponto de melhoria, submeta de novo e veja se a nota e o XP sobem. Teste também o botão **Dica**.
8. **Perfil.** Em **Perfil**: nível e XP, conquistas, radar de competências e histórico. Em **Seus dados**, use
   **Exportar backup** e depois **Importar backup** com o arquivo exportado.
9. **Fechar e abrir de novo.** Feche a janela preta, abra o executável de novo e confira que tudo continua lá.

## 4. Como relatar um problema

Mande, de preferência num lugar só:

- a versão (topo da tela);
- o que fez, o que esperava e o que aconteceu;
- um print da tela;
- o texto da janela preta, se aparecer erro nela (selecione e copie);
- se for sobre a nota de uma avaliação, o backup exportado em **Perfil → Seus dados** (permite reproduzir o desenho
  e a avaliação).

## 5. Onde ficam os dados

Os dados ficam fora da pasta do app, então trocar de versão não apaga nada:

- Windows: `%APPDATA%\CloudArchitectLab` (o banco `lab.db` e a pasta `backups`, com uma cópia por dia dos últimos 7
  dias)
- Linux: `~/.local/share/cloud-architect-lab`
- macOS: `~/Library/Application Support/CloudArchitectLab`

Para começar do zero, feche o app e apague essa pasta. Para atualizar para uma versão de teste nova, baixe o zip novo
e substitua a pasta do app.

## 6. Problemas comuns

| Sintoma | O que fazer |
|---|---|
| A janela preta mostra "O Cloud Architect Lab não conseguiu abrir" | Copie a mensagem e mande junto com o relato (seção 4); a janela espera um Enter para fechar |
| A janela preta abre e fecha sem mensagem | Abra pelo terminal na pasta do app (`.\Lab.App.exe`) para ler o que aconteceu |
| "A porta 5080 está ocupada por outro programa" | Feche o outro programa ou abra o Lab em outra porta: `.\Lab.App.exe --Lab:Port=5081` |
| O chat não mostra "IA local" | Confira se o Ollama está aberto e se o modelo foi baixado (`ollama list`) |
| A IA demora para responder | Na primeira pergunta o Ollama carrega o modelo na memória (alguns segundos); com o `gemma3:4b` numa GPU de 4 GB, cada resposta leva de 2 a 15 s |
| Quero a IA mais rápida | Feche o app, baixe o `qwen2.5:3b` (`ollama pull qwen2.5:3b`) e abra com `.\Lab.App.exe --Ai:Ollama:Model=qwen2.5:3b` (mais rápido e menos cuidadoso) |
| Quero testar sem IA mesmo com o Ollama aberto | `.\Lab.App.exe --Ai:Mode=none` |

## 7. Limites conhecidos desta versão

- Os números do catálogo (limites, latências e preços dos serviços) são ilustrativos, com link oficial e data de
  revisão por serviço: a nota é tão realista quanto eles.
- São 12 desafios em 7 trilhas (Pix, ledger, cartões, mercado de capitais, Open Finance, risco e investimentos).
- O app é só para desktop: abre em `localhost` e não tem login.
- Replay da simulação no desenho, comparação entre tentativas, modo Entrevista com cronômetro e desafio diário ficam
  para as próximas fases.
