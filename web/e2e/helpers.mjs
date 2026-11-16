// Utilitários das jornadas E2E: API do app, injeção da solução de referência como desenho e espera com timeout.
import { execSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';

export const base = process.env.BASE_URL ?? 'http://localhost:5080';
export const lab = process.env.LAB_CLI ?? 'dotnet ../src/Lab.Cli/bin/Debug/net10.0/lab.dll';
export const shots = process.env.E2E_SCREENSHOTS ?? 'e2e/screenshots';
export const headers = { 'Content-Type': 'application/json', 'X-Lab-Client': 'e2e' };
mkdirSync(shots, { recursive: true });

export async function api(path, init) {
  const response = await fetch(base + path, init);
  if (!response.ok) throw new Error(`${init?.method ?? 'GET'} ${path}: ${response.status} ${await response.text()}`);
  return response.status === 204 ? null : response.json();
}

/** Injeta a solução de referência da semente (com as variáveis do desafio) como o desenho da tentativa aberta. */
export async function injectReference(page, challengeId) {
  const challenge = await api(`/api/challenges/${challengeId}`);
  const vars = Object.entries(challenge.variables).map(([k, v]) => `--var=${k}=${v}`).join(' ');
  const twists = (challenge.twists ?? []).map((t) => `--twist ${t}`).join(' ');
  const design = JSON.parse(
    execSync(`${lab} challenge scene ${challenge.seedId} --difficulty ${challenge.difficulty} --provider ${challenge.provider} ${vars} ${twists}`).toString(),
  );
  // O autosave da página (ex.: componentes iniciais) pode salvar entre a leitura da versão e o PUT: relê e tenta de novo.
  let attempt;
  for (let tries = 0; ; tries++) {
    attempt = (await api(`/api/challenges/${challengeId}/attempts`)).at(-1);
    const response = await fetch(`${base}/api/attempts/${attempt.id}/draft`, {
      method: 'PUT',
      headers: { ...headers, 'If-Match': `"${attempt.draftVersion}"` },
      body: JSON.stringify({ scene: design.scene }),
    });
    if (response.ok) break;
    if (response.status !== 412 || tries >= 5) throw new Error(`PUT draft: ${response.status} ${await response.text()}`);
    await new Promise((r) => setTimeout(r, 700));
  }
  await api(`/api/attempts/${attempt.id}/flows`, { method: 'PUT', headers, body: JSON.stringify({ flows: design.flows }) });
  await page.reload();
  await page.waitForSelector('.excalidraw');
  await page.waitForTimeout(1500);
  return { challenge, attemptId: attempt.id };
}

export async function waitFor(probe, done, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await probe();
    if (done(value) || Date.now() > deadline) return value;
    await new Promise((r) => setTimeout(r, 300));
  }
}

/** Coleta erros de página e de console (a jornada falha se houver algum). */
export function watchErrors(page) {
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  page.on('dialog', (d) => d.accept());
  return errors;
}

/** Cria um desafio com um rascunho que tem formas desenhadas à mão (sem tipo), no formato mínimo do motor. */
export async function draftWithShapes(labels, seedId = 'pix/consulta-dict', difficulty = 'junior') {
  const challenge = await api('/api/challenges', { method: 'POST', headers, body: JSON.stringify({ seedId, provider: 'aws', difficulty }) });
  const attempt = await api(`/api/challenges/${challenge.id}/attempts`, { method: 'POST', headers, body: '{}' });
  const elements = labels.flatMap((label, i) => [
    { id: `shape_${i}`, type: 'rectangle', x: 200 + i * 260, y: 120, width: 180, height: 80 },
    { id: `shape_${i}_txt`, type: 'text', x: 210 + i * 260, y: 150, width: 160, height: 20, containerId: `shape_${i}`, text: label },
  ]);
  await api(`/api/attempts/${attempt.id}/draft`, {
    method: 'PUT',
    headers: { ...headers, 'If-Match': `"${attempt.draftVersion}"` },
    body: JSON.stringify({ scene: { type: 'excalidraw', version: 2, elements, appState: {} } }),
  });
  return { challengeId: challenge.id, attemptId: attempt.id };
}
