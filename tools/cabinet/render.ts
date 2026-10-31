// render.ts: draws the wood-grain TV cabinet that frames every channel, as PNGs with a see-through tube. The PNGs are
// committed and shipped inside the plugin; run this only to redraw them.
//   cd tools/check && bun install && cd ../.. && bun tools/cabinet/render.ts
//
// One PNG per stream size, drawn fresh rather than scaled, so the slats, dial marks and lettering stay crisp at 720p.
// Fine repeating lines are what costs the most bitrate, so keep them out of the art: scanlines added 11% to a film. The numbers marked "Picture" must stay in step with Picture (tube) and CabinetArt (logo plate) in the plugin.
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

  // A real set is lit from above: every metal part goes bright at its top left, and every raised part drops a shadow.
  const metal = `linear-gradient(145deg,#f3e2a8 0%,#c9a65c 28%,#8f6d30 52%,#d8bb74 74%,#7d5e28 100%)`;
  const knob = (top: number, label: string) => `
<div class=dial style="top:${px(top - 26)}"></div>
<div class=k style="top:${px(top)}"><div class=cap><i></i></div></div>
<div class=lab style="top:${px(top + 147)}">${label}</div>`;
  const screw = (x: number, y: number, turn: number) => `<div class=screw style="left:${px(x)};top:${px(y)};transform:rotate(${turn}deg)"></div>`;

  return `<!doctype html><meta charset=utf-8><style>
html,body{margin:0;background:transparent}
.f{position:relative;width:${1920 * s}px;height:${height}px;overflow:hidden}
.cab{position:absolute;inset:0;background:#5e3519;clip-path:path(evenodd,'${frame} ${hole}')}
.wood{position:absolute;inset:0;width:100%;height:100%}
.light{position:absolute;inset:0;background:radial-gradient(ellipse 80% 75% at 32% 22%,rgba(255,214,160,.2),transparent 62%),linear-gradient(180deg,rgba(255,220,170,.1),transparent 28%,rgba(0,0,0,.34))}
.edge{position:absolute;inset:0;box-shadow:inset 0 ${px(5)} ${px(4)} rgba(255,222,170,.32),inset ${px(4)} 0 ${px(4)} rgba(255,222,170,.14),inset 0 ${px(-9)} ${px(8)} rgba(0,0,0,.6),inset ${px(-5)} 0 ${px(7)} rgba(0,0,0,.4),inset 0 0 ${px(70)} ${px(14)} rgba(20,8,2,.42)}
.ring{position:absolute;left:${tx}px;top:${ty}px;width:${tw}px;height:${th}px;border-radius:${r}px;background:#0b0806;
  box-shadow:0 0 0 ${px(16)} #0f0a07,0 0 0 ${px(19)} #6b4f22,0 0 0 ${px(26)} #c8a55c,0 0 0 ${px(28)} #f0dca0,0 0 0 ${px(31)} #5d4318,0 ${px(7)} ${px(18)} ${px(31)} rgba(0,0,0,.75),inset 0 0 ${px(9)} ${px(2)} #000}
.plate{position:absolute;left:${px(63)};top:${px(84)};width:${px(150)};height:${px(150)};padding:${px(15)};box-sizing:border-box;background:${metal};border-radius:${px(9)};box-shadow:0 ${px(3)} ${px(7)} rgba(0,0,0,.75),inset 0 ${px(1)} 0 rgba(255,245,210,.75),inset 0 ${px(-1)} 0 rgba(60,40,10,.8)}
.plate div{width:100%;height:100%;background:#120c08;box-shadow:inset 0 ${px(3)} ${px(5)} #000}
.screw{position:absolute;width:${px(9)};height:${px(9)};border-radius:50%;background:linear-gradient(135deg,#f0e0ae,#7c5d28);box-shadow:0 ${px(1)} ${px(1)} rgba(0,0,0,.7)}
.screw:after{content:"";position:absolute;left:0;right:0;top:44%;height:12%;background:#3a2810}
.brand{position:absolute;left:${px(24)};width:${px(230)};top:${px(252)};text-align:center;font:italic 700 ${px(41)}/1.1 Georgia,"DejaVu Serif",serif;color:#e9cf8c;text-shadow:0 ${px(-1)} 0 rgba(255,246,214,.75),0 ${px(2)} ${px(1)} #120802,0 ${px(4)} ${px(5)} rgba(0,0,0,.7)}
.brand b{display:block;font:700 ${px(28)}/1.35 Georgia,"DejaVu Serif",serif;letter-spacing:${px(9)};font-style:normal}
.grille{position:absolute;left:${px(52)};width:${px(174)};top:${px(424)};height:${px(558)};border-radius:${px(5)};background:#0a0604;box-shadow:0 0 0 ${px(3)} #2b1608,0 ${px(1)} 0 ${px(4)} rgba(255,214,160,.2),inset 0 ${px(6)} ${px(8)} #000}
.grille:before{content:"";position:absolute;inset:${px(5)};background:repeating-linear-gradient(180deg,#8a5a2d 0,#6b4220 ${px(4)},#3c2210 ${px(9)},#0a0503 ${px(10)},#0a0503 ${px(17)})}
.grille:after{content:"";position:absolute;inset:${px(5)};background:linear-gradient(90deg,rgba(0,0,0,.55),transparent 16%,transparent 84%,rgba(0,0,0,.55)),linear-gradient(180deg,rgba(0,0,0,.5),transparent 12%,transparent 80%,rgba(0,0,0,.5))}
.g2{left:${px(1677)};top:${px(676)};height:${px(306)}}
.dial{position:absolute;left:${px(1680)};width:${px(168)};height:${px(168)};border-radius:50%;background:radial-gradient(circle,transparent 62%,#2b1809 63%,#2b1809 66%,transparent 67%),repeating-conic-gradient(rgba(233,207,140,.85) 0 1.4deg,transparent 1.4deg 30deg);-webkit-mask:radial-gradient(circle,transparent 66%,#000 67%,#000 72%,transparent 73%);mask:radial-gradient(circle,transparent 66%,#000 67%,#000 72%,transparent 73%)}
.k{position:absolute;left:${px(1706)};width:${px(116)};height:${px(116)};border-radius:50%;background:repeating-conic-gradient(#2a1d0d 0 2.2deg,#a98a4d 2.2deg 4.5deg);box-shadow:0 ${px(9)} ${px(14)} rgba(0,0,0,.8),0 ${px(2)} ${px(3)} rgba(0,0,0,.9)}
.cap{position:absolute;inset:${px(11)};border-radius:50%;background:radial-gradient(circle at 34% 28%,#fff0bd 0,#dcbc72 22%,#a68242 58%,#5d4318 100%);box-shadow:inset 0 ${px(2)} ${px(2)} rgba(255,246,214,.7),inset 0 ${px(-3)} ${px(4)} rgba(40,25,5,.75),0 0 0 ${px(1)} #2a1d0d}
.cap i{position:absolute;left:47%;width:6%;top:5%;height:36%;background:#23170a;box-shadow:${px(1)} 0 0 rgba(255,240,200,.6)}
.lab{position:absolute;left:${px(1664)};width:${px(200)};text-align:center;font:700 ${px(15)}/1 Georgia,"DejaVu Serif",serif;letter-spacing:${px(4)};color:#d9bd7c;text-shadow:0 ${px(1)} ${px(1)} #120802}
.tube{position:absolute;left:${hx}px;top:${hy}px;width:${hw}px;height:${hh}px;border-radius:${r}px;overflow:hidden;
  background:
    radial-gradient(ellipse 58% 34% at 26% 12%,rgba(255,255,255,.075),transparent 72%),
    linear-gradient(118deg,transparent 34%,rgba(255,255,255,.035) 34.4%,rgba(255,255,255,.035) 45%,transparent 45.4%),
    radial-gradient(ellipse 74% 74% at 50% 50%,transparent 60%,rgba(0,0,0,.34) 86%,rgba(0,0,0,.66) 100%);
  box-shadow:inset 0 0 ${px(26)} ${px(6)} rgba(0,0,0,.62)}
</style><div class=f>
<div class=cab>
<svg class=wood preserveAspectRatio="none" viewBox="0 0 1920 1080"><defs>
<filter id=grain x="0" y="0" width="100%" height="100%" color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0016 0.05" numOctaves="3" seed="11" result="n"/><feDisplacementMap in="n" in2="n" scale="60" xChannelSelector="G" yChannelSelector="R"/><feColorMatrix values="0 0 0 0 .11  0 0 0 0 .05  0 0 0 0 .02  3.4 0 0 0 -1.32"/><feGaussianBlur stdDeviation="1.1"/></filter>
<filter id=tone x="0" y="0" width="100%" height="100%" color-interpolation-filters="sRGB"><feTurbulence type="fractalNoise" baseFrequency="0.0009 0.012" numOctaves="3" seed="23"/><feColorMatrix values="0 0 0 0 .78  0 0 0 0 .44  0 0 0 0 .18  1.7 0 0 0 -.72"/></filter>
</defs><rect width="1920" height="1080" fill="#6e4120"/><rect width="1920" height="1080" filter="url(#tone)"/><rect width="1920" height="1080" filter="url(#grain)"/></svg>
<div class=light></div><div class=edge></div><div class=ring></div>
<div class=plate><div></div></div>
${screw(69, 90, 20)}${screw(198, 90, 110)}${screw(69, 219, 75)}${screw(198, 219, 35)}
<div class=brand>Spelunkr<b>TV</b></div>
<div class=grille></div>
${knob(100, "VOLUME")}${knob(290, "CHANNEL")}
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
