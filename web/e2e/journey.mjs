// Teste ponta a ponta no modo sem IA (spec 09): desafio → desenho (cena injetada) → validar → submeter → resultado → XP.
// Uso: com o app rodando (dotnet run --project src/Lab.App) e o `lab` compilado:
//   BASE_URL=http://localhost:5080 LAB_CLI="dotnet ../src/Lab.Cli/bin/Debug/net10.0/lab.dll" npm run e2e
import assert from 'node:assert/strict';
import { execSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { chromium } from 'playwright';

const base = process.env.BASE_URL ?? 'http://localhost:5080';
const lab = process.env.LAB_CLI ?? 'dotnet ../src/Lab.Cli/bin/Debug/net10.0/lab.dll';
const shots = process.env.E2E_SCREENSHOTS ?? 'e2e/screenshots';
const headers = { 'Content-Type': 'application/json', 'X-Lab-Client': 'e2e' };
mkdirSync(shots, { recursive: true });

async function api(path, init) {
  const response = await fetch(base + path, init);
  if (!response.ok) throw new Error(`${init?.method ?? 'GET'} ${path}: ${response.status} ${await response.text()}`);
  return response.status === 204 ? null : response.json();
}

/** Abre um desafio pela interface e injeta a solução de referência como desenho (o "usuário" dos testes). */
async function openWithReference(page, seedTitle, seedId, difficulty) {
  await page.goto(base + '/');
  await page.selectOption('select[aria-label="Dificuldade"]', difficulty);
  await page.locator('.card', { hasText: seedTitle }).locator('button.primary').click();
  await page.waitForURL(/desafios\/[0-9a-f-]+/);
  const challengeId = page.url().split('/').pop();
  await page.waitForSelector('.excalidraw');
  await page.waitForTimeout(2000);

  const challenge = await api(`/api/challenges/${challengeId}`);
  const attempt = (await api(`/api/challenges/${challengeId}/attempts`)).at(-1);
  const vars = Object.entries(challenge.variables).map(([k, v]) => `--var=${k}=${v}`).join(' ');
  const design = JSON.parse(execSync(`${lab} challenge scene ${seedId} --difficulty ${difficulty} --provider ${challenge.provider} ${vars}`).toString());
  await api(`/api/attempts/${attempt.id}/draft`, { method: 'PUT', headers: { ...headers, 'If-Match': `"${attempt.draftVersion}"` }, body: JSON.stringify({ scene: design.scene }) });
  await api(`/api/attempts/${attempt.id}/flows`, { method: 'PUT', headers, body: JSON.stringify({ flows: design.flows }) });
  await page.reload();
  await page.waitForSelector('.excalidraw');
  await page.waitForTimeout(1500);
  return { challengeId, attemptId: attempt.id };
}

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
page.on('dialog', (d) => d.accept());
const xpBefore = (await api('/api/me/progress')).summary.totalXp;

try {
  // Jornada 1: validar e submeter → nota, XP e conquista.
  await openWithReference(page, 'Consulta de chave PIX', 'pix/consulta-dict', 'junior');
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
  await openWithReference(page, 'Envio de PIX da FinBank', 'pix/envio-pix', 'pleno');
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

  // Jornada 4: perfil mostra nível, competências e histórico.
  await page.click('a:has-text("Perfil")');
  await page.waitForSelector('.level');
  assert.ok((await page.locator('.competency').count()) > 0, 'competências atualizadas');
  assert.ok((await page.locator('table tr').count()) >= 2, 'histórico com as avaliações');
  await page.screenshot({ path: `${shots}/perfil.png` });

  assert.deepEqual(errors, [], 'sem erros no console');
  console.log(`✓ jornadas ponta a ponta ok (nota ${score}, XP ${xpBefore} → ${xpAfterSubmission})`);
} catch (error) {
  await page.screenshot({ path: `${shots}/falha.png` }).catch(() => undefined);
  console.error(error);
  process.exitCode = 1;
} finally {
  await browser.close();
}

async function waitFor(probe, done, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await probe();
    if (done(value) || Date.now() > deadline) return value;
    await new Promise((r) => setTimeout(r, 300));
  }
}
