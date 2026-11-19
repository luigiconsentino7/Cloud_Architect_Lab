// Prepara a interface para rodar o app a partir do código (usado pelo app.cmd e pelo app.sh): confere a versão do
// Node.js e instala as dependências só quando o package-lock.json mudou desde a última instalação. O `npm ci` apaga
// e recria o node_modules inteiro: é lento e, numa pasta sincronizada (OneDrive), costuma falhar com EPERM.
import { execSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { nodeSupported } from './node-version.mjs';

if (!nodeSupported(process.version)) {
  console.error(`\nO Node.js ${process.version} não serve para o Angular 22: ele precisa da versão 22.22.3 ou mais nova`);
  console.error('(linha 22), 24.15.0 ou mais nova (linha 24) ou 26.');
  console.error('Atualize e abra o terminal de novo. No Windows: winget install OpenJS.NodeJS.LTS');
  process.exit(1);
}

const lock = createHash('sha256').update(readFileSync('package-lock.json')).digest('hex');
const stamp = 'node_modules/.lab-package-lock.sha256';
if (existsSync(stamp) && readFileSync(stamp, 'utf8').trim() === lock) {
  console.log('Dependências da interface em dia.');
  process.exit(0);
}

console.log('Instalando as dependências da interface (npm ci; só na primeira vez ou quando elas mudam)...');
try {
  execSync('npm ci --no-audit --no-fund', { stdio: 'inherit' });
} catch {
  console.error('\nO npm ci falhou (a mensagem dele está acima). Se for EPERM ou EBUSY numa pasta do OneDrive, mova o');
  console.error('repositório para fora dela (ex.: C:\\dev\\Cloud_Architect_Lab) ou pause a sincronização e tente de novo.');
  process.exit(1);
}
writeFileSync(stamp, lock);
