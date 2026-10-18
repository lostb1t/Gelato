// The setup assistant: a walk from an empty install to one that imports, saved in one go at the end.
// What it needs of the settings page (its libraries, how a pick becomes a folder) comes in `ctx`.

import { pluginId, errorText, checkAddon, renderAddonCard, drawLogos, applyThemeAccent } from './configurationpage?name=gelato-common.js';

const NEW_LIBRARY = 'new:';

// A walk from an empty install to one that imports: the addon, the libraries, the catalogs and
// the main choices, saved in one go at the end. It starts from what is saved, so it can be run
// again; what it does not ask about stays as it is.

const SETUP_KEY = 'gelato.setup.offered';

export function setupOfferedThisTab() {
  try { return sessionStorage.getItem(SETUP_KEY) === '1'; } catch (_) { return false; }
}

const SETUP_STEPS = ['welcome', 'addon', 'libraries', 'catalogs', 'choices', 'finish'];

// The choices the assistant asks about, with the setting each one is. "invert" for a setting
// that says the opposite of its switch.
const SETUP_CHOICES = [
  { key: 'DisableSearch', invert: true, title: 'Search AIOStreams from Jellyfin',
    text: 'A Jellyfin search also finds what is not in your libraries yet, ready to play.' },
  { key: 'FilterUnreleased', title: 'Hide titles that are not released yet',
    text: 'Keeps cinema-only and announced titles out of search and your libraries until they can be streamed.' },
  { key: 'PlaybackFailover', title: 'Try the next stream when one does not start',
    text: 'A stream known not to work is skipped for the next one, up to three. A version you pick yourself plays as it is.' },
  { key: 'RemuxDbEnabled', title: 'Look up media info on RemuxDB',
    text: 'Shows audio, subtitles and quality of a stream before you play it.' },
  { key: 'RemuxDbContribute', title: 'Contribute to RemuxDB',
    text: 'Shares that info for streams you play. Anonymous: no links, accounts or personal data.' },
  { key: 'LazyImages', title: 'Lazy images',
    text: 'Downloads artwork when it is first shown instead of at import, which saves a lot of storage.' },
  { key: 'EnableMixed', title: 'Streams for your own media',
    text: 'Titles Jellyfin found on disk get the addon\'s streams as further versions.' },
];

const SETUP_HTML = `
  <form method="dialog" novalidate>
    <div class="setupTop">
      <span class="gelatoLogo small" data-logo></span>
      <div class="setupTitle">Gelato setup</div>
      <button type="button" class="setupClose" data-do="close" title="Close"><span class="material-icons close" aria-hidden="true"></span></button>
    </div>
    <div class="setupRail" aria-hidden="true"></div>
    <div class="setupBody">
      <section class="setupStep" data-step="welcome">
        <div class="hero">
          <span class="gelatoLogo big" data-logo="animated"></span>
          <h2>Welcome to Gelato</h2>
          <p class="stepLead">Gelato puts what your AIOStreams addon offers into Jellyfin, as movies and
            series you browse, search and play like your own. A few steps and it is ready.</p>
        </div>
        <div class="heroPoints">
          <div><span class="material-icons travel_explore" aria-hidden="true"></span><b>Search everything</b>Find any title from Jellyfin's search and play it right away.</div>
          <div><span class="material-icons view_carousel" aria-hidden="true"></span><b>Import catalogs</b>Top lists, trending, your Trakt lists: as library items.</div>
          <div><span class="material-icons bolt" aria-hidden="true"></span><b>Stream on demand</b>Nothing is downloaded: streams are found when you press play.</div>
        </div>
      </section>

      <section class="setupStep" data-step="addon">
        <h2>Connect AIOStreams</h2>
        <p class="stepLead">Paste the manifest URL of your AIOStreams addon. You find it in AIOStreams after
          saving your config, under <b>Install</b>: copy the link instead of opening it.</p>
        <div class="inputContainer">
          <label class="inputLabel" for="txtSetupUrl">AIOStreams URL:</label>
          <div style="display: flex; gap: .6em; align-items: center;">
            <input type="text" id="txtSetupUrl" class="emby-input" style="flex: 1;" autocomplete="off" spellcheck="false" placeholder="https://…/manifest.json" />
            <button type="button" data-do="check" class="raised emby-button" style="margin: 0; white-space: nowrap;">Connect</button>
          </div>
          <div class="fieldDescription">Your server asks the addon, so it has to reach this address.</div>
        </div>
        <div data-addon></div>
        <div data-anyway style="display: none;" class="fieldDescription">
          <button type="button" class="linkButton" data-do="anyway">Use this URL anyway</button>
          — for an addon that is down for a moment.
        </div>
      </section>

      <section class="setupStep" data-step="libraries">
        <h2>Pick your libraries</h2>
        <p class="stepLead">Gelato's movies and series go into Jellyfin libraries. Use libraries you have —
          Gelato adds a folder of its own to them — or let it create new ones.</p>
        <div class="pickCard" data-kind="movie">
          <h3><span class="material-icons movie" aria-hidden="true"></span>Movies</h3>
          <div class="inputContainer">
            <select class="emby-select emby-select-withcolor" data-lib></select>
          </div>
          <div class="inputContainer" data-newname>
            <label class="inputLabel">Name of the new library:</label>
            <input type="text" class="emby-input" value="Movies" />
          </div>
        </div>
        <div class="pickCard" data-kind="series">
          <h3><span class="material-icons tv" aria-hidden="true"></span>Series</h3>
          <div class="inputContainer">
            <select class="emby-select emby-select-withcolor" data-lib></select>
          </div>
          <div class="inputContainer" data-newname>
            <label class="inputLabel">Name of the new library:</label>
            <input type="text" class="emby-input" value="Shows" />
          </div>
        </div>
      </section>

      <section class="setupStep" data-step="catalogs">
        <h2>Choose catalogs</h2>
        <p class="stepLead">A catalog is a list from your addon, like <i>Trending</i> or <i>Top rated</i>. The
          ones you pick are imported into your libraries and kept up to date. Search works without any.</p>
        <div data-catalogs></div>
        <div data-collections style="display: none;">
          <label class="choice">
            <span class="switch"><input type="checkbox" data-collection /><i></i></span>
            <span class="choiceText"><b>Also make a collection for each picked catalog</b><span>Its titles also
              show together as a Jellyfin collection named after the catalog. Per catalog on the Catalogs
              tab later.</span></span>
          </label>
        </div>
      </section>

      <section class="setupStep" data-step="choices">
        <h2>A few choices</h2>
        <p class="stepLead">Good defaults are on. Everything here can be changed later in the settings.</p>
        <div data-choices></div>
      </section>

      <section class="setupStep" data-step="finish">
        <div data-summary>
          <h2>Ready to go</h2>
          <p class="stepLead">This is what Gelato will set up.</p>
          <ul class="summary"></ul>
          <label class="choice" style="padding-top: 0;">
            <span class="switch"><input type="checkbox" data-import checked /><i></i></span>
            <span class="choiceText"><b>Import the picked catalogs now</b><span>Runs in the background; the
              titles show up in your libraries as they come in.</span></span>
          </label>
        </div>
        <div data-work style="display: none;">
          <h2>Setting up…</h2>
          <ul class="progressList"></ul>
          <div data-fail class="fieldDescription" style="color: #f44336;"></div>
        </div>
        <div data-done class="finale" style="display: none;">
          <div class="bigCheck"><span class="material-icons check" aria-hidden="true"></span></div>
          <h2>All set. Enjoy!</h2>
          <p class="stepLead" data-donetext></p>
          <p class="fieldDescription">Gelato set up the libraries for its titles, not their language, subtitle
            downloads or metadata order: those are under
            <a href="#/dashboard/libraries" class="button-link" data-do="libraries">Dashboard &gt; Libraries</a>.</p>
        </div>
      </section>
      <div class="sprinkles"></div>
    </div>
    <div class="setupFoot">
      <button type="button" data-do="back" class="raised emby-button">Back</button>
      <div class="footInfo" data-stepinfo></div>
      <button type="submit" data-do="next" class="raised button-submit emby-button">Next</button>
    </div>
  </form>`;

function sprinkle(container) {
  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
  const colors = ['#ff8fab', '#8ce0c2', '#fff1c9', '#00a4dc', '#ffd400', '#7c5cff', '#ff5c8a'];
  for (let i = 0; i < 70; i++) {
    const s = document.createElement('i');
    s.style.left = Math.random() * 100 + '%';
    s.style.background = colors[i % colors.length];
    s.style.animationDuration = (1.6 + Math.random() * 1.6) + 's';
    s.style.animationDelay = (Math.random() * .6) + 's';
    s.style.transform = 'rotate(' + Math.round(Math.random() * 180) + 'deg)';
    container.appendChild(s);
  }
  setTimeout(() => { container.innerHTML = ''; }, 4000);
}

let setupOpening = false;

export async function openSetup(ctx) {
  try { sessionStorage.setItem(SETUP_KEY, '1'); } catch (_) { /* private mode */ }
  if (setupOpening || document.querySelector('dialog.gelatoSetup')) return;
  setupOpening = true;
  let cfg;
  try {
    cfg = await window.ApiClient.getPluginConfiguration(pluginId);
  } finally {
    setupOpening = false;
  }
  // What the assistant has found out and picked so far.
  const state = {
    url: cfg.Url || '',
    addon: null,
    addonUrl: null,
    catalogs: new Set((cfg.Catalogs || []).filter(c => c.Enabled).map(c => c.Type + ':' + c.Id)),
    // On when a catalog that is imported already makes one: a run again starts from what is saved.
    collections: (cfg.Catalogs || []).some(c => c.Enabled && c.CreateCollection),
    choices: {},
  };
  SETUP_CHOICES.forEach(c => { state.choices[c.key] = c.invert ? !cfg[c.key] : !!cfg[c.key]; });

  const dialog = document.createElement('dialog');
  dialog.className = 'dialog gelatoSetup';
  // The dialog sits outside the page: it takes the theme's accent from the page's Save button.
  applyThemeAccent(dialog, ctx.page.querySelector('#btnSave'));
  dialog.innerHTML = SETUP_HTML;
  drawLogos(dialog);
  document.body.appendChild(dialog);

  const $ = sel => dialog.querySelector(sel);
  const stepEl = name => dialog.querySelector('.setupStep[data-step="' + name + '"]');
  const btnNext = $('[data-do="next"]');
  const btnBack = $('[data-do="back"]');
  const rail = $('.setupRail');
  SETUP_STEPS.forEach(() => rail.appendChild(document.createElement('span')));
  let current = 0;
  let busy = false;

  const close = () => {
    dialog.close();
    dialog.remove();
  };
  $('[data-do="close"]').onclick = close;
  $('[data-do="libraries"]').onclick = close;
  dialog.addEventListener('cancel', e => {
    // Escape closes, except while the setup saves.
    if (busy) e.preventDefault();
    else { e.preventDefault(); close(); }
  });

  // --- addon
  const txtSetupUrl = $('#txtSetupUrl');
  txtSetupUrl.value = state.url;
  const addonBox = $('[data-addon]');
  const anyway = $('[data-anyway]');
  async function connect() {
    const url = txtSetupUrl.value.trim();
    if (!url) {
      txtSetupUrl.focus();
      return;
    }
    const btn = $('[data-do="check"]');
    btn.disabled = true;
    btn.innerHTML = '<span class="gelatoSpin"></span>';
    anyway.style.display = 'none';
    try {
      const res = await checkAddon(url);
      renderAddonCard(addonBox, res);
      state.addon = res.Ok ? res : null;
      state.addonUrl = res.Ok ? url : null;
      anyway.style.display = res.Ok ? 'none' : '';
    } catch (e) {
      renderAddonCard(addonBox, null, await errorText(e));
      state.addon = null;
      state.addonUrl = null;
    } finally {
      btn.disabled = false;
      btn.textContent = 'Connect';
      updateFoot();
    }
  }
  $('[data-do="check"]').onclick = connect;
  txtSetupUrl.addEventListener('keydown', e => {
    if (e.key === 'Enter') {
      e.preventDefault();
      connect();
    }
  });
  txtSetupUrl.addEventListener('input', () => {
    if (state.addonUrl !== txtSetupUrl.value.trim()) {
      state.addonUrl = null;
      updateFoot();
    }
  });
  $('[data-do="anyway"]').onclick = () => {
    state.addonUrl = txtSetupUrl.value.trim();
    state.addon = null;
    go(1);
  };

  // --- libraries: the user's fitting libraries, the saved pick first, else a new one.
  async function fillLibraries() {
    await ctx.loadLibraries();
    dialog.querySelectorAll('.pickCard[data-kind]').forEach(card => {
      const kind = card.dataset.kind;
      const select = card.querySelector('[data-lib]');
      if (select.dataset.filled) return;
      select.dataset.filled = '1';
      const saved = ctx.libraryOf(kind === 'movie' ? cfg.MoviePath : cfg.SeriesPath);
      select.innerHTML = '';
      const add = (value, label) => {
        const o = document.createElement('option');
        o.value = value;
        o.textContent = label;
        select.appendChild(o);
      };
      ctx.libraries().filter(l => ctx.libraryFits(l, kind)).forEach(l => add('lib:' + l.Id, l.Name +
        (l.GelatoPath ? ' (has a Gelato folder)' : '')));
      add(NEW_LIBRARY, 'Create a new library');
      const ofGelato = ctx.libraries().find(l => ctx.libraryFits(l, kind) && l.GelatoPath);
      select.value = saved ? 'lib:' + saved.Id : (ofGelato ? 'lib:' + ofGelato.Id : NEW_LIBRARY);
      const newName = card.querySelector('[data-newname]');
      const sync = () => { newName.style.display = select.value === NEW_LIBRARY ? '' : 'none'; };
      select.addEventListener('change', sync);
      sync();
    });
  }

  // --- catalogs: from what the addon answered, grouped by type.
  function renderSetupCatalogs() {
    const box = $('[data-catalogs]');
    box.innerHTML = '';
    const importable = (state.addon?.Catalogs || []).filter(c => c.Importable);
    const collections = $('[data-collection]');
    collections.checked = state.collections;
    collections.onchange = () => { state.collections = collections.checked; };
    $('[data-collections]').style.display = importable.length ? '' : 'none';
    if (importable.length === 0) {
      box.innerHTML = '<p class="fieldDescription">' + (state.addon
        ? 'Your addon lists no catalogs to import. Search works all the same.'
        : 'The addon was not reached, so its catalogs are not known yet. Pick them later on the Catalogs tab.') +
        '</p>';
      return;
    }
    const types = [...new Set(importable.map(c => c.Type))];
    types.forEach(type => {
      const group = document.createElement('div');
      group.className = 'catalogGroup';
      const head = document.createElement('div');
      head.className = 'groupHead';
      const title = document.createElement('b');
      // AIOMetadata's "all" lists hold movies and series both.
      title.textContent = { all: 'Movies and series', movie: 'Movies', series: 'Series' }[type] ||
        type.charAt(0).toUpperCase() + type.slice(1);
      const count = document.createElement('span');
      const toggleAll = document.createElement('button');
      toggleAll.type = 'button';
      toggleAll.className = 'linkButton';
      head.append(title, count, toggleAll);
      const pills = document.createElement('div');
      pills.className = 'catalogPills';
      const ofType = importable.filter(c => c.Type === type);
      const refresh = () => {
        const on = ofType.filter(c => state.catalogs.has(c.Type + ':' + c.Id)).length;
        count.textContent = on + ' of ' + ofType.length;
        toggleAll.textContent = on === ofType.length ? 'None' : 'All';
        updateFoot();
      };
      ofType.forEach(c => {
        const key = c.Type + ':' + c.Id;
        const pill = document.createElement('button');
        pill.type = 'button';
        pill.className = 'catalogPill';
        pill.setAttribute('aria-pressed', state.catalogs.has(key) ? 'true' : 'false');
        // A mark for each state, so a picked catalog shows without its colour: a theme's accent can
        // be about as grey as the pill.
        pill.innerHTML = '<span class="material-icons pillOff close" aria-hidden="true"></span>' +
          '<span class="material-icons pillOn check_circle" aria-hidden="true"></span>';
        // As text: the name comes from the addon.
        pill.append(c.Name || c.Id);
        pill.onclick = () => {
          if (state.catalogs.has(key)) state.catalogs.delete(key);
          else state.catalogs.add(key);
          pill.setAttribute('aria-pressed', state.catalogs.has(key) ? 'true' : 'false');
          refresh();
        };
        pills.appendChild(pill);
      });
      toggleAll.onclick = () => {
        const all = ofType.every(c => state.catalogs.has(c.Type + ':' + c.Id));
        ofType.forEach(c => all ? state.catalogs.delete(c.Type + ':' + c.Id) : state.catalogs.add(c.Type + ':' + c.Id));
        pills.querySelectorAll('.catalogPill').forEach((p, i) => p.setAttribute('aria-pressed',
          state.catalogs.has(ofType[i].Type + ':' + ofType[i].Id) ? 'true' : 'false'));
        refresh();
      };
      group.append(head, pills);
      box.appendChild(group);
      refresh();
    });
  }

  // --- choices
  function renderChoices() {
    const box = $('[data-choices]');
    if (box.childElementCount) return;
    SETUP_CHOICES.forEach(c => {
      const row = document.createElement('label');
      row.className = 'choice';
      row.innerHTML = '<span class="switch"><input type="checkbox" /><i></i></span>' +
        '<span class="choiceText"><b></b><span></span></span>';
      row.querySelector('b').textContent = c.title;
      row.querySelector('.choiceText > span').textContent = c.text;
      const input = row.querySelector('input');
      input.checked = state.choices[c.key];
      input.onchange = () => { state.choices[c.key] = input.checked; };
      box.appendChild(row);
    });
  }

  function libraryPlan(kind) {
    const card = dialog.querySelector('.pickCard[data-kind="' + kind + '"]');
    const value = card.querySelector('[data-lib]').value;
    if (value === NEW_LIBRARY) {
      return { create: true, name: card.querySelector('[data-newname] input').value.trim() || (kind === 'movie' ? 'Movies' : 'Shows') };
    }
    const lib = ctx.libraries().find(l => 'lib:' + l.Id === value);
    return { value, name: lib ? lib.Name : value };
  }

  function pickedCatalogs() {
    return (state.addon?.Catalogs || []).filter(c => c.Importable && state.catalogs.has(c.Type + ':' + c.Id));
  }

  function renderSummary() {
    const list = $('.summary');
    list.innerHTML = '';
    const item = (icon, title, detail) => {
      const li = document.createElement('li');
      li.innerHTML = '<span class="material-icons ' + icon + '" aria-hidden="true"></span><div><b></b><small></small></div>';
      li.querySelector('b').textContent = title;
      li.querySelector('small').textContent = detail;
      list.appendChild(li);
    };
    item('extension', state.addon ? (state.addon.Name || 'AIOStreams') : 'AIOStreams',
      state.addon ? 'Connected, ' + state.addon.Milliseconds + ' ms' : 'Not checked');
    const lib = kind => {
      const p = libraryPlan(kind);
      return p.create ? 'New library "' + p.name + '"' : p.name;
    };
    item('video_library', 'Libraries', 'Movies: ' + lib('movie') + ' · Series: ' + lib('series'));
    const cats = pickedCatalogs();
    item('view_carousel', cats.length === 1 ? '1 catalog' : cats.length + ' catalogs',
      cats.length ? cats.map(c => c.Name).join(', ') + (state.collections ? ', each as a collection too' : '')
        : 'None for now: search only');
    const on = SETUP_CHOICES.filter(c => state.choices[c.key]).map(c => c.title);
    item('tune', 'Choices', on.length ? on.join(', ') : 'All off');
    $('[data-import]').closest('label').style.display = cats.length ? '' : 'none';
  }

  // --- the save: libraries first (a new one needs its folder, Gelato's folder in one of the
  // user's is created on demand), then the configuration, then the import.
  async function finish() {
    busy = true;
    btnNext.disabled = true;
    btnBack.style.visibility = 'hidden';
    $('[data-summary]').style.display = 'none';
    $('[data-work]').style.display = '';
    const list = $('.progressList');
    const tasks = [];
    const task = label => {
      const li = document.createElement('li');
      li.innerHTML = '<span class="mark"></span><span></span>';
      li.lastChild.textContent = label;
      list.appendChild(li);
      const t = {
        start() { li.className = 'busy'; li.firstChild.innerHTML = '<span class="gelatoSpin"></span>'; },
        done() { li.className = 'done'; li.firstChild.innerHTML = '<span class="material-icons check_circle"></span>'; },
        fail() { li.className = 'failed'; li.firstChild.innerHTML = '<span class="material-icons error"></span>'; },
      };
      tasks.push(t);
      return t;
    };
    const cats = pickedCatalogs();
    const doImport = cats.length > 0 && $('[data-import]').checked;
    const tLibs = task('Preparing the libraries');
    const tSave = task('Saving the settings');
    const tImport = doImport ? task('Starting the import of ' + (cats.length === 1 ? '1 catalog' : cats.length + ' catalogs')) : null;
    let running = tLibs;
    try {
      tLibs.start();
      const saved = await window.ApiClient.getPluginConfiguration(pluginId);
      const created = [];
      const pathFor = async (kind, previous) => {
        const plan = libraryPlan(kind);
        if (!plan.create) return ctx.resolveLibraryPick(plan.value, previous);
        const folder = await ctx.createLibraryFolder(plan.name);
        await window.ApiClient.ajax({
          type: 'POST',
          url: window.ApiClient.getUrl('Library/VirtualFolders', {
            name: plan.name,
            collectionType: kind === 'movie' ? 'movies' : 'tvshows',
            paths: folder,
            refreshLibrary: false
          }),
          // Gelato writes the folder itself: there is nothing for a file watcher to find.
          data: JSON.stringify({ LibraryOptions: { EnableRealtimeMonitor: false } }),
          contentType: 'application/json'
        });
        created.push(plan.name);
        return folder;
      };
      saved.MoviePath = await pathFor('movie', saved.MoviePath);
      saved.SeriesPath = await pathFor('series', saved.SeriesPath);
      tLibs.done();

      running = tSave;
      tSave.start();
      saved.Url = state.addonUrl || txtSetupUrl.value.trim();
      SETUP_CHOICES.forEach(c => { saved[c.key] = c.invert ? !state.choices[c.key] : state.choices[c.key]; });
      if (state.addon) {
        // The addon's catalogs as picked; one it does not list keeps what it had.
        saved.Catalogs = saved.Catalogs || [];
        state.addon.Catalogs.filter(c => c.Importable).forEach(c => {
          let entry = saved.Catalogs.find(s => s.Id === c.Id && s.Type === c.Type);
          if (!entry) {
            entry = { Id: c.Id, Type: c.Type, Name: c.Name, Enabled: false, MaxItems: 0, CreateCollection: false, Url: '', Path: '' };
            saved.Catalogs.push(entry);
          }
          entry.Name = c.Name;
          entry.Enabled = state.catalogs.has(c.Type + ':' + c.Id);
          // The switch speaks for the picked ones; one left out keeps its own setting.
          if (entry.Enabled) entry.CreateCollection = state.collections;
        });
      }
      await window.ApiClient.updatePluginConfiguration(pluginId, saved);
      if (created.length) {
        // The new libraries get their first scan, which also lists Gelato's folder in them.
        await window.ApiClient.ajax({ type: 'POST', url: window.ApiClient.getUrl('Library/Refresh') });
      }
      tSave.done();

      if (tImport) {
        running = tImport;
        tImport.start();
        await window.ApiClient.ajax({ type: 'POST', url: window.ApiClient.getUrl('gelato/catalogs/import-all') });
        tImport.done();
      }

      await new Promise(r => setTimeout(r, 450));
      $('[data-work]').style.display = 'none';
      $('[data-done]').style.display = '';
      $('[data-donetext]').textContent = doImport
        ? 'The import runs in the background: titles show up in your libraries as they come in. Try a search meanwhile.'
        : 'Try a search: what your addon finds can be opened and played right away.';
      btnNext.textContent = 'Close';
      btnNext.disabled = false;
      sprinkle($('.sprinkles'));
      rail.querySelectorAll('span').forEach(s => s.classList.add('done'));
      current = SETUP_STEPS.length;
      // The page behind shows what was saved.
      ctx.saved();
    } catch (e) {
      console.error(e);
      running.fail();
      $('[data-fail]').textContent = 'That did not work: ' + await errorText(e) +
        '. Nothing after this step was done; go back to change something, or try again.';
      btnBack.style.visibility = '';
      btnNext.textContent = 'Try again';
      btnNext.disabled = false;
    } finally {
      busy = false;
    }
  }

  function updateFoot() {
    const name = SETUP_STEPS[current];
    btnBack.style.visibility = current === 0 ? 'hidden' : '';
    btnNext.textContent = name === 'welcome' ? 'Let\'s go' : name === 'finish' ? 'Finish setup' : 'Next';
    btnNext.disabled = name === 'addon' && !state.addonUrl;
    $('[data-stepinfo]').textContent = current > 0 && current < SETUP_STEPS.length - 1
      ? 'Step ' + current + ' of ' + (SETUP_STEPS.length - 2) : '';
    rail.querySelectorAll('span').forEach((s, i) => s.classList.toggle('done', i <= current));
  }

  function show(index, back) {
    current = index;
    const name = SETUP_STEPS[index];
    dialog.querySelectorAll('.setupStep').forEach(s => s.classList.remove('current', 'back'));
    const el = stepEl(name);
    el.classList.add('current');
    if (back) el.classList.add('back');
    $('.setupBody').scrollTop = 0;
    if (name === 'addon') setTimeout(() => txtSetupUrl.focus(), 50);
    if (name === 'libraries') fillLibraries();
    if (name === 'catalogs') renderSetupCatalogs();
    if (name === 'choices') renderChoices();
    if (name === 'finish') {
      $('[data-summary]').style.display = '';
      $('[data-work]').style.display = 'none';
      $('[data-done]').style.display = 'none';
      $('.progressList').innerHTML = '';
      $('[data-fail]').textContent = '';
      renderSummary();
    }
    updateFoot();
  }

  function go(delta) {
    const next = current + delta;
    if (next < 0 || next >= SETUP_STEPS.length) return;
    show(next, delta < 0);
  }

  dialog.querySelector('form').addEventListener('submit', async e => {
    e.preventDefault();
    if (busy || btnNext.disabled) return;
    if (current >= SETUP_STEPS.length) {
      close();
      return;
    }
    const name = SETUP_STEPS[current];
    if (name === 'addon' && !state.addonUrl) {
      await connect();
      if (state.addonUrl) go(1);
      return;
    }
    if (name === 'finish') {
      await finish();
      return;
    }
    go(1);
  });
  btnBack.onclick = () => go(-1);

  // A URL saved already is checked right away: the catalogs step needs what the addon lists.
  if (state.url) {
    connect();
  }
  dialog.showModal();
  show(0);
}
