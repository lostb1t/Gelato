(function(){
    'use strict';

    // Intercept fetch and XMLHttpRequest calls to /Items/{Id}, log them, and add ?gelato=false if missing.
    function isItemsUrl(url) {
        try {
            const u = new URL(url, window.location.origin);
            // Match exactly /Items/{id} or /Users/{userId}/Items/{id} (optionally trailing slash), no deeper paths
            return /^\/(?:Users\/[\w-]+\/)?Items\/[\w-]+\/?$/i.test(u.pathname);
        } catch (e) {
            // Fallback for relative urls that can't be parsed with URL
            return /^\/(?:Users\/[\w-]+\/)?Items\/[\w-]+\/?$/i.test(url);
        }
    }

    function addGelatoIfMissing(url) {
        try {
            const u = new URL(url, window.location.origin);
            if (!u.searchParams.has('gelato')) {
                u.searchParams.set('gelato', 'false');
                return u.toString();
            }
            return url;
        } catch (e) {
            // Relative URL fallback
            if (!/[?&]gelato=/i.test(url)) {
                return url + (url.indexOf('?') === -1 ? '?' : '&') + 'gelato=false';
            }
            return url;
        }
    }

    const _origFetch = window.fetch;
    if (_origFetch) {
        window.fetch = function(input, init) {
            try {
                let reqUrl = typeof input === 'string' ? input : input && input.url;
                if (reqUrl && isItemsUrl(reqUrl) && !/[?&]gelato=/i.test(reqUrl)) {
                    const newUrl = addGelatoIfMissing(reqUrl);
                    if (typeof input === 'string') {
                        input = newUrl;
                    } else if (input instanceof Request) {
                        // Recreate Request with new URL while preserving init-like fields
                        const orig = input;
                        const reqInit = {
                            method: orig.method,
                            headers: orig.headers,
                            body: orig._body || undefined,
                            mode: orig.mode,
                            credentials: orig.credentials,
                            cache: orig.cache,
                            redirect: orig.redirect,
                            referrer: orig.referrer,
                            integrity: orig.integrity,
                            keepalive: orig.keepalive
                        };
                        try { input = new Request(newUrl, reqInit); } catch(e) { input = newUrl; }
                    } else if (input && input.url) {
                        try { input = new Request(newUrl, input); } catch(e) { input = newUrl; }
                    }
                    console.log('Gelato: intercepted fetch to', newUrl);
                }
            } catch (e) {}
            return _origFetch.call(this, input, init);
        };
    }

    const _xhrOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function(method, url) {
        try {
            if (url && isItemsUrl(url) && !/[?&]gelato=/i.test(url)) {
                arguments[1] = addGelatoIfMissing(url);
                console.log('Gelato: intercepted XHR to', arguments[1], { method });
            }
        } catch (e) {}
        return _xhrOpen.apply(this, arguments);
    };

})();
