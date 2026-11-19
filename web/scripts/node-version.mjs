// Versões do Node.js que o Angular CLI 22 aceita (@angular/cli/src/utilities/node-version.js: ^22.22.3, ^24.15.0 e 26;
// as ímpares 23 e 25 passam com aviso). Fora delas, o `ng build` para com o código 3.
const minimum = { 22: [22, 3], 23: [0, 0], 24: [15, 0], 25: [0, 0], 26: [0, 0] };

export function nodeSupported(version) {
  const [major, minor, patch] = version.replace(/^v/, '').split('.').map(Number);
  const min = minimum[major];
  return !!min && (minor > min[0] || (minor === min[0] && patch >= min[1]));
}
