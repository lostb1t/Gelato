(function () {
    'use strict';

    // Jellyfin Web's item menu (right-click, a long press, the menu key, or a card's three dots)
    // loads the item first, with the same call the item's page makes. For a movie or an episode
    // Gelato answers that call after a stream search, which held the menu up to a few seconds,
    // and it put a search result into the library. The menu needs neither: this marks its
    // request (gelatoMenu), and Gelato answers it with the item as it is. The actions in the
    // menu make their own requests, unmarked.

    const FLAG = 'gelatoMenu';
    // The card whose menu is opening, until its item request has gone out. The menu asks for
    // the item in the same turn as the event that opens it.
    let armed = null;
    let marking = false;

    const norm = (id) => String(id || '').replace(/-/g, '').toLowerCase();

    function patch(apiClient) {
        const proto = apiClient && Object.getPrototypeOf(apiClient);
        if (!proto || proto.gelatoItemMenu || typeof proto.getItem !== 'function'
            || typeof proto.getUrl !== 'function') return;
        proto.gelatoItemMenu = true;

        const getItem = proto.getItem;
        proto.getItem = function (userId, itemId) {
            if (!armed || norm(itemId) !== armed) return getItem.apply(this, arguments);
            armed = null;
            marking = true;
            try {
                return getItem.apply(this, arguments);
            } finally {
                marking = false;
            }
        };

        const getUrl = proto.getUrl;
        proto.getUrl = function (name, params, serverAddress) {
            if (marking && /\bItems\/[0-9a-f-]+$/i.test(String(name))) {
                params = Object.assign({}, params, { [FLAG]: true });
            }
            return getUrl.call(this, name, params, serverAddress);
        };
    }

    function arm(target) {
        const card = target && target.closest && target.closest('[data-id]');
        if (!card) return;
        patch(window.ApiClient);
        armed = norm(card.getAttribute('data-id'));
        setTimeout(() => { armed = null; }, 0);
    }

    // A right-click, a long press and the menu key reach the cards as a 'menu' command.
    document.addEventListener('command', function (e) {
        if (e.detail && e.detail.command === 'menu') arm(e.target);
    }, true);

    // A card's three dots.
    document.addEventListener('click', function (e) {
        const button = e.target && e.target.closest && e.target.closest('[data-action="menu"]');
        if (button) arm(button);
    }, true);
})();
