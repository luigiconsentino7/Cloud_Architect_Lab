// Ollama de mentira para testar o modo com IA sem GPU: implementa /api/tags e /api/chat (com e sem streaming NDJSON)
// e responde conforme o papel, lendo o prompt de sistema. Só para testes: nada aqui é IA de verdade.
import { createServer } from 'node:http';

const interviewAnswer =
  'Boa pergunta. O enunciado não fixa esse ponto: pode assumir, mas deixe a premissa explícita no desenho e pense no que acontece se a mesma ordem chegar duas vezes.';

function personalize(format) {
  // Esquema por semente (anyOf com o seedId fixo em cada ramo) ou o esquema simples de reserva.
  const seeds = format?.anyOf?.map((branch) => branch.properties.seedId.const) ?? format?.properties?.seedId?.enum ?? [];
  return {
    seedId: seeds.find((s) => s.includes('pix')) ?? seeds[0],
    narrative: 'A FinBank vai lançar Pix por aproximação no próximo trimestre e o time precisa garantir que nenhum cliente pague duas vezes.',
    variables: [],
    twists: [],
    clarificationsExtra: [{ q: 'O app atende só pessoa física?', a: 'Sim, nesta fase só pessoa física.' }],
    reason: 'O contexto fala de Pix num banco digital, então escolhi o envio de Pix.',
  };
}

function mapLabel(format) {
  const ids = (format?.properties?.catalogId?.enum ?? []).filter((id) => id !== 'nenhum');
  return { funcao: 'Regras de negócio rodam bem em funções.', componente: true, catalogId: ids.find((id) => id.includes('lambda') || id.includes('functions')) ?? ids[0] ?? 'nenhum' };
}

function narrate(user) {
  const score = /Nota final: (\d+)/.exec(user)?.[1] ?? '0';
  return {
    paragraphs: [
      `Nota ${score}: o desenho segurou a carga e as falhas que a avaliação simulou.`,
      'Siga os pontos de melhoria na ordem da revisão e use as perguntas de follow-up para estudar.',
    ],
  };
}

export function startFakeOllama({ port = 11999, model = 'gemma3:4b', chunkDelayMs = 25, slowTagsMs = Number(process.env.FAKE_OLLAMA_SLOW_TAGS_MS ?? 0) } = {}) {
  const requests = [];
  const server = createServer(async (req, res) => {
    if (process.env.FAKE_OLLAMA_LOG) console.log(`${req.method} ${req.url}`);
    if (req.method === 'GET' && req.url === '/api/tags') {
      // Simula o /api/tags lento de um Ollama frio (disco, antivírus): a checagem do app não pode desistir cedo.
      if (slowTagsMs > 0) await new Promise((r) => setTimeout(r, slowTagsMs));
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ models: [{ name: model, model, modified_at: new Date().toISOString(), size: 1, digest: 'fake' }] }));
      return;
    }
    if (req.method !== 'POST' || req.url !== '/api/chat') {
      res.writeHead(404).end();
      return;
    }

    let raw = '';
    for await (const chunk of req) raw += chunk;
    const body = JSON.parse(raw);
    requests.push(body);
    if (process.env.FAKE_OLLAMA_DUMP) console.log(`--- ${body.messages.length} mensagens; sistema:\n${body.messages[0].content}\n--- usuário:\n${body.messages.at(-1).content}\n---`);
    const system = body.messages.find((m) => m.role === 'system')?.content ?? '';
    const firstUser = body.messages.find((m) => m.role === 'user')?.content ?? '';
    const content = system.startsWith('Você monta desafios')
      ? JSON.stringify(personalize(body.format))
      : system.startsWith('Você é um mentor')
        ? JSON.stringify(narrate(firstUser))
        : system.startsWith('Você ajuda a tipar')
          ? JSON.stringify(mapLabel(body.format))
          : interviewAnswer;

    const done = { model, created_at: new Date().toISOString(), done: true, done_reason: 'stop', total_duration: 1, prompt_eval_count: 10, eval_count: 10 };
    if (body.stream === false) {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ...done, message: { role: 'assistant', content } }));
      return;
    }

    res.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
    for (let i = 0; i < content.length; i += 12) {
      res.write(JSON.stringify({ model, created_at: new Date().toISOString(), message: { role: 'assistant', content: content.slice(i, i + 12) }, done: false }) + '\n');
      await new Promise((r) => setTimeout(r, chunkDelayMs));
    }
    res.end(JSON.stringify({ ...done, message: { role: 'assistant', content: '' } }) + '\n');
  });

  return new Promise((resolve) => server.listen(port, '127.0.0.1', () => resolve({ requests, close: () => new Promise((r) => server.close(r)) })));
}

// Rodando direto (node e2e/fake-ollama.mjs), fica no ar até Ctrl+C.
if (import.meta.url === `file://${process.argv[1]}`) {
  const port = Number(process.env.FAKE_OLLAMA_PORT ?? 11999);
  await startFakeOllama({ port });
  console.log(`Ollama de mentira em http://127.0.0.1:${port}`);
}
