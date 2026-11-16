import { keepFontsLocal, localSources } from './canvas.component';

describe('fontes do canvas', () => {
  const both = "url(http://localhost:5080/excalidraw/fonts/Excalifont/a.woff2) format('woff2'), url(https://esm.sh/@excalidraw/excalidraw@0.18.1/dist/prod/fonts/Excalifont/a.woff2) format('woff2')";

  it('tira o CDN esm.sh e mantém a origem local', () => {
    expect(localSources(both)).toBe("url(http://localhost:5080/excalidraw/fonts/Excalifont/a.woff2) format('woff2')");
    expect(localSources("url(data:font/woff2;base64,AAAA) format('woff2')")).toBe("url(data:font/woff2;base64,AAAA) format('woff2')");
  });

  it('troca o construtor de FontFace por um que só usa fontes locais', () => {
    const created: string[] = [];
    class FakeFontFace {
      constructor(_family: string, source: string) {
        created.push(source);
      }
    }
    const target = { FontFace: FakeFontFace as unknown as typeof FontFace };

    keepFontsLocal(target);
    keepFontsLocal(target); // idempotente
    const face = new target.FontFace('Excalifont', both);

    expect(created).toEqual(["url(http://localhost:5080/excalidraw/fonts/Excalifont/a.woff2) format('woff2')"]);
    expect(face instanceof target.FontFace).toBe(true);
  });
});
