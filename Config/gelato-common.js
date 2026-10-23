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
// The cone as in logo.png: its edges fitted to the logo's, and the band around the scoop's drips
// (the scoop's outline grown by 36 units) cut out of it, so the glow shows between them.
const CONE =
  'M 118 534 L 105 536 L 100 537 L 96 540 L 94 543 L 93 548 L 93 552 L 95 558 L 310 1048 L 322 1068 ' +
  'L 327 1073 L 334 1080 L 338 1083 L 346 1085 L 350 1085 L 358 1083 L 365 1078 L 372 1071 L 379 1061 ' +
  'L 386 1048 L 600 558 L 602 550 L 601 543 L 596 538 L 585 535 L 522 534 L 521 655 L 519 669 L 514 684 ' +
  'L 509 694 L 503 702 L 496 709 L 486 716 L 477 720 L 466 724 L 455 725 L 442 726 L 431 725 L 420 723 ' +
  'L 409 719 L 401 714 L 393 708 L 386 700 L 380 692 L 375 682 L 371 669 L 369 655 L 369 552 L 368 543 ' +
  'L 366 534 L 319 534 L 316 552 L 315 615 L 313 629 L 307 644 L 302 652 L 295 660 L 289 666 L 281 671 ' +
  'L 272 675 L 263 678 L 252 680 L 241 681 L 230 680 L 219 679 L 210 676 L 201 672 L 192 667 L 186 661 ' +
  'L 179 654 L 174 646 L 168 631 L 165 615 L 164 552 L 161 534 Z';
const SCOOP = 'M 8 400 C 8 160 170 6 342 6 C 514 6 676 160 676 400 Q 676 485 592 485 L 530 485 ' +
  'Q 485 485 485 545 L 485 648 Q 485 690 445 690 Q 405 690 405 648 L 405 555 Q 405 485 342 485 ' +
  'Q 280 485 280 555 L 280 603 Q 280 645 240 645 Q 200 645 200 603 L 200 555 Q 200 485 138 485 ' +
  'L 92 485 Q 8 485 8 400 Z';
// The cone's waffle: the windows of logo.png, traced from it. The ones at the top are cut by the
// band around the scoop's drips, as in the logo.
const WAFFLE = [
  '334,671 319,691 285,723 342,780 344,780 375,749 379,743 361,727 347,707 338,685 336,671',
  '204,712 211,729 213,729 224,717 207,712',
  '254,754 233,776 269,852 270,852 311,810 311,808 256,754',
  '419,760 372,810 419,858 421,858 457,776 447,766 421,760',
  '341,839 286,893 345,952 346,952 401,897 343,839',
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
