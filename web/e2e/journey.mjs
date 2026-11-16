// Teste ponta a ponta no modo sem IA (spec 09): desafio → desenho (cena injetada) → validar → submeter → resultado → XP.
// Uso: com o app rodando (dotnet run --project src/Lab.App) e o `lab` compilado:
//   BASE_URL=http://localhost:5080 LAB_CLI="dotnet ../src/Lab.Cli/bin/Debug/net10.0/lab.dll" npm run e2e
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { api, base, draftWithShapes, headers, injectReference, shots, waitFor, watchErrors } from './helpers.mjs';

/** Abre um desafio pela interface e injeta a solução de referência como desenho (o "usuário" dos testes). */
async function openWithReference(page, seedTitle, difficulty) {
  await page.goto(base + '/');
  await page.selectOption('select[aria-label="Dificuldade"]', difficulty);
  await page.locator('.card', { hasText: seedTitle }).locator('button.primary').click();
  await page.waitForURL(/desafios\/[0-9a-f-]+/);
  const challengeId = page.url().split('/').pop();
  await page.waitForSelector('.excalidraw');
  await page.waitForTimeout(2000);
  const { attemptId } = await injectReference(page, challengeId);
  return { challengeId, attemptId };
}

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const errors = watchErrors(page);
const xpBefore = (await api('/api/me/progress')).summary.totalXp;

try {
  // Jornada 1: validar e submeter → nota, XP e conquista.
  await openWithReference(page, 'Consulta de chave PIX', 'junior');
  await page.click('button:has-text("Validar")');
  await page.waitForSelector('.problems');
  assert.equal(await page.locator('.problem.error').count(), 0, 'o desenho de referência não pode ter erro de compilação');
  assert.ok(await page.locator('.functional.passed').count() > 0, 'os testes funcionais passam');
  await page.click('button:has-text("Submeter")');
  await page.waitForURL(/avaliacoes/);
  await page.waitForSelector('.head .rank', { timeout: 180_000 });
  const score = Number((await page.locator('.score h1').innerText()).replace(/\D+/g, ''));
  assert.ok(score >= 85, `a referência tira 85 ou mais (tirou ${score})`);
  await page.screenshot({ path: `${shots}/resultado.png` });
  const progress = await waitFor(async () => (await api('/api/me/progress')).summary, (p) => p.totalXp > xpBefore);
  assert.ok(progress.totalXp > xpBefore, 'a submissão credita XP');
  const achievements = await api('/api/me/achievements');
  assert.ok(achievements.find((a) => a.code === 'primeiro-deploy')?.unlocked, 'conquista Primeiro Deploy');

  // Jornada 2: simulação de rascunho (Treino) roda só os cenários públicos e não rende XP.
  const xpAfterSubmission = progress.totalXp;
  await openWithReference(page, 'Envio de PIX da FinBank', 'pleno');
  await page.click('button:has-text("Simular")');
  await page.waitForURL(/avaliacoes/);
  await page.waitForSelector('.head .rank', { timeout: 180_000 });
  assert.match(await page.locator('.score').innerText(), /rascunho/i);
  assert.ok((await page.locator('.scenario h3:has-text("oculto")').count()) === 0, 'rascunho não mostra cenários ocultos');
  await page.waitForTimeout(1500);
  assert.equal((await api('/api/me/progress')).summary.totalXp, xpAfterSubmission, 'rascunho não rende XP');

  // Jornada 3: dica revela texto e reduz o contador.
  await page.click('a:has-text("Voltar ao desenho")');
  await page.waitForSelector('.excalidraw');
  await page.click('button:has-text("Dica")');
  await page.waitForSelector('.hint-box');
  assert.match(await page.locator('.hint-box').first().innerText(), /Dica 1/);

  // Jornada 4: chat de contexto (sem IA: busca guiada) → desafio → entrevistador com respostas curadas.
  await page.goto(base + '/');
  await page.selectOption('select[aria-label="Dificuldade"]', 'pleno');
  const before = await page.locator('lab-chat .msg').count();
  await page.fill('lab-chat textarea', 'Quero treinar envio de Pix com idempotência e retentativas');
  await page.click('lab-chat button:has-text("Enviar")');
  await waitFor(async () => page.locator('lab-chat .msg').count(), (n) => n >= before + 2);
  assert.match(await page.locator('lab-chat .source').last().innerText(), /biblioteca/i, 'sem IA, a resposta vem da biblioteca');
  await page.screenshot({ path: `${shots}/chat-contexto.png` });
  await page.locator('lab-chat .msg').last().locator('.suggestion', { hasText: 'Envio de PIX' }).locator('button').click();
  await page.waitForURL(/desafios\/[0-9a-f-]+/);
  await page.waitForSelector('.excalidraw');
  await page.click('nav.tabs button:has-text("Entrevistador")');
  await page.locator('lab-chat .chip').first().click();
  await page.waitForSelector('lab-chat .msg:not(.user) .source');
  assert.match(await page.locator('lab-chat .source').last().innerText(), /curada/i, 'sem IA, o entrevistador usa os esclarecimentos curados');
  await page.screenshot({ path: `${shots}/entrevistador.png` });

  // "Outro" e "Ajustar": desafios novos da mesma semente, na mesma tela.
  await page.click('nav.tabs button:has-text("Enunciado")');
  const firstId = page.url().split('/').pop();
  await page.click('.brief-actions button:has-text("Outro")');
  await page.waitForURL((url) => !url.pathname.endsWith(firstId));
  await page.waitForSelector('.excalidraw');
  const otherId = page.url().split('/').pop();
  await page.click('.brief-actions button:has-text("Ajustar")');
  await page.waitForSelector('lab-adjust-panel select[aria-label="Nível do desafio"]');
  await page.selectOption('lab-adjust-panel select[aria-label="Nível do desafio"]', 'senior');
  await page.click('lab-adjust-panel button:has-text("Criar desafio ajustado")');
  await page.waitForURL((url) => !url.pathname.endsWith(otherId));
  await page.waitForSelector('.excalidraw');
  assert.match(await page.locator('header.bar small').innerText(), /Sênior/, 'o desafio ajustado está no nível pedido');

  // Jornada 5: forma desenhada à mão vira componente com um clique; a validação ao vivo atualiza os problemas.
  const shapes = await draftWithShapes(['Redis']);
  await page.goto(`${base}/desafios/${shapes.challengeId}`);
  await page.waitForSelector('.excalidraw');
  await page.click('nav.tabs button:has-text("Problemas")');
  await page.waitForSelector('.map-actions button:has-text("Mapear para Amazon ElastiCache")', { timeout: 15_000 });
  await page.click('.map-actions button:has-text("Mapear para Amazon ElastiCache")');
  await waitFor(async () => page.locator('.map-actions button').count(), (n) => n === 0, 15_000);
  assert.equal(await page.locator('.problem:has-text("IR004")').count(), 0, 'a forma tipada some dos problemas sem clicar em Validar');
  const typed = (await api(`/api/challenges/${shapes.challengeId}/attempts`)).at(-1).scene.elements.find((e) => e.id === 'shape_0');
  assert.equal(typed?.customData?.catalogId, 'aws.elasticache', 'o rascunho salvo tem a forma tipada');
  await page.screenshot({ path: `${shots}/mapeamento.png` });

  // Fronteira de zona pela paleta: vira frame com customData de AZ, que o compilador lê como fronteira.
  await page.click('nav.tabs button:has-text("Paleta")');
  await page.click('.palette button.boundary:has-text("AZ b")');
  const frame = await waitFor(
    async () => (await api(`/api/challenges/${shapes.challengeId}/attempts`)).at(-1).scene.elements.find((e) => e.type === 'frame' && !e.isDeleted),
    (f) => !!f,
    15_000,
  );
  assert.equal(frame?.customData?.zone, 'b', 'o frame da AZ é salvo com o customData da fronteira');
  const compiled = await api(`/api/attempts/${(await api(`/api/challenges/${shapes.challengeId}/attempts`)).at(-1).id}/validate`, { method: 'POST', headers, body: '{}' });
  assert.ok(compiled.graph.boundaries.some((b) => b.type === 'zone' && b.zone === 'b'), 'o compilador reconhece a AZ');

  // Jornada 6: perfil mostra nível, competências e histórico.
  await page.click('a:has-text("Perfil")');
  await page.waitForSelector('.level');
  assert.ok((await page.locator('.competency').count()) > 0, 'competências atualizadas');
  assert.ok((await page.locator('table tr').count()) >= 2, 'histórico com as avaliações');
  assert.equal(await page.locator('lab-radar polygon.area').count(), 1, 'radar de competências');
  await page.screenshot({ path: `${shots}/perfil.png` });

  // Backup de ida e volta pela interface: exporta o arquivo e importa de novo (com cópia do banco antes).
  const xpBeforeBackup = (await api('/api/me/progress')).summary.totalXp;
  const [download] = await Promise.all([page.waitForEvent('download'), page.click('button:has-text("Exportar backup")')]);
  const backupFile = `${shots}/backup.json`;
  await download.saveAs(backupFile);
  await page.setInputFiles('.data input[type=file]', backupFile);
  await page.waitForSelector('.data-message:has-text("Backup importado")', { timeout: 30_000 });
  assert.equal((await api('/api/me/progress')).summary.totalXp, xpBeforeBackup, 'XP continua o mesmo depois de importar');

  assert.deepEqual(errors, [], 'sem erros no console: ' + JSON.stringify(errors).slice(0, 1500));
  console.log(`✓ jornadas ponta a ponta ok (nota ${score}, XP ${xpBefore} → ${xpAfterSubmission})`);
} catch (error) {
  await page.screenshot({ path: `${shots}/falha.png` }).catch(() => undefined);
  console.error(error);
  process.exitCode = 1;
} finally {
  await browser.close();
}
