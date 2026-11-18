// Adds the taskbar Liquid Glass (glass.js) to the built PRO Threads wallpaper.
// Usage: node build-glass.mjs <index.base.html> <glass.js> <out index.html>
import fs from 'node:fs';

const [basePath, glassPath, outPath] = process.argv.slice(2);
let html = fs.readFileSync(basePath, 'utf8');
const glass = fs.readFileSync(glassPath, 'utf8');

const swaps = [
  // Glass goes over each finished frame.
  ['  gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);\n  dirty = false;', '  gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);\n  glassPass();\n  dirty = false;'],
  // The frame loop sleeps while a window covers the desktop; __glassUpdate wakes it.
  ['function frame(now) {\n  requestAnimationFrame(frame);', 'function frame(now) {\n  if (glassSkipFrame()) {\n    glass.asleep = true;\n    last = null;\n    return;\n  }\n  requestAnimationFrame(frame);'],
  ['window.__wall = { state, draw, pane };\n', `window.__wall = { state, draw, pane };\n\n// glass.js\n${glass}\n`],
];
for (const [from, to] of swaps) {
  const at = html.indexOf(from);
  if (at < 0 || html.indexOf(from, at + 1) >= 0) throw new Error(`expected exactly one match: ${from.slice(0, 60)}`);
  html = html.replace(from, () => to);
}
if (/<\/script/i.test(glass)) throw new Error('glass.js contains </script');
fs.writeFileSync(outPath, html);
console.log('wrote', outPath, html.length, 'bytes');
