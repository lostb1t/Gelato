// What the settings page and the setup assistant share: the plugin's id, the addon check and its
// card, the logo, and the reason a failed request gives.

export const pluginId = '94EA4E14-8163-4989-96FE-0A2094BC2D6A';

// The theme's accent, taken from its primary button: skins colour that one, and few of them say
// their colour any other way. A skin whose button is about the page's own colour (an outlined one)
// gets the button's border, or else the text colour, so what is marked stays visible.
function rgb(color) {
  const m = /rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?\)/.exec(color || '');
  return m && (m[4] === undefined || +m[4] > 0.5) ? [+m[1], +m[2], +m[3]] : null;
}

function luminance([r, g, b]) {
  const c = v => (v /= 255) <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  return 0.2126 * c(r) + 0.7152 * c(g) + 0.0722 * c(b);
}

function contrast(a, b) {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
}

function backgroundOf(el) {
  for (; el; el = el.parentElement) {
    const color = rgb(getComputedStyle(el).backgroundColor);
    if (color) return color;
  }
  return [16, 16, 16];
}

export function applyThemeAccent(target, button) {
  const style = getComputedStyle(button);
  const page = backgroundOf(target);
  const pick = [style.backgroundColor, style.borderTopColor, getComputedStyle(target).color]
    .map(c => [c, rgb(c)])
    .find(([, color]) => color && contrast(color, page) >= 2);
  if (pick) target.style.setProperty('--g-accent', pick[0]);
}

// ApiClient rejects with the Response: its body holds the server's reason.
export async function errorText(e) {
  try {
    if (e && typeof e.text === 'function') {
      const text = await e.text();
      // The reason comes as a JSON string, quotes included.
      try {
        const parsed = JSON.parse(text);
        if (typeof parsed === 'string') return parsed;
      } catch (_) { /* plain text */ }
      return text || e.statusText || ('HTTP ' + e.status);
    }
  } catch (_) { /* fall through */ }
  return e?.responseText || e?.message || e?.statusText || String(e);
}

// Asks the addon behind a URL for its manifest, through the server: what the server reaches
// is what counts, and the URL stays out of the browser's history.
export function checkAddon(url) {
  return window.ApiClient.ajax({
    type: 'POST',
    url: window.ApiClient.getUrl('gelato/addon/check'),
    data: JSON.stringify({ Url: url }),
    contentType: 'application/json',
    dataType: 'json'
  });
}

function chip(on, label) {
  const c = document.createElement('span');
  c.className = 'gelatoChip ' + (on ? 'yes' : 'no');
  c.innerHTML = '<span class="material-icons ' + (on ? 'check_circle' : 'remove_circle_outline') +
    '" aria-hidden="true"></span>';
  c.append(label);
  return c;
}

// What the addon answered, as a card: used on the Addon tab and in the setup assistant.
export function renderAddonCard(target, res, error) {
  target.innerHTML = '';
  const card = document.createElement('div');
  const ok = res && res.Ok;
  // An addon that answers but streams nothing is AIOStreams' bare address, not a config of it.
  const empty = ok && !res.Streams;
  card.className = 'gelatoAddon ' + (empty ? 'warn' : ok ? 'ok' : 'bad');
  const logo = document.createElement('div');
  logo.className = 'addonLogo';
  logo.innerHTML = '<span class="material-icons ' + (empty ? 'warning_amber' : ok ? 'check_circle' : 'error_outline') +
    '" aria-hidden="true"></span>';
  if (ok && res.Logo && /^https?:\/\//i.test(res.Logo)) {
    // Shown only once it loads: a broken image would hide the tick.
    const img = new Image();
    img.onload = () => {
      logo.innerHTML = '';
      logo.style.backgroundImage = 'url("' + res.Logo.replace(/"/g, '%22') + '")';
    };
    img.src = res.Logo;
  }
  const text = document.createElement('div');
  text.className = 'addonText';
  const name = document.createElement('div');
  name.className = 'addonName';
  const meta = document.createElement('div');
  meta.className = 'addonMeta';
  if (ok) {
    name.textContent = (res.Name || 'Addon') + (res.Version ? ' ' + res.Version : '');
    const importable = (res.Catalogs || []).filter(c => c.Importable).length;
    meta.textContent = 'Answered in ' + res.Milliseconds + ' ms · ' + importable + ' catalogs to import';
    const chips = document.createElement('div');
    chips.className = 'gelatoChips';
    chips.append(chip(res.Streams, 'Streams'), chip(res.SearchMovie, 'Movie search'),
      chip(res.SearchSeries, 'Series search'), chip(res.Meta, 'Metadata'), chip(res.Subtitles, 'Subtitles'));
    text.append(name, meta, chips);
    if (empty) {
      const hint = document.createElement('div');
      hint.className = 'addonMeta';
      hint.style.marginTop = '.4em';
      hint.textContent = 'It answers but offers no streams, so this looks like AIOStreams itself, not your saved ' +
        'config. Copy the manifest URL AIOStreams shows after you save.';
      text.appendChild(hint);
    }
  } else {
    name.textContent = 'The addon didn\'t answer';
    meta.textContent = error || res?.Error || 'Check the URL and that this server can reach it.';
    text.append(name, meta);
  }
  card.append(logo, text);
  target.appendChild(card);
}

// Gelato's logo (logo.png) as shapes, so the setup can animate it. Plain shapes only: Jellyfin Web
// sets a <base href>, and a url(#id) to a clip path or filter then points at nothing. The glow is a
// CSS drop-shadow for the same reason.
const CONE = 'M 128 528 L 556 528 Q 590 528 578 558 L 366 1048 Q 342 1094 318 1048 L 106 558 Q 94 528 128 528 Z';
const SCOOP = 'M 8 400 C 8 160 170 6 342 6 C 514 6 676 160 676 400 Q 676 485 592 485 L 530 485 ' +
  'Q 485 485 485 545 L 485 648 Q 485 690 445 690 Q 405 690 405 648 L 405 555 Q 405 485 342 485 ' +
  'Q 280 485 280 555 L 280 603 Q 280 645 240 645 Q 200 645 200 603 L 200 555 Q 200 485 138 485 ' +
  'L 92 485 Q 8 485 8 400 Z';
// The cone's waffle: a lattice of windows cut to the cone's edges.
const WAFFLE = [
  '111,536 112,535 176,535 188,547 144,591 127,574',
  '244,535 308,535 320,547 276,591 232,547',
  '376,535 440,535 452,547 408,591 364,547',
  '508,535 572,535 573,536 557,574 540,591 496,547',
  '210,569 254,613 210,657 166,613',
  '342,569 386,613 342,657 298,613',
  '474,569 518,613 474,657 430,613',
  '276,635 320,679 276,723 232,679',
  '408,635 452,679 408,723 364,679',
  '191,720 210,701 254,745 218,781',
  '342,701 386,745 342,789 298,745',
  '474,701 493,720 466,781 430,745',
  '276,767 320,811 276,855 232,811',
  '408,767 452,811 408,855 364,811',
  '342,833 386,877 342,921 298,877',
  '272,903 276,899 320,943 299,964',
  '408,899 412,903 385,964 364,943',
  '312,995 342,965 372,995 349,1046 342,1053 335,1046',
];

function logoSvg() {
  return `<svg viewBox="0 0 684 1094" aria-hidden="true">
    <path d="${CONE}" fill="#eeb062" />
    ${WAFFLE.map(points => `<polygon points="${points}" fill="#f6c47c" />`).join('')}
    <ellipse class="logoDrip" cx="445" cy="700" rx="18" ry="24" fill="#b2e9cf" />
    <path class="logoScoop" fill="#b2e9cf" d="${SCOOP}" />
  </svg>`;
}

export function drawLogos(root) {
  root.querySelectorAll('[data-logo]:not([data-drawn])').forEach(el => {
    el.dataset.drawn = '1';
    el.innerHTML = logoSvg();
    if (el.dataset.logo === 'animated') el.classList.add('animated');
  });
}
