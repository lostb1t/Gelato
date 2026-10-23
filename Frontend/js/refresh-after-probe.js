(function () {
    'use strict';

    // A title's page shows the tracks of the version it opened with. A version that was never
    // probed has none yet: Gelato probes it a second after the page opens. It tells the user's
    // sessions when that probe starts and ends (RefreshProgress 0 and 100) and, when it saved
    // the tracks, that the item changed (LibraryChanged; PreProbes.AnnounceEndAsync). Meanwhile
    // the page says the tracks are being read; then this loads the version again and fills in
    // the video, audio and subtitle lists the way the page does.

    // The longest a probe may run (PreProbes.PreProbeTimeout): no end heard by then, the
    // indicator goes.
    const PROBE_TIMEOUT_MS = 60000;

    let view = null;
    let unsubscribe = null;
    let observer = null;
    // The reload of the probed version under way: its end comes just after it starts.
    let loading = null;
    // Ids whose probe runs, with the time it started.
    const probing = new Map();
    // Sources loaded here, by id: the page's own list stays the one it opened with, and it shows
    // that again when the version is switched until its own reload is in.
    const fresh = new Map();

    const norm = (id) => String(id || '').replace(/-/g, '').toLowerCase();

    function injectStyle() {
        if (document.getElementById('gelato-probing-style')) return;
        const style = document.createElement('style');
        style.id = 'gelato-probing-style';
        // The theme's own text colour and font: nothing here has a colour of its own.
        style.textContent = `
            .gelatoProbing { display: flex; align-items: center; gap: .6em; margin: .4em 0 .8em;
                font-size: .9em; opacity: 0; animation: gelatoProbingIn .2s ease .4s forwards; }
            .gelatoProbing.hide { display: none; }
            .gelatoProbingSpinner { width: 1em; height: 1em; box-sizing: border-box; border-radius: 50%;
                border: .15em solid currentColor; border-right-color: transparent;
                animation: gelatoProbingSpin .8s linear infinite; }
            @keyframes gelatoProbingIn { to { opacity: .7; } }
            @keyframes gelatoProbingSpin { to { transform: rotate(360deg); } }
            @media (prefers-reduced-motion: reduce) {
                .gelatoProbing { animation: none; opacity: .7; }
                .gelatoProbingSpinner { animation: none; }
            }
        `;
        document.head.appendChild(style);
    }

    function hasTracks(page) {
        return !page.querySelector('.selectVideoContainer').classList.contains('hide')
            || !page.querySelector('.selectAudioContainer').classList.contains('hide');
    }

    // The version shown: the selected one, or the page's item while its versions load.
    function shownId(page) {
        return norm(page.querySelector('.selectSource').value || page.dataset.gelatoItemId);
    }

    function versionIds(page) {
        const ids = Array.from(page.querySelector('.selectSource').options).map(o => norm(o.value));
        ids.push(norm(page.dataset.gelatoItemId));
        return ids;
    }

    function update() {
        const page = view;
        if (!page) return;
        const started = probing.get(shownId(page));
        const show = started !== undefined && Date.now() - started < PROBE_TIMEOUT_MS && !hasTracks(page);
        let indicator = page.querySelector('.gelatoProbing');
        if (show && !indicator) {
            injectStyle();
            indicator = document.createElement('div');
            indicator.className = 'gelatoProbing';
            indicator.setAttribute('role', 'status');
            const spinner = document.createElement('span');
            spinner.className = 'gelatoProbingSpinner';
            const text = document.createElement('span');
            text.textContent = 'Reading the tracks of this version…';
            indicator.append(spinner, text);
            page.querySelector('.trackSelections').appendChild(indicator);
        }
        if (indicator) indicator.classList.toggle('hide', !show);
    }

    function sortTracks(a, b) {
        return (a.IsExternal - b.IsExternal)
            || (b.IsForced - a.IsForced)
            || (b.IsDefault - a.IsDefault)
            || (a.Index - b.Index);
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text == null ? '' : String(text);
        return div.innerHTML;
    }

    function fill(page, kind, tracks, selectedId, enabled, offOption) {
        const select = page.querySelector('.select' + kind);
        select.innerHTML = (offOption || '') + tracks.map(function (t) {
            const title = t.DisplayTitle || [t.Codec && t.Codec.toUpperCase()].filter(Boolean).join(' ');
            return '<option value="' + t.Index + '"' + (t.Index === selectedId ? ' selected' : '') + '>'
                + escapeHtml(title) + '</option>';
        }).join('');
        if (enabled) select.removeAttribute('disabled');
        else select.setAttribute('disabled', 'disabled');
        page.querySelector('.select' + kind + 'Container').classList.toggle('hide', tracks.length === 0);
    }

    function render(page, source) {
        const streams = source.MediaStreams || [];
        const video = streams.filter(s => s.Type === 'Video');
        const audio = streams.filter(s => s.Type === 'Audio').sort(sortTracks);
        const subtitles = streams.filter(s => s.Type === 'Subtitle').sort(sortTracks);
        const subtitleId = source.DefaultSubtitleStreamIndex == null ? -1 : source.DefaultSubtitleStreamIndex;
        // The page's own, in the user's language.
        const off = page.querySelector('.selectSubtitles option[value="-1"]');

        fill(page, 'Video', video, video.length ? video[0].Index : -1, false);
        fill(page, 'Audio', audio, source.DefaultAudioStreamIndex, audio.length > 1);
        fill(page, 'Subtitles', subtitles, subtitleId, subtitles.length > 0,
            '<option value="-1"' + (subtitleId === -1 ? ' selected' : '') + '>'
            + escapeHtml(off ? off.textContent : 'Off') + '</option>');
    }

    function renderFresh(page) {
        const source = fresh.get(norm(page.querySelector('.selectSource').value));
        if (source && (source.MediaStreams || []).length) render(page, source);
    }

    async function reload(page) {
        const select = page.querySelector('.selectSource');
        const selectedId = select.value;
        if (!selectedId || hasTracks(page)) return;

        const apiClient = window.ApiClient;
        const item = await apiClient.getItem(apiClient.getCurrentUserId(), selectedId);
        // The page moved on while this loaded.
        if (page !== view || select.value !== selectedId) return;

        (item.MediaSources || []).forEach(s => fresh.set(norm(s.Id), s));
        renderFresh(page);
    }

    function onMessage(message) {
        const page = view;
        const data = message && message.Data;
        if (!page || !data) return;
        const ids = versionIds(page);

        if (message.MessageType === 'RefreshProgress') {
            const id = norm(data.ItemId);
            if (!ids.includes(id)) return;
            if (Number(data.Progress) < 100) {
                probing.set(id, Date.now());
                setTimeout(update, PROBE_TIMEOUT_MS + 100);
                update();
            } else {
                // The indicator stays until the tracks are in.
                const end = () => { probing.delete(id); update(); };
                if (loading) loading.then(end);
                else end();
            }
            return;
        }

        const updated = (data.ItemsUpdated || []).map(norm);
        if (!updated.some(id => ids.includes(id))) return;
        const current = loading = reload(page)
            .catch(err => console.warn('[Gelato] Could not load the probed version', err))
            .finally(() => {
                if (loading === current) loading = null;
                update();
            });
    }

    // After the page's own handler, which shows the list it opened with.
    function onSourceChange() {
        if (!view) return;
        renderFresh(view);
        update();
    }

    function disarm() {
        if (unsubscribe) unsubscribe();
        unsubscribe = null;
        if (observer) observer.disconnect();
        observer = null;
    }

    document.addEventListener('viewshow', function (e) {
        const page = e.target;
        if (!page || !page.querySelector || !page.querySelector('.selectSource')) return;
        const apiClient = window.ApiClient;
        if (!apiClient || typeof apiClient.subscribe !== 'function') return;

        disarm();
        if (page === view) {
            // Back on the same page: it shows the list it opened with again.
            renderFresh(page);
        } else {
            if (view) view.querySelector('.selectSource').removeEventListener('change', onSourceChange);
            fresh.clear();
            probing.clear();
            view = page;
            page.querySelector('.selectSource').addEventListener('change', onSourceChange);
        }
        // The probe can start before the page has loaded its item: the item is the one in the
        // address.
        const query = location.hash.split('?')[1] || location.search.slice(1);
        page.dataset.gelatoItemId = new URLSearchParams(query).get('id') || '';
        unsubscribe = apiClient.subscribe(['RefreshProgress', 'LibraryChanged'], onMessage);
        // The page fills its versions and tracks in after the probe may have started.
        observer = new MutationObserver(() => { if (probing.size) update(); });
        observer.observe(page.querySelector('.trackSelections'), {
            subtree: true, childList: true, attributes: true, attributeFilter: ['class']
        });
        update();
    });

    document.addEventListener('viewbeforehide', function (e) {
        if (e.target === view) disarm();
    });
})();
