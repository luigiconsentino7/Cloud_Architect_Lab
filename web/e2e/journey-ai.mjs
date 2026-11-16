// Teste ponta a ponta do modo com IA, contra um Ollama de mentira (fake-ollama.mjs) e o cliente real do OllamaSharp.
// Uso: suba o app apontando para o Ollama de mentira e rode:
//   Ai__Mode=ollama Ai__Ollama__Endpoint=http://127.0.0.1:11999 dotnet run --project src/Lab.App
//   BASE_URL=http://localhost:5080 npm run e2e:ai
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { startFakeOllama } from './fake-ollama.mjs';
import { api, base, draftWithShapes, injectReference, shots, waitFor, watchErrors } from './helpers.mjs';

// Pedaços espaçados o bastante para dar para ver (e testar) a resposta sendo escrita.
const ollama = await startFakeOllama({ port: Number(process.env.FAKE_OLLAMA_PORT ?? 11999), chunkDelayMs: 120 });
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const errors = watchErrors(page);

try {
  // O app guarda o status da IA por 30 s: espera ele enxergar o Ollama.
  const status = await waitFor(() => api('/api/ai/status'), (s) => s.mode === 'ollama', 45_000);
  assert.equal(status.mode, 'ollama', `o app não enxergou o Ollama de mentira: ${status.detail}`);

  // Jornada 1: contexto → desafio personalizado pela IA (delta validado e montado pelo instanciador).
  await page.goto(base + '/');
  await page.waitForSelector('lab-chat .ai.on');
  await page.selectOption('select[aria-label="Dificuldade"]', 'pleno');
  const before = await page.locator('lab-chat .msg').count(); // a conversa de contexto continua entre visitas
  await page.fill('lab-chat textarea', 'Sou dev num banco digital e quero treinar o envio de Pix por aproximação');
  await page.click('lab-chat button:has-text("Enviar")');
  await waitFor(async () => page.locator('lab-chat .msg').count(), (n) => n >= before + 2);
  await page.locator('lab-chat .msg').last().locator('.created').waitFor();
  assert.match(await page.locator('lab-chat .source').last().innerText(), /IA local/);
  await page.screenshot({ path: `${shots}/ia-contexto.png` });
  const personalizer = ollama.requests.find((r) => r.messages[0].content.startsWith('Você monta desafios'));
  assert.ok(personalizer?.format?.properties?.seedId?.enum?.length > 0, 'o personalizador manda o JSON Schema com os seedId candidatos');
  assert.equal(personalizer.options?.num_ctx, 8192, 'janela de contexto configurada');
  assert.match(personalizer.messages[1].content, /<contexto_do_usuario>/, 'o contexto vai delimitado como dado');

  await page.locator('lab-chat .msg').last().locator('a:has-text("Abrir desafio")').click();
  await page.waitForURL(/desafios\/[0-9a-f-]+/);
  const challengeId = page.url().split('/').pop();
  await page.waitForSelector('.excalidraw');
  assert.match(await page.locator('.ai-note').innerText(), /personalizado pela IA local/);

  // Jornada 2: entrevistador com resposta transmitida pelo SignalR enquanto o modelo escreve.
  await page.click('nav.tabs button:has-text("Entrevistador")');
  await page.fill('lab-chat textarea', 'Posso assumir que o SPI aceita a mesma ordem duas vezes?');
  await page.click('lab-chat button:has-text("Enviar")');
  await page.waitForSelector('lab-chat .typing');
  const partial = await waitFor(async () => page.locator('lab-chat .typing').innerText().catch(() => ''), (t) => t.startsWith('Boa pergunta'), 10_000);
  assert.ok(partial.startsWith('Boa pergunta'), 'a resposta aparece enquanto o modelo escreve (ChatDelta pelo SignalR)');
  await page.screenshot({ path: `${shots}/ia-entrevistador-escrevendo.png` });
  await page.waitForSelector('lab-chat .typing', { state: 'detached' });
  assert.match(await page.locator('lab-chat .source').last().innerText(), /IA local/);
  assert.match(await page.locator('lab-chat .msg:not(.user) .bubble').last().innerText(), /premissa explícita/);
  const interviewer = ollama.requests.find((r) => r.messages[0].content.startsWith('Você é o entrevistador'));
  assert.ok(interviewer.stream !== false, 'o entrevistador usa streaming');
  assert.match(interviewer.messages[0].content, /<desafio>/);
  assert.doesNotMatch(interviewer.messages[0].content, /reference|referência da solução/i, 'o entrevistador não recebe a referência');
  await page.screenshot({ path: `${shots}/ia-entrevistador.png` });

  // Jornada 3: anotação sem alias ganha sugestão da IA (só ids da paleta) e vira componente com um clique.
  const shapes = await draftWithShapes(['Motor de regras antifraude']);
  await page.goto(`${base}/desafios/${shapes.challengeId}`);
  await page.waitForSelector('.excalidraw');
  await page.click('nav.tabs button:has-text("Problemas")');
  await page.click('button:has-text("Sugerir componentes com a IA")', { timeout: 15_000 });
  await page.waitForSelector('.map-actions button:has-text("(IA)")', { timeout: 30_000 });
  const mapper = ollama.requests.find((r) => r.messages[0].content.startsWith('Você ajuda a tipar'));
  assert.ok(mapper?.format?.properties?.catalogId?.enum?.includes('nenhum'), 'o mapeador manda a paleta e a opção "nenhum" no schema');
  await page.click('.map-actions button:has-text("(IA)")');
  await waitFor(async () => (await api(`/api/challenges/${shapes.challengeId}/attempts`)).at(-1).scene.elements.find((e) => e.id === 'shape_0')?.customData?.kind, (k) => k === 'component', 15_000);
  const left = await waitFor(async () => page.locator('button.problem.info').count(), (n) => n === 0, 15_000);
  assert.equal(left, 0, 'a validação ao vivo tira a forma das anotações depois do mapeamento');
  await page.screenshot({ path: `${shots}/ia-mapeamento.png` });

  // Jornada 4: submissão → nota determinística → narrativa da IA conferida contra a revisão.
  await page.goto(`${base}/desafios/${challengeId}`);
  await page.waitForSelector('.excalidraw');
  await injectReference(page, challengeId);
  await page.click('button:has-text("Submeter")');
  await page.waitForURL(/avaliacoes/);
  await page.waitForSelector('.head .rank', { timeout: 180_000 });
  const score = Number((await page.locator('.score h1').innerText()).replace(/\D+/g, ''));
  await page.waitForSelector('.narrative h2', { timeout: 60_000 });
  assert.match(await page.locator('.narrative').innerText(), new RegExp(`Nota ${score}:`), 'a narrativa cita a nota que o motor deu');
  await page.locator('.narrative').scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${shots}/ia-narrativa.png` });
  const narrator = ollama.requests.find((r) => r.messages[0].content.startsWith('Você é um mentor'));
  assert.ok(narrator?.format?.properties?.paragraphs, 'o narrador manda o JSON Schema da narrativa');

  assert.deepEqual(errors, [], 'sem erros no console: ' + JSON.stringify(errors).slice(0, 1500));
  console.log(`✓ jornadas com IA ok (nota ${score}, ${ollama.requests.length} chamadas ao Ollama de mentira)`);
} catch (error) {
  await page.screenshot({ path: `${shots}/falha-ia.png` }).catch(() => undefined);
  console.error(error);
  process.exitCode = 1;
} finally {
  await browser.close();
  await ollama.close();
}
