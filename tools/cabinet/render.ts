// render.ts: draws the wood-grain TV cabinet that frames every channel, as PNGs with a see-through tube. The PNGs are
// committed and shipped inside the plugin; run this only to redraw them.
//   cd tools/check && bun install && cd ../.. && bun tools/cabinet/render.ts
//
// One PNG per stream size, drawn fresh rather than scaled: resizing 1080p's fine scanlines to 720p turns them into
// bands. The numbers marked "Picture" must stay in step with Picture (tube) and CabinetArt (logo plate) in the plugin.
import { resolve } from "node:path";
import { chromium } from "../check/node_modules/playwright/index.mjs";

const out = resolve(import.meta.dir, "../../Jellyfin.Plugin.Spelunkr/Stream");

function page(height: number) {
  const s = height / 1080; // everything is laid out at 1080p and scaled
  const px = (n: number) => `${n * s}px`;
  // Picture: the tube, 4:3 and even at both sizes. The film fills this; the hole starts 2 px inside it, so a film
  // that rounds a pixel short never shows a gap.
  const [tx, ty, tw, th] = [312, 54, 1296, 972].map((n) => n * s);
  const inset = 2 * s;
  const r = 62 * s;
  const [hx, hy, hw, hh] = [tx + inset, ty + inset, tw - 2 * inset, th - 2 * inset];
  const hole = `M${hx + r} ${hy}h${hw - 2 * r}a${r} ${r} 0 0 1 ${r} ${r}v${hh - 2 * r}a${r} ${r} 0 0 1 ${-r} ${r}h${-(hw - 2 * r)}a${r} ${r} 0 0 1 ${-r} ${-r}v${-(hh - 2 * r)}a${r} ${r} 0 0 1 ${r} ${-r}z`;
  const frame = `M0 0H${1920 * s}V${height}H0z`;
  // A whole number of pixels at either size, with the dark line one pixel tall, so nothing beats against the frame.
  const period = height === 1080 ? 3 : 2;
  const line = height === 1080 ? 0.2 : 0.14; // the same overall darkening at both sizes

  return `<!doctype html><meta charset=utf-8><style>
html,body{margin:0;background:transparent}
.f{position:relative;width:${1920 * s}px;height:${height}px;overflow:hidden}
.cab{position:absolute;inset:0;background:#7b4a22;clip-path:path(evenodd,'${frame} ${hole}')}
.wood{position:absolute;inset:0;width:100%;height:100%}
.bevel{position:absolute;inset:0;box-shadow:inset 0 0 0 ${px(10)} #2a1408,inset 0 0 ${px(76)} ${px(19)} rgba(0,0,0,.5)}
.ring{position:absolute;left:${tx}px;top:${ty}px;width:${tw}px;height:${th}px;border-radius:${r}px;background:#17110c;box-shadow:0 0 0 ${px(17)} #17110c,0 0 0 ${px(27)} #b08a4a,0 0 0 ${px(33)} #5a4220}
.plate{position:absolute;left:${px(63)};top:${px(84)};width:${px(150)};height:${px(150)};padding:${px(15)};box-sizing:border-box;background:linear-gradient(135deg,#e2c67c,#a07c38 55%,#d9bc72);border-radius:${px(12)}}
.plate div{width:100%;height:100%;background:#17110c}
.brand{position:absolute;left:${px(24)};width:${px(230)};top:${px(258)};text-align:center;font:italic 700 ${px(40)}/1.15 Georgia,serif;color:#e8cc85;text-shadow:0 ${px(2)} ${px(2)} #1a0d05}
.brand b{display:block;font:700 ${px(29)}/1.3 Georgia,serif;letter-spacing:${px(8)};font-style:normal}
.grille{position:absolute;left:${px(52)};width:${px(174)};top:${px(424)};height:${px(558)};background:repeating-linear-gradient(#1a0f07 0,#1a0f07 ${px(10)},#5a3a1c ${px(10)},#5a3a1c ${px(17)})}
.k{position:absolute;left:${px(1706)};width:${px(116)};height:${px(116)};border-radius:50%;background:radial-gradient(circle at 35% 30%,#efd795,#8a6a30 70%);box-shadow:0 ${px(3)} ${px(6)} rgba(0,0,0,.6)}
.g2{left:${px(1677)};top:${px(676)};height:${px(306)}}
.tube{position:absolute;left:${hx}px;top:${hy}px;width:${hw}px;height:${hh}px;border-radius:${r}px;
  background:radial-gradient(ellipse 72% 72% at 50% 50%,transparent 62%,rgba(0,0,0,.5) 100%),repeating-linear-gradient(rgba(0,0,0,${line}) 0,rgba(0,0,0,${line}) 1px,transparent 1px,transparent ${period}px)}
</style><div class=f>
<div class=cab>
<svg class=wood preserveAspectRatio="none" viewBox="0 0 800 450"><filter id=w x="0" y="0" width="100%" height="100%" color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.004 0.11" numOctaves="3" seed="11"/><feColorMatrix values="0 0 0 0 .17  0 0 0 0 .08  0 0 0 0 .03  3.2 0 0 0 -1.25"/></filter><rect width="800" height="450" fill="#7b4a22"/><rect width="800" height="450" filter="url(#w)"/></svg>
<div class=bevel></div><div class=ring></div>
<div class=plate><div></div></div><div class=brand>Spelunkr<b>TV</b></div>
<div class=grille></div>
<div class=k style="top:${px(100)}"></div><div class=k style="top:${px(290)}"></div>
<div class="grille g2"></div>
</div>
<div class=tube></div>
</div>`;
}

const browser = await chromium.launch(process.env.CHROME ? { executablePath: process.env.CHROME } : {});
for (const [width, height] of [[1920, 1080], [1280, 720]]) {
  const tab = await browser.newPage({ viewport: { width, height } });
  await tab.setContent(page(height));
  await tab.screenshot({ path: `${out}/cabinet-${height}.png`, omitBackground: true });
  await tab.close();
}

await browser.close();
