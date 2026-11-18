// Liquid Glass for the taskbar, drawn into the wallpaper itself: the dock and
// tray pills refract the threads behind them. Windows' compositor can't bend
// what's behind a window, but the wallpaper is the only thing ever behind the
// taskbar, so the glass is rendered here and the taskbar itself is transparent.
//
// A helper (dockglass/DockGlass.cs) writes geometry.js next to this page with the pills'
// screen rectangles and whether the desktop is visible; it's polled below.

// Physical pixels at 100% scaling; multiplied by the display scale.
var GLASS_LOOK = {
  bezel: 16,        // width of the curved rim
  thickness: 20,    // glass height; how far the rim bends the background
  specular: 0,      // light on the rim facing the top-left (off: it reads as an outline)
  dim: 0.16,        // darkens only bright content behind, so white icons stay readable
  bodyTop: 0.16,    // the glass's own body: light catching the top, fading downward
  bodyBottom: 0.02,
  rimGlow: 0.07,    // light gathering in the curved rim, fading inward (no line)
  tint: [0.74, 0.84, 1.0], // cool steel blue, matching the XDR Blue threads
};

var GLASS_VERT = `#version 300 es
in vec2 aPos;
void main() { gl_Position = vec4(aPos, 0.0, 1.0); }`;

var GLASS_FRAG = `#version 300 es
precision highp float;
uniform sampler2D uStrip;
uniform vec2 uStripSize;
uniform vec4 uRect[2];
uniform float uRadius[2];
uniform int uCount;
uniform float uK;
uniform vec4 uLook;
uniform vec3 uBody;
uniform vec3 uTint;
out vec4 outColor;

float sdRoundRect(vec2 p, vec4 r, float rad) {
  vec2 q = abs(p - (r.xy + r.zw * 0.5)) - (r.zw * 0.5 - rad);
  return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - rad;
}

vec3 strip(vec2 p) {
  return texture(uStrip, clamp(p, vec2(0.5), uStripSize - 0.5) / uStripSize).rgb;
}

void main() {
  vec2 p = gl_FragCoord.xy;
  float sd = 1e9;
  vec4 rect = vec4(0.0);
  float rad = 0.0;
  for (int i = 0; i < 2; i++) {
    if (i >= uCount) break;
    float s = sdRoundRect(p, uRect[i], uRadius[i]);
    if (s < sd) { sd = s; rect = uRect[i]; rad = uRadius[i]; }
  }
  if (sd > 1.0) discard;

  // Outward direction of the rim at this pixel.
  const float e = 0.5;
  vec2 g = vec2(sdRoundRect(p + vec2(e, 0.0), rect, rad) - sdRoundRect(p - vec2(e, 0.0), rect, rad),
                sdRoundRect(p + vec2(0.0, e), rect, rad) - sdRoundRect(p - vec2(0.0, e), rect, rad));
  g = normalize(g + 1e-6);

  // Convex squircle rim: height h(x) = (1 - (1 - x)^4)^(1/4), x from 0 at the
  // edge to 1 where the flat top starts.
  float bezel = uLook.x * uK;
  float thick = uLook.y * uK;
  float x = clamp(max(-sd, 0.0) / bezel, 0.0, 1.0);
  float a = 1.0 - x;
  float a4 = a * a * a * a;
  float h = pow(max(1.0 - a4, 0.0), 0.25);
  float slope = min(a * a * a * pow(max(1.0 - a4, 1e-4), -0.75), 6.0);
  vec3 n = normalize(vec3(g * (thick / bezel) * slope, 1.0));

  // Snell's law, a slightly different index per channel for dispersion.
  vec3 eta = vec3(1.0 / 1.47, 1.0 / 1.5, 1.0 / 1.54);
  vec3 col;
  for (int c = 0; c < 3; c++) {
    vec3 r = refract(vec3(0.0, 0.0, -1.0), n, eta[c]);
    col[c] = strip(p + r.xy / max(-r.z, 0.15) * thick * h)[c];
  }

  // Light from the top-left, spread over the whole rim (no hard line), and a
  // fainter bounce on the opposite rim.
  float facing = dot(g, normalize(vec2(-0.55, 0.85)));
  float rim = a * a;
  col += uLook.z * rim * (max(facing, 0.0) + 0.35 * max(-facing, 0.0));
  col *= 1.0 - uLook.w * dot(col, vec3(0.2126, 0.7152, 0.0722));

  // The glass's own body, lit from above, plus light gathering in the curved
  // rim and fading inward, so the pill reads as glass even over black without
  // any edge line.
  float up = clamp((p.y - rect.y) / rect.w, 0.0, 1.0);
  col = mix(col, uTint, mix(uBody.y, uBody.x, up));
  col += uTint * uBody.z * a * a * a * mix(0.55, 1.0, up);

  float cover = clamp(0.5 - sd, 0.0, 1.0);
  outColor = vec4(mix(strip(p), col, cover), 1.0);
}`;

function glassShader(type, source) {
  const s = gl.createShader(type);
  gl.shaderSource(s, source);
  gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
    console.warn(`glass shader: ${gl.getShaderInfoLog(s)}`);
    return null;
  }
  return s;
}

// A broken glass shader must never take the wallpaper down with it.
function createGlass() {
  const vs = glassShader(gl.VERTEX_SHADER, GLASS_VERT);
  const fs = glassShader(gl.FRAGMENT_SHADER, GLASS_FRAG);
  if (!vs || !fs) return null;
  const prog = gl.createProgram();
  gl.attachShader(prog, vs);
  gl.attachShader(prog, fs);
  gl.bindAttribLocation(prog, aPos, "aPos");
  gl.linkProgram(prog);
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
    console.warn(`glass program: ${gl.getProgramInfoLog(prog)}`);
    return null;
  }
  const loc = {};
  for (const name of ["uStrip", "uStripSize", "uRect", "uRadius", "uCount", "uK", "uLook", "uBody", "uTint"]) loc[name] = gl.getUniformLocation(prog, name);
  const tex = gl.createTexture();
  gl.activeTexture(gl.TEXTURE1);
  gl.bindTexture(gl.TEXTURE_2D, tex);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.activeTexture(gl.TEXTURE0);
  gl.useProgram(prog);
  gl.uniform1i(loc.uStrip, 1);
  gl.useProgram(program);
  return { prog, loc, tex, texW: 0, texH: 0, target: null, cur: null, screen: [1920, 1080], dpi: 1, mode: "visible", rev: -1, last: 0 };
}

// Copies the strip behind the taskbar and redraws the pills as glass over it.
function glassPass() {
  if (!glass || still !== null || !glass.target || !glass.target.length || glass.mode === "hidden") return;
  const now = performance.now();
  const dt = Math.min(0.1, (now - (glass.last || now)) / 1e3);
  glass.last = now;

  // Ease toward the dock's real size when apps open or close; snap otherwise.
  const snap = !glass.cur || glass.cur.length !== glass.target.length || glass.mode !== "visible";
  const f = 1 - Math.exp(-dt / 0.06);
  glass.cur = snap ? glass.target.map((r) => r.slice()) : glass.cur.map((r, i) => r.map((v, j) => v + (glass.target[i][j] - v) * f));

  const k = canvas.width / glass.screen[0];
  const rects = [0, 0, 0, 0, 0, 0, 0, 0];
  const radii = [0, 0];
  let top = 0, left = Infinity, right = 0;
  glass.cur.slice(0, 2).forEach(([x, y, w, h], i) => {
    const X = x * k, Y = canvas.height - (y + h) * k, W = w * k, H = h * k;
    rects.splice(i * 4, 4, X, Y, W, H);
    radii[i] = Math.min(H / 2, 16 * glass.dpi * k);
    top = Math.max(top, Y + H);
    left = Math.min(left, X);
    right = Math.max(right, X + W);
  });
  const stripH = Math.min(canvas.height, Math.ceil(top + 48 * glass.dpi * k));

  gl.activeTexture(gl.TEXTURE1);
  gl.bindTexture(gl.TEXTURE_2D, glass.tex);
  if (glass.texW !== canvas.width || glass.texH !== stripH) {
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, canvas.width, stripH, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    glass.texW = canvas.width;
    glass.texH = stripH;
  }
  gl.copyTexSubImage2D(gl.TEXTURE_2D, 0, 0, 0, 0, 0, canvas.width, stripH);

  const { loc } = glass;
  gl.useProgram(glass.prog);
  gl.uniform2f(loc.uStripSize, canvas.width, stripH);
  gl.uniform4fv(loc.uRect, rects);
  gl.uniform1fv(loc.uRadius, radii);
  gl.uniform1i(loc.uCount, Math.min(2, glass.cur.length));
  gl.uniform1f(loc.uK, k);
  gl.uniform4f(loc.uLook, GLASS_LOOK.bezel * glass.dpi, GLASS_LOOK.thickness * glass.dpi, GLASS_LOOK.specular, GLASS_LOOK.dim);
  gl.uniform3f(loc.uBody, GLASS_LOOK.bodyTop, GLASS_LOOK.bodyBottom, GLASS_LOOK.rimGlow);
  gl.uniform3fv(loc.uTint, GLASS_LOOK.tint);
  gl.enable(gl.SCISSOR_TEST);
  gl.scissor(Math.floor(left) - 2, 0, Math.ceil(right - left) + 4, Math.ceil(top) + 2);
  gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  gl.disable(gl.SCISSOR_TEST);
  gl.useProgram(program);
  gl.activeTexture(gl.TEXTURE0);
}

// While a window covers the desktop only the strip behind the taskbar shows,
// so the frame loop stops and a frame is drawn only when the dock changes size.
function glassSkipFrame() {
  return !!glass && glass.mode !== "visible";
}

var glass = createGlass();
window.__glassLook = GLASS_LOOK;

window.__glassUpdate = (g) => {
  if (!glass || !g || g.rev === glass.rev) return;
  glass.rev = g.rev;
  glass.screen = g.screen;
  glass.dpi = g.dpi || 1;
  glass.mode = g.mode || "visible";
  glass.target = [g.dock, g.tray].filter(Boolean);
  if (glass.mode === "covered") draw();
  if (glass.mode === "visible" && glass.asleep) {
    glass.asleep = false;
    requestAnimationFrame(frame);
  }
};

setInterval(() => {
  const s = document.createElement("script");
  s.src = `geometry.js?${Date.now()}`;
  s.onload = s.onerror = () => s.remove();
  document.head.appendChild(s);
}, 250);
