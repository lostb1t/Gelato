(function(){
    'use strict';

    async function fetchMediaSources(itemId) {
        try {
            const url = window.ApiClient.getUrl(`Itema/${itemId}?gelato=true`);
            const response = await window.ApiClient.ajax({
                type: 'GET',
                url: url,
                dataType: 'json'
            });

            return response?.results || [];
        } catch (err) {
            console.error('[Gelato] Error fetching sources:', err);
            return null;
        }
    }
    
    function isDetailsPage() {
        const hash = window.location.hash;
        return hash.includes('/details?') || 
               hash.includes('#!/item/item.html?') ||
               hash.includes('/item?id=');
    }
    
    function getItemId() {
        // Try URL params
        const urlParams = new URLSearchParams(window.location.search);
        const urlId = urlParams.get('id');
        if (urlId) {
            currentItemId = urlId;
            console.log('[ReviewsCarousel] Captured itemId from URL params:', currentItemId);
            return urlId;
        }

        // Try URL hash
        const urlMatch = window.location.href.match(/[?&]id=([a-f0-9-]+)/i);
        if (urlMatch) {
            const currentItemId = urlMatch[1].replace(/-/g, '');
            console.log('[Gelato] Captured itemId from URL match:', currentItemId);
            return currentItemId;
        }

        console.warn('[Gelato] No itemId found');
        return null;
    }
    
    function start() {
      console.log("YOOOO");
        // Try to initialize immediately if on details page
        if (document.readyState === 'complete' || document.readyState === 'interactive') {
            if (isDetailsPage()) {
console.log("YES");
            }
        }
        return;
        // Also watch for DOM changes and page navigation - BUT ONLY ON DETAILS PAGES
        let isProcessing = false;
        let lastItemId = null; // Track last processed item to avoid duplicate work
        const observer = new MutationObserver(() => {
            if (isProcessing) return;
            if (!isDetailsPage()) return; // ONLY run on details pages
            
            const castCollapsible = document.querySelector('#castCollapsible');
            const currentItemId = getItemId();
            
            // Only process if: castCollapsible exists, no carousel yet, and we haven't processed this item
            if (castCollapsible && !document.querySelector('.cavea-reviews-carousel') && currentItemId && currentItemId !== lastItemId) {
                isProcessing = true;
                lastItemId = currentItemId;
                setTimeout(() => {
                    initReviewsCarousel();
                    isProcessing = false;
                }, 100);
            }
        });

        observer.observe(document.body, { 
            childList: true, 
            subtree: true 
        });

        // Listen for hash changes (Jellyfin navigation) - ONLY trigger on details pages
        let hashChangeTimeout = null;
        window.addEventListener('hashchange', () => {
            if (!isDetailsPage()) {
                console.log('[ReviewsCarousel] Not a details page, ignoring');
                lastItemId = null; // Reset when leaving details page
                return;
            }
            console.log('[ReviewsCarousel] Hash changed to details page, will check for reviews');
            lastItemId = null; // Reset for new page
            if (hashChangeTimeout) clearTimeout(hashChangeTimeout);
            hashChangeTimeout = setTimeout(() => {
                const castCollapsible = document.querySelector('#castCollapsible');
                if (castCollapsible && !document.querySelector('.cavea-reviews-carousel')) {
                    initReviewsCarousel();
                }
            }, 1000);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
    
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
