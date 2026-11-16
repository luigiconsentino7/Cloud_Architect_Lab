// Copia o build do Angular para o wwwroot do Lab.App (o executável serve a SPA na mesma origem).
import { cpSync, existsSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const source = join(root, 'dist', 'web', 'browser');
const target = join(root, '..', 'src', 'Lab.App', 'wwwroot');
if (!existsSync(source)) throw new Error(`Build não encontrado em ${source}. Rode "ng build" antes.`);
rmSync(target, { recursive: true, force: true });
cpSync(source, target, { recursive: true });
console.log(`SPA copiada para ${target}`);
