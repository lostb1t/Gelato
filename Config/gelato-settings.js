// The settings page: its tabs, the libraries and catalogs, the save. Jellyfin loads it as the page's
// controller (data-controller in config.html) and calls the default export with the page.

import { pluginId, errorText, checkAddon, renderAddonCard, drawLogos, applyThemeAccent } from './configurationpage?name=gelato-common.js';
import { openSetup, setupOfferedThisTab } from './configurationpage?name=gelato-setup.js';

export default function (view) {
  const guidRegex = /^[0-9a-f]{32}$/i;
  const page = view;
  const form = page.querySelector('#gelatoForm');

  // Tab Logic
  const tabs = page.querySelectorAll('.tab-button');
  const tabContents = page.querySelectorAll('.tab-content');
  const tabInk = page.querySelector('.tabInk');
  // Which tab was open, for the next visit of the page in this browser tab.
  const TAB_KEY = 'gelato.settings.tab';

  function moveInk(tab) {
    if (!tab.offsetWidth) return;
    tabInk.style.width = tab.offsetWidth + 'px';
    tabInk.style.transform = 'translateX(' + tab.offsetLeft + 'px)';
  }

  function openTab(name) {
    const tab = page.querySelector('.tab-button[data-tab="' + name + '"]') || tabs[0];
    // The class alone marks the open tab: the page's style does the rest.
    tabs.forEach(t => {
      t.classList.toggle('active', t === tab);
      t.setAttribute('aria-selected', t === tab ? 'true' : 'false');
    });
    tabContents.forEach(c => {
      const shown = c.id === 'tab-' + tab.dataset.tab;
      c.style.display = '';
      c.classList.toggle('shown', shown);
    });
    // Again on every switch: a skin's stylesheet may apply after the page has opened.
    applyThemeAccent(page, page.querySelector('#btnSave'));
    moveInk(tab);
    tab.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    try { sessionStorage.setItem(TAB_KEY, tab.dataset.tab); } catch (_) { /* private mode */ }

    // Lazy load catalogs if switching to catalogs tab
    if (tab.dataset.tab === 'catalogs') {
      // Always refresh display on switch
      renderCatalogs();
      if (allCatalogs.length === 0) loadCatalogs();
    }
  }

  tabs.forEach(tab => tab.addEventListener('click', () => openTab(tab.dataset.tab)));
  window.addEventListener('resize', () => moveInk(page.querySelector('.tab-button.active')));

  // Elements
  const txtUrl = page.querySelector('#txtUrl');
  const txtBasePath = page.querySelector('#txtBasePath');
  const selMovieLibrary = page.querySelector('#selMovieLibrary');
  const selSeriesLibrary = page.querySelector('#selSeriesLibrary');

  const txtStreamTTL = page.querySelector('#txtStreamTTL');
  const chkEnableMixed = page.querySelector('#chkEnableMixed');
  const chkExtendLocalSeriesTrees = page.querySelector('#chkExtendLocalSeriesTrees');
  const chkAddonSearch = page.querySelector('#chkAddonSearch');
  const chkFilterUnreleased = page.querySelector('#chkFilterUnreleased');
  const txtBufferDays = page.querySelector('#txtBufferDays');
  const txtFFmpegAnalyzeDuration = page.querySelector('#txtFFmpegAnalyzeDuration');
  const txtFFmpegProbeSize = page.querySelector('#txtFFmpegProbeSize');
  const chkEnableJavaScriptInjection = page.querySelector('#chkEnableJavaScriptInjection');
  const chkLazyImages = page.querySelector('#chkLazyImages');
  const chkPersonRemoteTitles = page.querySelector('#chkPersonRemoteTitles');
  const chkHideUnplayedSearchTitles = page.querySelector('#chkHideUnplayedSearchTitles');
  const chkPreProbe = page.querySelector('#chkPreProbe');
  const chkPlaybackFailover = page.querySelector('#chkPlaybackFailover');
  const chkRemuxDbEnabled = page.querySelector('#chkRemuxDbEnabled');
  const chkRemuxDbContribute = page.querySelector('#chkRemuxDbContribute');

  // Catalogs
  const catalogsList = page.querySelector('#catalogsList');
  const txtCatalogSearch = page.querySelector('#txtCatalogSearch');
  const btnRefreshCatalogs = page.querySelector('#btnRefreshCatalogs');
  let allCatalogs = [];
  // Whether the addon's list was fetched: an empty table then means it lists none.
  let catalogsLoaded = false;

  // Overrides
  const selUserConfig = page.querySelector('#selUserConfig');
  const userOverrideContainer = page.querySelector('#userOverrideContainer');
  const userOverridesList = page.querySelector('#userOverridesList');
  const txtUserUrl = page.querySelector('#txtUserUrl');
  const selUserMovieLibrary = page.querySelector('#selUserMovieLibrary');
  const selUserSeriesLibrary = page.querySelector('#selUserSeriesLibrary');
  const chkUserAddonSearch = page.querySelector('#chkUserAddonSearch');
  const chkUserPersonRemoteTitles = page.querySelector('#chkUserPersonRemoteTitles');

  // The save bar
  const saveBar = page.querySelector('.gelatoSaveBar');
  const saveStateText = page.querySelector('#saveStateText');

  let currentConfig = null;
  let users = [];

  // The save bar comes up while something is not saved, and says "Saved" for a moment after a save.
  let savedTimer = 0;
  function markDirty(dirty, saved) {
    clearTimeout(savedTimer);
    saveBar.classList.toggle('dirty', dirty);
    saveBar.classList.toggle('saved', !dirty && !!saved);
    saveStateText.textContent = dirty ? 'Unsaved changes' : 'Saved';
    if (saved) savedTimer = setTimeout(() => saveBar.classList.remove('saved'), 1800);
  }

  function userConfigsArrayToObject(arr) {
    const obj = {};
    (arr || []).forEach(uc => {
      if (uc.UserId && guidRegex.test(uc.UserId)) {
        obj[uc.UserId] = {
          Url: uc.Url,
          MoviePath: uc.MoviePath,
          SeriesPath: uc.SeriesPath,
          DisableSearch: uc.DisableSearch,
          PersonRemoteTitles: uc.PersonRemoteTitles ?? true,
        };
      }
    });
    return obj;
  }
  // Libraries: a pick is a library, what gets saved is Gelato's folder in it. A select's value is
  // 'lib:<id>', 'path:<path>' for a folder no library holds (yet), '' for a catalog's default, or
  // 'new:' while the folder for a new library is being asked for.
  let libraries = [];
  const LIBRARY_KINDS = { movie: ['movies', 'mixed'], series: ['tvshows', 'mixed'] };
  const NEW_LIBRARY = 'new:';

  // A dialog of this page: in the page only while it is open, and with a form of its own, so
  // Enter answers the dialog and saves nothing. Plain elements, Jellyfin's classes theme them.
  function openDialog(html) {
    const dialog = document.createElement('dialog');
    dialog.className = 'dialog';
    dialog.style.cssText = 'margin: auto; border: 0; border-radius: .3em; padding: 1.5em; color: inherit; ' +
      'width: 90%; max-width: 30em;';
    dialog.innerHTML = html;
    page.appendChild(dialog);
    dialog.showModal();
    return dialog;
  }

  // Asks before something is started; resolves to whether it was confirmed.
  function askConfirm(title, text, okLabel) {
    return new Promise(resolve => {
      const dialog = openDialog(`
        <form>
          <h2 style="margin-top: 0;"></h2>
          <div data-text></div>
          <div style="display: flex; gap: 1em; justify-content: flex-end; margin-top: 1.5em;">
            <button type="button" class="raised emby-button">Cancel</button>
            <button type="submit" class="raised button-submit emby-button"></button>
          </div>
        </form>`);
      // As text: a catalog's name comes from the addon.
      dialog.querySelector('h2').textContent = title;
      dialog.querySelector('[data-text]').textContent = text;
      const btnOk = dialog.querySelector('button[type="submit"]');
      btnOk.textContent = okLabel;

      const done = ok => {
        dialog.remove();
        resolve(ok);
      };
      dialog.querySelector('form').onsubmit = e => {
        e.preventDefault();
        done(true);
      };
      dialog.querySelector('button[type="button"]').onclick = () => done(false);
      // Escape
      dialog.oncancel = e => {
        e.preventDefault();
        done(false);
      };
      btnOk.focus();
    });
  }

  const NEW_LIBRARY_DIALOG = `
    <form>
      <h2 style="margin-top: 0;">New library</h2>
      <div data-step="ask">
        <label class="inputLabel" for="txtNewLibraryName">Library name:</label>
        <input type="text" id="txtNewLibraryName" class="emby-input" />
        <div class="fieldDescription">Jellyfin needs a folder for a new library, so Gelato creates the
          folder first. You add the library with it in the next step.</div>
      </div>
      <div data-step="ready" style="display: none;">
        <label class="inputLabel" for="txtNewLibraryPath">Folder for the library:</label>
        <div style="display: flex; gap: .5em; align-items: center;">
          <input type="text" id="txtNewLibraryPath" class="emby-input" readonly style="flex: 1;" />
          <button type="button" data-do="copy" class="raised emby-button">Copy</button>
        </div>
        <ol style="padding-left: 1.2em;">
          <li>Select <b>Save</b> on this page.</li>
          <li>In <b>Dashboard &gt; Libraries</b>, add a library with this folder.</li>
        </ol>
        <div class="fieldDescription">Once the library exists, it shows up here under its name.</div>
      </div>
      <div data-step="error" class="fieldDescription" style="color: #f44336; display: none;"></div>
      <div style="display: flex; gap: 1em; justify-content: flex-end; margin-top: 1em;">
        <button type="button" data-do="cancel" class="raised emby-button">Cancel</button>
        <button type="submit" class="raised button-submit emby-button">Create folder</button>
      </div>
    </form>`;

  async function showError(what, e) {
    Dashboard.alert(what + ': ' + await errorText(e));
  }

  // The libraries as they are with this Gelato folder. The server refuses one that is not a
  // full path, which is how a save finds out.
  function fetchLibraries(base) {
    return window.ApiClient.getJSON(
      window.ApiClient.getUrl('gelato/libraries', base ? { basePath: base } : {}));
  }

  async function loadLibraries() {
    try {
      const base = txtBasePath.value.trim();
      // A folder saved by an earlier version may be refused: list with the default then.
      const res = await fetchLibraries(base).catch(e => base ? fetchLibraries('') : Promise.reject(e)) || {};
      libraries = res.Libraries || [];
      txtBasePath.placeholder = res.DefaultBasePath || '';
    } catch (e) {
      console.error('Error loading libraries:', e);
      libraries = [];
    }
  }

  function libraryFits(lib, kind) {
    const type = (lib.CollectionType || 'mixed').toLowerCase();
    const kinds = LIBRARY_KINDS[kind] || [...LIBRARY_KINDS.movie, ...LIBRARY_KINDS.series];
    return kinds.includes(type);
  }

  function libraryOf(path) {
    return path ? libraries.find(l => (l.Locations || []).includes(path)) || null : null;
  }

  // The library a folder lies inside without being one of its folders: a path typed before
  // the pickers could be any folder below a library's, and Jellyfin lists what is in it.
  function libraryAround(path) {
    const norm = p => (p || '').replace(/\\/g, '/').replace(/\/+$/, '');
    const p = norm(path);
    if (!p) return null;
    return libraries.find(l => (l.Locations || []).some(loc => norm(loc) && p.startsWith(norm(loc) + '/'))) || null;
  }

  // A person's titles outside the library are remote requests too: none while searches stay local.
  function showPersonRemoteTitles() {
    const off = !chkAddonSearch.checked;
    chkPersonRemoteTitles.disabled = off;
    page.querySelector('#personRemoteTitlesSetting').classList.toggle('gelatoNested-off', off);
    chkHideUnplayedSearchTitles.disabled = off;
    page.querySelector('#hideUnplayedSearchTitlesSetting').classList.toggle('gelatoNested-off', off);
  }

  function showUserPersonRemoteTitles() {
    const off = !chkUserAddonSearch.checked;
    chkUserPersonRemoteTitles.disabled = off;
    page.querySelector('#userPersonRemoteTitlesSetting').classList.toggle('gelatoNested-off', off);
  }

  // Said on the Catalogs tab: an import that seems to add nothing is the filter at work.
  function showUnreleasedHint() {
    page.querySelector('#unreleasedHint').style.display = chkFilterUnreleased.checked ? '' : 'none';
  }

  // A folder no library holds. A catalog's select is narrow, and the folder's own name is what
  // tells two of them apart, whatever the Gelato folder is: it comes first there, the tooltip
  // has the full path.
  function noLibraryLabel(select, path) {
    const around = libraryAround(path);
    if (select.dataset.field !== 'Path') {
      return around ? path + ' (in ' + around.Name + ')' : 'In no library: ' + path;
    }
    const name = path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path;
    return name + (around ? ' (in ' + around.Name + ')' : ' (in no library)');
  }

  function fillLibrarySelect(select, kind, path, defaultLabel) {
    select.innerHTML = '';
    const add = (value, label) => {
      const o = document.createElement('option');
      o.value = value;
      o.textContent = label;
      select.appendChild(o);
    };
    const current = libraryOf(path);
    if (defaultLabel) add('', defaultLabel);
    if (path && !current) add('path:' + path, noLibraryLabel(select, path));
    if (!defaultLabel && !path) add('', '-- Select library --');
    libraries.filter(l => l === current || libraryFits(l, kind)).forEach(l => add('lib:' + l.Id, l.Name));
    add(NEW_LIBRARY, 'New library…');
    select.value = current ? 'lib:' + current.Id : (path ? 'path:' + path : '');
    select.dataset.picked = select.value;
    showLibraryWarning(select);
    if (!select.dataset.warns) {
      select.dataset.warns = '1';
      select.addEventListener('change', () => {
        if (select.value === NEW_LIBRARY) {
          newLibraryFor(select);
          return;
        }
        select.dataset.picked = select.value;
        showLibraryWarning(select);
      });
    }
  }

  // Jellyfin's dialog takes no library without a folder, and only a folder that exists: Gelato
  // creates the folder, the user the library with it. Resolves to the folder, or null when
  // cancelled.
  function prepareLibraryFolder() {
    return new Promise(resolve => {
      let path = null;
      const dialog = openDialog(NEW_LIBRARY_DIALOG);

      const step = name => dialog.querySelector('[data-step="' + name + '"]');
      const txtName = dialog.querySelector('#txtNewLibraryName');
      const txtPath = dialog.querySelector('#txtNewLibraryPath');
      const btnOk = dialog.querySelector('button[type="submit"]');
      const btnCancel = dialog.querySelector('[data-do="cancel"]');
      const fail = text => {
        step('error').textContent = text;
        step('error').style.display = text ? '' : 'none';
      };
      const done = () => {
        dialog.remove();
        resolve(path);
      };

      dialog.querySelector('form').onsubmit = async e => {
        e.preventDefault();
        if (path) {
          done();
          return;
        }
        const name = txtName.value.trim();
        if (!name) {
          fail('Enter a name for the library.');
          txtName.focus();
          return;
        }
        try {
          path = await createLibraryFolder(name);
          txtPath.value = path;
          step('ask').style.display = 'none';
          step('ready').style.display = '';
          btnCancel.style.display = 'none';
          btnOk.textContent = 'Done';
          fail('');
          txtPath.select();
        } catch (err) {
          console.error(err);
          // Said in the dialog: an alert would open behind it.
          fail('Couldn\'t create the folder: ' + await errorText(err));
        }
      };
      dialog.querySelector('[data-do="copy"]').onclick = async () => {
        txtPath.select();
        try {
          // Only on https and localhost.
          await navigator.clipboard.writeText(txtPath.value);
        } catch (_) {
          document.execCommand('copy');
        }
      };
      btnCancel.onclick = done;
      // Escape
      dialog.oncancel = e => {
        e.preventDefault();
        done();
      };
      txtName.focus();
    });
  }

  // A folder of its own for a library that is about to be created, in the Gelato folder.
  async function createLibraryFolder(name) {
    const base = txtBasePath.value.trim();
    const res = await window.ApiClient.ajax({
      type: 'POST',
      url: window.ApiClient.getUrl('gelato/libraries/folder', base ? { name, basePath: base } : { name }),
      dataType: 'json'
    });
    return res.Path;
  }

  // "New library…" was picked: picks the folder made for it, or puts the pick back.
  async function newLibraryFor(select) {
    const path = await prepareLibraryFolder();
    let value = select.dataset.picked || '';
    if (path) {
      value = 'path:' + path;
      if (![...select.options].some(o => o.value === value)) {
        const o = document.createElement('option');
        o.value = value;
        o.textContent = noLibraryLabel(select, path);
        select.insertBefore(o, [...select.options].find(x => x.value === NEW_LIBRARY) || null);
      }
    }
    select.value = value;
    // The row keeps its pick through this event.
    select.dispatchEvent(new Event('change'));
  }

  // A folder no Jellyfin library holds: Gelato has nowhere to put the items. Said under the
  // select; in the catalog table, where there is no room, as the tooltip.
  const NO_LIBRARY_WARNING = 'This folder isn\'t in a Jellyfin library yet, so these titles don\'t show up anywhere. ' +
    'Add a library with this folder in Dashboard > Libraries, or pick one of your libraries and save: ' +
    'Gelato adds a folder of its own to it.';

  function showLibraryWarning(select) {
    // A folder inside a library's folder is in that library: nothing to add.
    const missing = select.value.startsWith('path:') && !libraryAround(select.value.slice(5));
    const folder = missing ? select.value.slice(5) : '';
    select.title = missing ? folder + ': ' + NO_LIBRARY_WARNING : '';
    // A catalog's select has its table cell to itself, the others their field.
    const inTable = select.dataset.field === 'Path';
    const container = inTable ? select.parentElement : select.closest('.inputContainer');
    if (!container) return;

    // The folder is what the library is created with in Jellyfin: one click copies it.
    const copied = (control, label) => async e => {
      e.preventDefault();
      await copyText(select.value.slice(5));
      control.textContent = 'Copied';
      setTimeout(() => { control.textContent = label; }, 1500);
    };
    const pick = select.closest('.libraryPick');
    const copy = pick?.querySelector('button');
    if (copy) {
      copy.style.display = missing ? '' : 'none';
      copy.onclick = copied(copy, 'Copy folder');
    }

    let note = container.querySelector(':scope > .libraryWarning');
    if (!note) {
      note = document.createElement('div');
      note.className = 'fieldDescription libraryWarning';
      (pick || select).insertAdjacentElement('afterend', note);
      const link = document.createElement('a');
      link.href = '#/dashboard/libraries';
      // A tab of its own: the folder is pasted there, and this page keeps what is picked.
      link.target = '_blank';
      link.className = 'button-link';
      if (inTable) {
        // One line, where the cell has no room for more: the select's tooltip says the rest.
        // Not a fieldDescription: a skin may force those to wrap.
        note.className = 'libraryWarning';
        note.style.cssText = 'white-space: nowrap; font-size: .85em; margin: .3em 0 0;';
        const copyLink = document.createElement('a');
        copyLink.href = '#';
        copyLink.className = 'button-link';
        copyLink.textContent = 'Copy folder';
        copyLink.onclick = copied(copyLink, 'Copy folder');
        link.textContent = 'Libraries';
        note.append('No library yet: ', copyLink, ' · ', link);
      } else {
        link.textContent = 'Dashboard > Libraries';
        note.append('This folder isn\'t in a library yet. Copy it and add a library with it in ', link,
          ', or pick one of your libraries here. Gelato finds the new library on its own.');
      }
    }
    // On an install without any library of Gelato's this is the first step, not a fault.
    note.style.color = libraries.some(l => l.GelatoPath) ? '#ff9800' : '';
    // A catalog that is switched off imports nothing: its row says nothing either.
    note.style.display = missing && !(inTable && select.disabled) ? '' : 'none';
  }

  async function copyText(text) {
    try {
      // Only on https and localhost.
      await navigator.clipboard.writeText(text);
    } catch (_) {
      const area = document.createElement('textarea');
      area.value = text;
      area.style.cssText = 'position: fixed; opacity: 0;';
      page.appendChild(area);
      area.select();
      document.execCommand('copy');
      area.remove();
    }
  }

  // A select filled anew keeps what was picked in it, also a folder made for a new library
  // that is not saved yet; a folder that got its library meanwhile shows as that library.
  function restorePick(select, picked) {
    if (picked === undefined || picked === NEW_LIBRARY || picked === select.value) return true;
    const has = value => [...select.options].some(o => o.value === value);
    if (picked.startsWith('path:')) {
      const lib = libraryOf(picked.slice(5));
      if (lib) {
        picked = 'lib:' + lib.Id;
      } else if (!has(picked)) {
        const o = document.createElement('option');
        o.value = picked;
        o.textContent = noLibraryLabel(select, picked.slice(5));
        select.insertBefore(o, [...select.options].find(x => x.value === NEW_LIBRARY) || null);
      }
    }
    if (!has(picked)) return false;
    select.value = picked;
    select.dataset.picked = picked;
    showLibraryWarning(select);
    return true;
  }

  // Back from the Libraries tab: what was created there shows without a reload.
  async function refreshLibraries() {
    if (!currentConfig || document.hidden || !page.isConnected || page.classList.contains('hide')
      || page.querySelector('dialog') || document.querySelector('dialog.gelatoSetup')) return;
    const movie = selMovieLibrary.value, series = selSeriesLibrary.value;
    if (allCatalogs.length > 0) collectCatalogStatesFromDOM();
    await loadLibraries();
    fillLibrarySelect(selMovieLibrary, 'movie', currentConfig.MoviePath || '');
    restorePick(selMovieLibrary, movie);
    fillLibrarySelect(selSeriesLibrary, 'series', currentConfig.SeriesPath || '');
    restorePick(selSeriesLibrary, series);
    if (allCatalogs.length > 0) renderCatalogs();
  }

  // The folder to save for a pick: the one saved already when the library holds it, the library's
  // Gelato folder, or a new one Gelato creates and adds to the library.
  async function resolveLibraryPick(value, path) {
    if (!value) return '';
    if (value.startsWith('path:')) return value.slice(5);
    const lib = libraries.find(l => 'lib:' + l.Id === value);
    if (!lib) return path || '';
    if (path && (lib.Locations || []).includes(path)) return path;
    if (lib.GelatoPath) return lib.GelatoPath;
    const base = txtBasePath.value.trim();
    const res = await window.ApiClient.ajax({
      type: 'POST',
      url: window.ApiClient.getUrl('gelato/libraries/' + encodeURIComponent(lib.Id) + '/folder',
        base ? { basePath: base } : {}),
      dataType: 'json'
    });
    lib.GelatoPath = res.Path;
    lib.Locations = [...(lib.Locations || []), res.Path];
    return res.Path;
  }

  async function testAddonUrl() {
    const btn = page.querySelector('#btnCheckAddon');
    const status = page.querySelector('#addonStatus');
    btn.disabled = true;
    btn.innerHTML = '<span class="gelatoSpin"></span>';
    try {
      renderAddonCard(status, await checkAddon(txtUrl.value.trim()));
    } catch (e) {
      renderAddonCard(status, null, await errorText(e));
    } finally {
      btn.disabled = false;
      btn.textContent = 'Test';
    }
  }

  // Refresh reads the addon's manifest again, after a change in AIOStreams: a save of the settings,
  // unchanged, empties Gelato's caches (the manifest with them). The saved settings, not the page's:
  // what is not saved on the page stays as it is.
  async function refreshCatalogs() {
    try {
      const cfg = await window.ApiClient.getPluginConfiguration(pluginId);
      await window.ApiClient.updatePluginConfiguration(pluginId, cfg);
    } catch (e) {
      console.error(e);
    }
    await loadCatalogs();
  }

  async function loadCatalogs() {
    try {
      // Show loading spinner in list
      catalogsList.innerHTML = '<div style="padding: 2em; text-align: center;"><span class="gelatoSpin" style="font-size: 1.6em;"></span><p>Loading catalogs…</p></div>';

      // Get current configuration to preserve user settings
      const cfg = await window.ApiClient.getPluginConfiguration(pluginId);
      const savedCatalogs = cfg.Catalogs || [];

      // The catalogs are the addon's: without one there is nothing to list yet.
      catalogsLoaded = false;
      if (!(cfg.Url || '').trim()) {
        allCatalogs = [];
        catalogsList.innerHTML = '<p style="padding: 1em; text-align: center; opacity: 0.7;">No catalogs yet. ' +
          'Enter your AIOStreams URL on the Addon tab and save. The catalogs are the ones your addon lists.</p>';
        return;
      }

      const response = await window.ApiClient.getJSON(window.ApiClient.getUrl('gelato/catalogs'));
      if (libraries.length === 0) await loadLibraries();

      // Merge server catalogs with saved user preferences
      allCatalogs = (response || []).map(serverCat => {
        // Find matching saved catalog
        const savedCat = savedCatalogs.find(s => s.Id === serverCat.Id && s.Type === serverCat.Type);

        return {
          Id: serverCat.Id || '',
          Type: serverCat.Type || '',
          Name: serverCat.Name || '',
          // Use saved user preference if available, otherwise use server default
          Enabled: savedCat ? savedCat.Enabled : (serverCat.Enabled || false),
          MaxItems: savedCat ? savedCat.MaxItems : (serverCat.MaxItems || 0),
          CreateCollection: savedCat ? savedCat.CreateCollection : (serverCat.CreateCollection || false),
          Path: savedCat ? (savedCat.Path || '') : (serverCat.Path || ''),
          Url: serverCat.Url || ''
        };
      });
      catalogsLoaded = true;

      renderCatalogs();
    } catch (e) {
      console.error('Error loading catalogs:', e);
      const failed = document.createElement('p');
      failed.style.cssText = 'color: red; padding: 1em;';
      failed.textContent = 'Couldn\'t load the catalogs: ' + await errorText(e);
      catalogsList.replaceChildren(failed);
    }
  }

  // A checkbox of the table in the markup Jellyfin's own component builds around one, so it
  // looks like those on the other tabs. The classes alone style it.
  function tableCheckbox(input) {
    input.classList.add('emby-checkbox');
    input.dataset.embycheckbox = 'true';
    const label = document.createElement('label');
    label.className = 'emby-checkbox-label';
    label.style.cssText = 'display: inline-flex; min-height: 2.2em; margin: 0;';
    const text = document.createElement('span');
    text.className = 'checkboxLabel';
    const outline = document.createElement('span');
    outline.className = 'checkboxOutline';
    outline.innerHTML = '<span class="material-icons checkboxIcon checkboxIcon-checked check" aria-hidden="true"></span>' +
      '<span class="material-icons checkboxIcon checkboxIcon-unchecked" aria-hidden="true"></span>';
    label.append(input, text, outline);
    return label;
  }

  // A column's tooltip: on the page's body, under its ? and inside the window, while the ? is
  // hovered or focused (a tap on a phone).
  function attachTip(el) {
    let tip = null;
    const show = () => {
      if (tip) return;
      tip = document.createElement('div');
      tip.className = 'gelatoTip';
      tip.textContent = el.dataset.tip;
      document.body.appendChild(tip);
      const r = el.getBoundingClientRect();
      const w = tip.offsetWidth;
      const left = Math.min(Math.max(8, r.left + r.width / 2 - w / 2), window.innerWidth - w - 8);
      tip.style.left = left + 'px';
      tip.style.top = (r.bottom + 8) + 'px';
    };
    const hide = () => {
      tip?.remove();
      tip = null;
    };
    el.addEventListener('mouseenter', show);
    el.addEventListener('mouseleave', () => { if (document.activeElement !== el) hide(); });
    el.addEventListener('focus', show);
    el.addEventListener('blur', hide);
    // The table scrolls, and the page renders the table anew: a tip of a ? that is gone goes too.
    catalogsList.addEventListener('scroll', hide, { passive: true });
    window.addEventListener('scroll', hide, { passive: true, once: true });
    new MutationObserver((_, obs) => { if (!el.isConnected) { hide(); obs.disconnect(); } })
      .observe(catalogsList, { childList: true });
  }

  function renderCatalogs() {
    // Not loaded: what the load put there (its spinner, a hint, an error) stays.
    if (!catalogsLoaded) return;

    catalogsList.innerHTML = '';
    const searchTerm = (txtCatalogSearch.value || '').toLowerCase();

    const filtered = allCatalogs.filter(cat =>
      (cat.Name || '').toLowerCase().includes(searchTerm) ||
      (cat.Type || '').toLowerCase().includes(searchTerm)
    );

    if (filtered.length === 0) {
      catalogsList.innerHTML = '<p style="padding: 1em; text-align: center; opacity: 0.7;">' +
        (allCatalogs.length === 0 ? 'Your addon lists no catalogs that can be imported.' : 'No catalogs match your search.') +
        '</p>';
      return;
    }

    const table = document.createElement('table');
    table.classList.add('table', 'detailTable');
    table.style.width = '100%';
    table.style.textAlign = 'left';
    table.style.borderCollapse = 'collapse';

    const header = document.createElement('tr');
    // What a column does, behind a ? beside its name: the names alone said too little.
    const help = text => `<span class="helpTip" tabindex="0" role="note" ` +
      `aria-label="${text}" data-tip="${text}">?</span>`;
    header.innerHTML = `
          <th style="padding: 10px;">Name</th>
          <th style="padding: 10px;">Type</th>
          <th style="padding: 10px;">Import${help('Imports the catalog’s titles into your library and updates them each time the Import Catalogs task runs.')}</th>
          <th style="padding: 10px;">Collection${help('Also puts the catalog’s titles into a Jellyfin collection named after the catalog.')}</th>
          <th style="padding: 10px;">Max items${help('How many titles to import, from the top of the catalog.')}</th>
          <th style="padding: 10px;">Library${help('Where the catalog’s titles go. Default: the movies or series library from the Libraries tab.')}</th>
          <th style="padding: 10px;"></th>
      `;
    table.appendChild(header);

    filtered.forEach(cat => {
      const row = document.createElement('tr');

      // Name
      const nameCell = document.createElement('td');
      nameCell.dataset.label = 'name';
      nameCell.style.padding = '10px';
      nameCell.textContent = cat.Name;
      row.appendChild(nameCell);

      // Type
      const typeCell = document.createElement('td');
      typeCell.dataset.label = 'type';
      typeCell.style.padding = '10px';
      const typeChip = document.createElement('span');
      typeChip.className = 'gelatoChip';
      typeChip.textContent = cat.Type;
      typeCell.appendChild(typeChip);
      row.appendChild(typeCell);

      // Enabled
      const enabledCell = document.createElement('td');
      enabledCell.dataset.label = 'Import';
      enabledCell.style.padding = '10px';
      const enabledCheck = document.createElement('input');
      enabledCheck.type = 'checkbox';
      enabledCheck.checked = cat.Enabled;
      enabledCheck.dataset.catalogId = cat.Id;
      enabledCheck.dataset.catalogType = cat.Type;
      enabledCheck.dataset.field = 'Enabled';
      enabledCell.appendChild(tableCheckbox(enabledCheck));
      row.appendChild(enabledCell);

      // Create Collection
      const collCell = document.createElement('td');
      collCell.dataset.label = 'Collection';
      collCell.style.padding = '10px';
      const collCheck = document.createElement('input');
      collCheck.type = 'checkbox';
      collCheck.checked = cat.CreateCollection;
      collCheck.disabled = !cat.Enabled;
      collCheck.dataset.catalogId = cat.Id;
      collCheck.dataset.catalogType = cat.Type;
      collCheck.dataset.field = 'CreateCollection';
      const collLabel = tableCheckbox(collCheck);
      collLabel.style.opacity = cat.Enabled ? '1' : '0.45';
      collCell.appendChild(collLabel);
      row.appendChild(collCell);

      // Items per catalog
      const maxCell = document.createElement('td');
      maxCell.dataset.label = 'Max items';
      maxCell.style.padding = '10px';
      const maxInput = document.createElement('input');
      maxInput.type = 'number';
      maxInput.value = cat.MaxItems > 0 ? cat.MaxItems : 100;
      maxInput.disabled = !cat.Enabled;
      maxInput.style.width = '70px';
      maxInput.style.opacity = cat.Enabled ? '1' : '0.45';
      maxInput.is = 'emby-input';
      maxInput.classList.add('emby-input');
      maxInput.style.padding = '4px';
      maxInput.dataset.catalogId = cat.Id;
      maxInput.dataset.catalogType = cat.Type;
      maxInput.dataset.field = 'MaxItems';

      maxCell.appendChild(maxInput);
      row.appendChild(maxCell);

      // Library of its own
      const pathCell = document.createElement('td');
      pathCell.dataset.label = 'Library';
      pathCell.style.padding = '10px';
      // The classes alone theme it; emby-select's own setup would add a label to the cell.
      const pathInput = document.createElement('select');
      pathInput.classList.add('emby-select', 'emby-select-withcolor');
      // Before it is filled: the options are worded for the table.
      pathInput.dataset.catalogId = cat.Id;
      pathInput.dataset.catalogType = cat.Type;
      pathInput.dataset.field = 'Path';
      fillLibrarySelect(pathInput, cat.Type, cat.Path || '', 'Default');
      // An unsaved pick is shown again, unless its library is gone meanwhile: then the saved
      // folder stays, instead of an empty select that would save as "Default".
      if (cat._library !== undefined && !restorePick(pathInput, cat._library)) delete cat._library;
      pathInput.disabled = !cat.Enabled;
      pathInput.style.minWidth = '200px';
      pathInput.style.opacity = cat.Enabled ? '1' : '0.45';
      // Room on the right for the arrow: a long folder would run under it.
      pathInput.style.padding = '4px 2em 4px 4px';
      // Kept on the catalog right away: filtering the table renders it anew.
      pathInput.addEventListener('change', () => { cat._library = pathInput.value; });

      pathCell.appendChild(pathInput);
      // In its cell now: a folder in no library gets its line under the select.
      showLibraryWarning(pathInput);
      row.appendChild(pathCell);

      // Actions
      const actionCell = document.createElement('td');
      actionCell.dataset.label = 'action';
      actionCell.style.padding = '10px';

      const importBtn = document.createElement('button');
      importBtn.type = 'button';
      // An icon: the column is narrow, and "Import" is the name of the checkbox column already.
      importBtn.innerHTML = '<span class="material-icons sync" aria-hidden="true"></span>';
      importBtn.title = 'Import now';
      importBtn.setAttribute('aria-label', 'Import ' + cat.Name + ' now');
      importBtn.className = 'raised button-submit emby-button rowImport';
      importBtn.is = 'emby-button';
      importBtn.disabled = !cat.Enabled;
      importBtn.style.opacity = cat.Enabled ? '1' : '0.45';
      importBtn.onclick = async () => {
        await triggerImport(cat);
      };

      // Toggle all row controls when Enabled changes
      enabledCheck.addEventListener('change', () => {
        const on = enabledCheck.checked;
        maxInput.disabled = !on;
        maxInput.style.opacity = on ? '1' : '0.45';
        pathInput.disabled = !on;
        pathInput.style.opacity = on ? '1' : '0.45';
        showLibraryWarning(pathInput);
        collCheck.disabled = !on;
        collLabel.style.opacity = on ? '1' : '0.45';
        importBtn.disabled = !on;
        importBtn.style.opacity = on ? '1' : '0.45';
      });

      actionCell.appendChild(importBtn);
      row.appendChild(actionCell);
      table.appendChild(row);
    });

    catalogsList.appendChild(table);
    table.querySelectorAll('.helpTip').forEach(attachTip);
  }

  function collectCatalogStatesFromDOM() {
    const rows = catalogsList.querySelectorAll('tr');
    rows.forEach(row => {
      const enabledCheck = row.querySelector('input[data-field="Enabled"]');
      const collCheck = row.querySelector('input[data-field="CreateCollection"]');
      const maxInput = row.querySelector('input[data-field="MaxItems"]');
      const pathInput = row.querySelector('select[data-field="Path"]');
      if (!enabledCheck || !collCheck || !maxInput) return;

      const catId = enabledCheck.dataset.catalogId;
      const catType = enabledCheck.dataset.catalogType;
      const cat = allCatalogs.find(c => c.Id === catId && c.Type === catType);
      if (!cat) return;

      cat.Enabled = enabledCheck.checked;
      cat.CreateCollection = collCheck.checked;
      cat.MaxItems = parseInt(maxInput.value) || 0;
      if (pathInput) cat._library = pathInput.value;
    });
  }

  async function triggerImport(cat) {
    if (!await askConfirm('Import catalog', 'Import ' + cat.Name + ' now?', 'Start import')) return;
    Dashboard.showLoadingMsg();
    try {
      // The import reads the saved settings, so the row is saved as it is on the page first:
      // a library or a limit picked a moment ago would not count otherwise.
      collectCatalogStatesFromDOM();
      if (cat._library !== undefined) {
        cat.Path = await resolveLibraryPick(cat._library, cat.Path);
        delete cat._library;
      }
      const url = 'gelato/catalogs/' + encodeURIComponent(cat.Id) + '/' + encodeURIComponent(cat.Type);
      await window.ApiClient.ajax({
        type: "POST",
        url: window.ApiClient.getUrl(url + '/config'),
        data: JSON.stringify(cat),
        contentType: 'application/json'
      });
      await window.ApiClient.ajax({ type: "POST", url: window.ApiClient.getUrl(url + '/import') });
      Dashboard.alert("Import started.");
    } catch (e) {
      console.error(e);
      await showError('Couldn\'t start the import', e);
    } finally {
      Dashboard.hideLoadingMsg();
    }
  }

  // The catalog table as it is on the page, put into the configuration that gets saved. A
  // table that was never loaded changes nothing: the saved settings stay.
  async function applyCatalogRows(cfg) {
    if (allCatalogs.length === 0) return;
    collectCatalogStatesFromDOM();
    for (const cat of allCatalogs) {
      if (cat._library !== undefined) {
        cat.Path = await resolveLibraryPick(cat._library, cat.Path);
        delete cat._library;
      }
    }
    // The list holds what the addon has now; a catalog it dropped keeps its settings.
    const unlisted = (cfg.Catalogs || []).filter(c =>
      !allCatalogs.some(a => a.Id === c.Id && a.Type === c.Type));
    cfg.Catalogs = allCatalogs.concat(unlisted);
  }

  async function triggerImportAll() {
    collectCatalogStatesFromDOM();
    const enabled = allCatalogs.filter(c => c.Enabled);
    if (enabled.length === 0) {
      Dashboard.alert('No catalog is turned on.');
      return;
    }
    const which = enabled.length === 1 ? enabled[0].Name : 'the ' + enabled.length + ' catalogs that are on';
    if (!await askConfirm('Import all catalogs', 'Import ' + which + ' now?', 'Start import')) return;
    Dashboard.showLoadingMsg();
    try {
      // The import reads the saved settings, so the table is saved as it is on the page
      // first, as a row's own Import button does for its row: a catalog enabled a moment
      // ago would not be imported otherwise.
      const cfg = await window.ApiClient.getPluginConfiguration(pluginId);
      await applyCatalogRows(cfg);
      await window.ApiClient.updatePluginConfiguration(pluginId, cfg);
      renderCatalogs();

      const url = window.ApiClient.getUrl('gelato/catalogs/import-all');
      await window.ApiClient.ajax({ type: "POST", url: url });
      Dashboard.alert("Import started in the background.");
    } catch (e) {
      console.error(e);
      await showError('Couldn\'t start the import', e);
    } finally {
      Dashboard.hideLoadingMsg();
    }
  }

  async function loadConfig() {
    Dashboard.showLoadingMsg();
    try {
      const cfg = await window.ApiClient.getPluginConfiguration(pluginId);
      cfg.UserConfigs = userConfigsArrayToObject(cfg.UserConfigs);
      currentConfig = cfg;
      // Populate fields
      if (txtUrl) txtUrl.value = cfg.Url || "";
      if (txtBasePath) txtBasePath.value = cfg.BasePath || "";
      await loadLibraries();
      fillLibrarySelect(selMovieLibrary, 'movie', cfg.MoviePath || '');
      fillLibrarySelect(selSeriesLibrary, 'series', cfg.SeriesPath || '');
      if (txtStreamTTL) txtStreamTTL.value = cfg.StreamTTL || 3600;
      if (chkEnableMixed) chkEnableMixed.checked = cfg.EnableMixed ?? true;
      if (chkExtendLocalSeriesTrees) chkExtendLocalSeriesTrees.checked = cfg.ExtendLocalSeriesTrees ?? false;
      // The setting says the opposite of the switch, as in the setup assistant.
      if (chkAddonSearch) chkAddonSearch.checked = !cfg.DisableSearch;
      if (chkFilterUnreleased) chkFilterUnreleased.checked = cfg.FilterUnreleased || false;
      showUnreleasedHint();
      if (txtBufferDays) txtBufferDays.value = cfg.FilterUnreleasedBufferDays ?? 0;
      if (txtFFmpegAnalyzeDuration) txtFFmpegAnalyzeDuration.value = cfg.FFmpegAnalyzeDuration || "5M";
      if (txtFFmpegProbeSize) txtFFmpegProbeSize.value = cfg.FFmpegProbeSize || "25M";
      if (chkEnableJavaScriptInjection) chkEnableJavaScriptInjection.checked = cfg.EnableJavaScriptInjection || false;
      if (chkLazyImages) chkLazyImages.checked = cfg.LazyImages ?? false;
      if (chkPersonRemoteTitles) chkPersonRemoteTitles.checked = cfg.PersonRemoteTitles ?? true;
      chkHideUnplayedSearchTitles.checked = cfg.HideUnplayedSearchTitles ?? false;
      showPersonRemoteTitles();
      if (chkPreProbe) chkPreProbe.checked = cfg.PreProbe ?? true;
      if (chkPlaybackFailover) chkPlaybackFailover.checked = cfg.PlaybackFailover ?? false;
      if (chkRemuxDbEnabled) chkRemuxDbEnabled.checked = cfg.RemuxDbEnabled ?? true;
      if (chkRemuxDbContribute) chkRemuxDbContribute.checked = cfg.RemuxDbContribute ?? true;

      // User Overrides Logic
      await loadUsers();
      renderOverridesList();
      markDirty(false);

      // A new install: the setup assistant opens on its own, once per browser tab.
      if (!(cfg.Url || '').trim() && !setupOfferedThisTab()) startSetup();
    } catch (e) {
      console.error(e);
    } finally {
      Dashboard.hideLoadingMsg();
    }
  }

  async function saveConfig(e) {
    e.preventDefault();
    Dashboard.showLoadingMsg();
    try {
      const cfg = await window.ApiClient.getPluginConfiguration(pluginId);
      cfg.Url = txtUrl.value.trim();
      cfg.BasePath = txtBasePath.value.trim();
      if (cfg.BasePath) await fetchLibraries(cfg.BasePath);
      cfg.MoviePath = await resolveLibraryPick(selMovieLibrary.value, cfg.MoviePath);
      cfg.SeriesPath = await resolveLibraryPick(selSeriesLibrary.value, cfg.SeriesPath);

      // Stream TTL
      cfg.StreamTTL = parseInt(txtStreamTTL.value) || 3600;
      cfg.EnableMixed = chkEnableMixed.checked;
      cfg.ExtendLocalSeriesTrees = chkExtendLocalSeriesTrees.checked;

      cfg.DisableSearch = !chkAddonSearch.checked;
      cfg.FilterUnreleased = chkFilterUnreleased.checked;
      cfg.FilterUnreleasedBufferDays = txtBufferDays.value !== '' ? parseInt(txtBufferDays.value) : 0;

      cfg.FFmpegAnalyzeDuration = txtFFmpegAnalyzeDuration.value;
      cfg.FFmpegProbeSize = txtFFmpegProbeSize.value;
      cfg.EnableJavaScriptInjection = chkEnableJavaScriptInjection.checked;
      cfg.LazyImages = chkLazyImages.checked;
      cfg.PersonRemoteTitles = chkPersonRemoteTitles.checked;
      cfg.HideUnplayedSearchTitles = chkHideUnplayedSearchTitles.checked;
      cfg.PreProbe = chkPreProbe.checked;
      cfg.PlaybackFailover = chkPlaybackFailover.checked;
      cfg.RemuxDbEnabled = chkRemuxDbEnabled.checked;
      cfg.RemuxDbContribute = chkRemuxDbContribute.checked;

      // Only collect catalog states if catalogs have been loaded (i.e., if we're on catalogs tab or it was loaded)
      await applyCatalogRows(cfg);
      cfg.Catalogs = cfg.Catalogs || [];

      // Handle User Configs
      // Use currentConfig.UserConfigs which contains any unsaved user overrides
      const userConfigsObj = (currentConfig && currentConfig.UserConfigs) || {};
      cfg.UserConfigs = Object.entries(userConfigsObj).map(([userId, override]) => ({
        UserId: userId,
        Url: override.Url,
        MoviePath: override.MoviePath,
        SeriesPath: override.SeriesPath,
        DisableSearch: override.DisableSearch,
        PersonRemoteTitles: override.PersonRemoteTitles ?? true
      }));

      await window.ApiClient.updatePluginConfiguration(pluginId, cfg);

      // Update local currentConfig to match
      currentConfig = cfg;
      // Re-hydrate the object form of UserConfigs for local editing
      currentConfig.UserConfigs = userConfigsArrayToObject(cfg.UserConfigs);

      Dashboard.processPluginConfigurationUpdateResult();

      // New folders are in their libraries now: show the picks as saved.
      await loadLibraries();
      fillLibrarySelect(selMovieLibrary, 'movie', cfg.MoviePath || '');
      fillLibrarySelect(selSeriesLibrary, 'series', cfg.SeriesPath || '');
      currentConfig.MoviePath = cfg.MoviePath;
      currentConfig.SeriesPath = cfg.SeriesPath;
      renderCatalogs();
      markDirty(false, true);
    } catch (e) {
      console.error(e);
      await showError('Couldn\'t save the settings', e);
    } finally {
      Dashboard.hideLoadingMsg();
    }
  }

  async function loadUsers() {
    try {
      users = (await window.ApiClient.getUsers()).filter(u => guidRegex.test(u.Id));
      selUserConfig.innerHTML = '<option value="">-- Select user --</option>';
      users.forEach(u => {
        const opt = document.createElement('option');
        opt.value = u.Id;
        opt.textContent = u.Name;
        selUserConfig.appendChild(opt);
      });
    } catch (e) { console.error(e); }
  }

  function renderOverridesList() {
    userOverridesList.innerHTML = '';
    const overrides = currentConfig?.UserConfigs || {};
    const entries = Object.entries(overrides);

    if (entries.length === 0) {
      userOverridesList.innerHTML = '<p style="opacity: .6; font-style: italic;">No user overrides yet.</p>';
      return;
    }

    for (const [userId, override] of entries) {
      const userName = users.find(u => u.Id === userId)?.Name || userId;

      const container = document.createElement('div');
      container.className = 'gelatoOverrideRow';

      const infoDiv = document.createElement('div');
      infoDiv.className = 'gelatoOverrideInfo';
      const strong = document.createElement('strong');
      strong.textContent = userName;
      infoDiv.appendChild(strong);
      const summary = document.createElement('div');
      summary.className = 'gelatoOverrideSummary';
      summary.textContent = override.DisableSearch
        ? 'Searches the library only'
        : (override.PersonRemoteTitles ?? true)
          ? 'Searches AIOStreams; person pages show titles outside the library'
          : 'Searches AIOStreams; person pages show the library only';
      infoDiv.appendChild(summary);
      container.appendChild(infoDiv);

      const editBtn = document.createElement('button');
      editBtn.type = 'button';
      editBtn.className = 'emby-button';
      editBtn.is = 'emby-button';
      editBtn.innerHTML = 'Edit';
      editBtn.onclick = () => editOverride(userId);
      container.appendChild(editBtn);

      const delBtn = document.createElement('button');
      delBtn.type = 'button';
      delBtn.className = 'emby-button';
      delBtn.is = 'emby-button';
      delBtn.innerHTML = 'Delete';
      delBtn.onclick = () => deleteOverride(userId);
      container.appendChild(delBtn);

      userOverridesList.appendChild(container);
    }
  }

  function editOverride(userId) {
    selUserConfig.value = userId;
    onUserSelected();
  }

  function deleteOverride(userId) {
    if (currentConfig?.UserConfigs) {
      delete currentConfig.UserConfigs[userId];
      renderOverridesList();
      markDirty(true);
    }
  }

  function onUserSelected() {
    const userId = selUserConfig.value;
    if (!userId) {
      userOverrideContainer.style.display = 'none';
      return;
    }
    userOverrideContainer.style.display = 'block';

    const override = currentConfig?.UserConfigs?.[userId];
    if (override) {
      txtUserUrl.value = override.Url || '';
      fillLibrarySelect(selUserMovieLibrary, 'movie', override.MoviePath || '');
      fillLibrarySelect(selUserSeriesLibrary, 'series', override.SeriesPath || '');
      chkUserAddonSearch.checked = !override.DisableSearch;
      chkUserPersonRemoteTitles.checked = override.PersonRemoteTitles ?? true;
    } else {
      // New
      txtUserUrl.value = currentConfig?.Url || '';
      fillLibrarySelect(selUserMovieLibrary, 'movie', currentConfig?.MoviePath || '');
      fillLibrarySelect(selUserSeriesLibrary, 'series', currentConfig?.SeriesPath || '');
      // A new override starts from the settings everyone has.
      chkUserAddonSearch.checked = chkAddonSearch.checked;
      chkUserPersonRemoteTitles.checked = chkPersonRemoteTitles.checked;
    }
    showUserPersonRemoteTitles();
  }

  page.querySelector('#btnCancelOverride').addEventListener('click', () => {
    selUserConfig.value = '';
    userOverrideContainer.style.display = 'none';
  });

  page.querySelector('#btnAddOverride').addEventListener('click', async () => {
    const userId = selUserConfig.value;
    if (!userId) return;
    if (!currentConfig.UserConfigs) currentConfig.UserConfigs = {};

    const previous = currentConfig.UserConfigs[userId] || currentConfig;
    let moviePath, seriesPath;
    Dashboard.showLoadingMsg();
    try {
      moviePath = await resolveLibraryPick(selUserMovieLibrary.value, previous.MoviePath);
      seriesPath = await resolveLibraryPick(selUserSeriesLibrary.value, previous.SeriesPath);
    } catch (e) {
      console.error(e);
      await showError('Couldn\'t set up the library folder', e);
      return;
    } finally {
      Dashboard.hideLoadingMsg();
    }

    currentConfig.UserConfigs[userId] = {
      Url: txtUserUrl.value.trim(),
      MoviePath: moviePath,
      SeriesPath: seriesPath,
      DisableSearch: !chkUserAddonSearch.checked,
      PersonRemoteTitles: chkUserPersonRemoteTitles.checked
    };

    renderOverridesList();
    selUserConfig.value = '';
    userOverrideContainer.style.display = 'none';
    markDirty(true);
  });

  function init() {
    drawLogos(page);
    // A skin's stylesheet can come in late: look at its colour once more when it is likely there.
    setTimeout(() => applyThemeAccent(page, page.querySelector('#btnSave')), 1500);
    form.addEventListener("submit", saveConfig);
    txtCatalogSearch.addEventListener('input', renderCatalogs);
    chkFilterUnreleased.addEventListener('change', showUnreleasedHint);
    chkAddonSearch.addEventListener('change', showPersonRemoteTitles);
    chkUserAddonSearch.addEventListener('change', showUserPersonRemoteTitles);
    btnRefreshCatalogs.addEventListener('click', refreshCatalogs);
    page.querySelector('#btnImportAll').addEventListener('click', triggerImportAll);
    page.querySelector('#btnCheckAddon').addEventListener('click', testAddonUrl);
    page.querySelector('#btnOpenSetup').addEventListener('click', startSetup);
    selUserConfig.addEventListener('change', onUserSelected);
    // Seerr's webhook (SeerrController): the address this page reaches the server at, one click to copy.
    page.querySelector('#txtSeerrUrl').value = window.ApiClient.serverAddress().replace(/\/+$/, '') + '/gelato/seerr';
    page.querySelectorAll('[data-copy]').forEach(btn => {
      btn.onclick = async e => {
        e.preventDefault();
        await copyText(page.querySelector('#' + btn.dataset.copy).value);
        btn.textContent = 'Copied';
        setTimeout(() => { btn.textContent = 'Copy'; }, 1500);
      };
    });
    // What changes on the page and is not saved yet; the catalog search filters only.
    const changed = e => {
      if (e.target === txtCatalogSearch || e.target === selUserConfig) return;
      if (e.target.closest && e.target.closest('#userOverrideContainer')) return;
      markDirty(true);
    };
    form.addEventListener('input', changed);
    form.addEventListener('change', changed);
    // Once: the page is shown again and again, the document stays.
    if (!page.dataset.watchesLibraries) {
      page.dataset.watchesLibraries = '1';
      document.addEventListener('visibilitychange', refreshLibraries);
      window.addEventListener('focus', refreshLibraries);
    }

    let startTab = 'addon';
    try { startTab = sessionStorage.getItem(TAB_KEY) || startTab; } catch (_) { /* private mode */ }
    openTab(startTab);
    // The ink needs the tab's size, which the font may change once it has loaded.
    requestAnimationFrame(() => moveInk(page.querySelector('.tab-button.active')));
    if (document.fonts) document.fonts.ready.then(() => moveInk(page.querySelector('.tab-button.active')));

    loadConfig();
  }

  // The setup assistant's view of this page.
  const setupContext = {
    page,
    libraries: () => libraries,
    loadLibraries,
    libraryOf,
    libraryFits,
    resolveLibraryPick,
    createLibraryFolder,
    saved() {
      allCatalogs = [];
      catalogsLoaded = false;
      loadConfig();
    },
  };
  const startSetup = () => openSetup(setupContext);

  view.addEventListener('viewshow', init);
}
