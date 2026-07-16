// ─── Page loader (spinning diamond) ───────────────────────────────────────
(function () {
    const loader = document.getElementById('page-loader');
    if (!loader) return;
    // Toggle via inline styles (not a CSS class) so hiding never depends on a class the CSS build
    // might tree-shake out — a purged class once left the overlay covering (blocking) the page.
    const hide = () => { loader.style.opacity = '0'; loader.style.visibility = 'hidden'; loader.style.pointerEvents = 'none'; };
    const show = () => { loader.style.opacity = '1'; loader.style.visibility = 'visible'; loader.style.pointerEvents = 'auto'; };

    // Keep the drawing diamond up for at least data-min-ms from when the page started loading, so the
    // animation has time to show (admin-tunable via Settings → Homepage). performance.now() ≈ elapsed
    // since navigation start, so we only wait out the remainder.
    const minMs = parseInt(loader.getAttribute('data-min-ms') || '0', 10) || 0;
    const elapsed = (window.performance && performance.now) ? performance.now() : 0;
    setTimeout(hide, Math.max(0, minMs - elapsed));

    // Only hide instantly when restored from bfcache; on a normal load the min-time timeout above owns it.
    window.addEventListener('pageshow', function (e) { if (e.persisted) hide(); });
    window.addEventListener('pagehide', show);     // leaving the page → cover the gap to the next one

    // Show the loader immediately when a same-origin navigation starts (nicer than waiting for unload).
    document.addEventListener('click', function (e) {
        const a = e.target.closest && e.target.closest('a[href]');
        if (!a || a.target === '_blank' || a.hasAttribute('download')) return;
        const href = a.getAttribute('href') || '';
        if (!href || href[0] === '#' || /^(javascript|mailto|tel):/i.test(href)) return;
        try { if (new URL(a.href).origin !== location.origin) return; } catch (_) { return; }
        if (a.href === location.href) return;      // same page (e.g. a filter that only sets a hash)
        show();
    }, true);
    document.addEventListener('submit', function (e) {
        const f = e.target;
        if (f && f.method && f.method.toLowerCase() === 'get' && (f.target === '_blank')) return;
        show();
    }, true);
})();

// ─── Search Overlay ───────────────────────────────────────────────────────
(function () {
    const overlay     = document.getElementById('search-overlay');
    const input       = document.getElementById('search-input');
    const closeBtn    = document.getElementById('search-close');
    const backdrop    = document.getElementById('search-backdrop');
    const toggle      = document.getElementById('search-toggle');
    const form        = document.getElementById('search-form');
    const suggestions = document.getElementById('search-suggestions');

    if (!overlay || !toggle) return;

    function openSearch() {
        overlay.classList.remove('hidden');
        document.body.style.overflow = 'hidden';
        setTimeout(() => input && input.focus(), 60);
    }

    function closeSearch() {
        overlay.classList.add('hidden');
        document.body.style.overflow = '';
        if (input) input.value = '';
        if (suggestions) suggestions.innerHTML = '';
    }

    toggle.addEventListener('click', openSearch);
    if (closeBtn) closeBtn.addEventListener('click', closeSearch);
    if (backdrop) backdrop.addEventListener('click', closeSearch);

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && !overlay.classList.contains('hidden')) closeSearch();
    });

    // Live suggestions with debounce
    var debounceTimer;
    if (input) {
        input.addEventListener('input', function () {
            clearTimeout(debounceTimer);
            var q = input.value.trim();
            if (q.length < 2) { suggestions.innerHTML = ''; return; }
            debounceTimer = setTimeout(function () {
                fetch('/api/search?q=' + encodeURIComponent(q))
                    .then(function (r) { return r.ok ? r.json() : []; })
                    .then(function (items) {
                        if (!items || items.length === 0) {
                            suggestions.innerHTML = '<span>No results found.</span>';
                            return;
                        }
                        suggestions.innerHTML = items.map(function (item) {
                            return '<a href="/products/' + item.slug + '" class="block py-1.5 hover:text-neutral-900 transition-colors border-b border-neutral-50 last:border-0">'
                                + item.name
                                + ' <span class="text-neutral-400">\u2014 \u20a6' + Number(item.price).toLocaleString() + '</span></a>';
                        }).join('');
                    })
                    .catch(function () {});
            }, 250);
        });
    }

    // On submit, close overlay then let form navigate
    if (form) {
        form.addEventListener('submit', function () {
            var q = input ? input.value.trim() : '';
            if (!q) { event.preventDefault(); return; }
            overlay.classList.add('hidden');
            document.body.style.overflow = '';
        });
    }
}());

// ─── Mobile Menu ──────────────────────────────────────────────────────────
document.getElementById('mobile-menu-toggle')?.addEventListener('click', () => {
    const menu = document.getElementById('mobile-menu');
    menu?.classList.toggle('hidden');
});

// ─── Per-user header state ────────────────────────────────────────────────
// The storefront's big pages (home, category lists) are output-cached, so their HTML carries
// no per-user data. This pulls cart/wishlist counts, signed-in state, the wishlist hearts, and
// a valid antiforgery token from /site/header-state and applies them after the page loads.
const SiteHeader = (function () {
    let token = '';
    let loaded = null;

    function fillTokens(t) {
        if (!t) return;
        // Only fill EMPTY placeholders — never clobber a real token rendered on a non-cached page.
        document.querySelectorAll('input[name="__RequestVerificationToken"]').forEach(i => {
            if (!i.value) i.value = t;
        });
    }

    function apply(state) {
        if (!state) return;
        token = state.antiforgeryToken || '';
        fillTokens(token);

        updateCartBadge(state.cartCount || 0);

        const wb = document.getElementById('wishlist-badge');
        if (wb) {
            wb.textContent = state.wishlistCount || 0;
            wb.classList.toggle('hidden', !state.wishlistCount);
        }

        // Account UI: reveal the matching variant (defaults render the signed-out view).
        const authed = !!state.authenticated;
        document.querySelectorAll('[data-auth="in"]').forEach(el => el.classList.toggle('hidden', !authed));
        document.querySelectorAll('[data-auth="out"]').forEach(el => el.classList.toggle('hidden', authed));

        // Fill in the saved-to-wishlist hearts on product cards.
        const ids = new Set((state.wishlistProductIds || []).map(String));
        document.querySelectorAll('.wishlist-toggle').forEach(btn => {
            if (ids.has(String(btn.dataset.productId))) {
                const svg = btn.querySelector('svg');
                if (svg) svg.setAttribute('fill', 'currentColor');
            }
        });
    }

    function load() {
        if (!loaded) {
            loaded = fetch('/site/header-state', { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(r => (r.ok ? r.json() : null))
                .then(s => { apply(s); return s; })
                .catch(() => null);
        }
        return loaded;
    }

    // Returns a usable antiforgery token, fetching header-state first if needed (covers an
    // instant click before the initial load resolves, and pages with no token in the DOM yet).
    async function ensureToken() {
        if (token) return token;
        const onPage = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
        if (onPage) { token = onPage; return token; }
        await load();
        return token || document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    }

    load();
    return { ensureToken, reload: () => { loaded = null; return load(); }, ready: () => loaded || load() };
})();
window.SiteHeader = SiteHeader;

// ─── Size & length guide modal ────────────────────────────────────────────
(function () {
    const overlay = document.getElementById('size-guide-overlay');
    if (!overlay) return;

    const open  = () => { overlay.classList.remove('hidden'); document.body.style.overflow = 'hidden'; };
    const close = () => { overlay.classList.add('hidden'); document.body.style.overflow = ''; };

    document.querySelectorAll('[data-size-guide-open]').forEach(b => b.addEventListener('click', open));
    overlay.querySelectorAll('[data-size-guide-close]').forEach(b => b.addEventListener('click', close));
    // Click the backdrop (outside the panel) closes; Esc closes.
    overlay.addEventListener('click', (e) => { if (e.target === overlay) close(); });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !overlay.classList.contains('hidden')) close(); });

    // Tabs
    overlay.querySelectorAll('.sg-tab').forEach(tab => {
        tab.addEventListener('click', () => {
            const key = tab.dataset.sgTab;
            overlay.querySelectorAll('.sg-tab').forEach(t => {
                const on = t === tab;
                t.classList.toggle('border-neutral-900', on);
                t.classList.toggle('text-neutral-900', on);
                t.classList.toggle('border-transparent', !on);
                t.classList.toggle('text-neutral-400', !on);
            });
            overlay.querySelectorAll('.sg-panel').forEach(p =>
                p.classList.toggle('hidden', p.dataset.sgPanel !== key));
        });
    });
})();

// ─── Cart Badge Update ────────────────────────────────────────────────────
function updateCartBadge(count) {
    let badge = document.getElementById('cart-badge');
    // The badge isn't rendered server-side when the cart starts empty, so create
    // it on the first add — otherwise the count never appears until a page reload.
    if (!badge) {
        if (!count) return;
        const link = document.getElementById('cart-link');
        if (!link) return;
        badge = document.createElement('span');
        badge.id = 'cart-badge';
        badge.className = 'absolute -top-1.5 -right-1.5 bg-brand-500 text-white text-[10px] w-4 h-4 rounded-full flex items-center justify-center';
        link.appendChild(badge);
    }
    badge.textContent = count;
    badge.classList.toggle('hidden', !count);
}

// ─── Toast Notification ───────────────────────────────────────────────────
function showToast(message, duration = 3000) {
    const toast = document.getElementById('toast');
    const msg   = document.getElementById('toast-message');
    if (!toast || !msg) return;
    msg.textContent = message;
    toast.classList.remove('translate-y-4', 'opacity-0', 'pointer-events-none');
    toast.classList.add('translate-y-0', 'opacity-100');
    clearTimeout(toast._hideTimer);
    toast._hideTimer = setTimeout(() => {
        toast.classList.add('translate-y-4', 'opacity-0', 'pointer-events-none');
        toast.classList.remove('translate-y-0', 'opacity-100');
    }, duration);
}

// ─── Quick Add to Bag (list page) ─────────────────────────────────────────
document.querySelectorAll('.add-to-bag-quick').forEach(btn => {
    btn.addEventListener('click', async (e) => {
        e.preventDefault();
        e.stopPropagation();

        // Products with options need a selection — open the quick-view popup.
        if (btn.dataset.hasVariants === 'true') {
            openQuickView(btn.dataset.productSlug);
            return;
        }
        await addToBag(btn.dataset.productId, null, 1, btn);
    });
});

// Shared add-to-bag — used by the hover icon, the card "Add to Cart" button and the popup.
async function addToBag(productId, variantId, qty, btn) {
    const token = await SiteHeader.ensureToken();
    if (btn) btn.disabled = true;
    try {
        const body = new URLSearchParams({ productId, quantity: qty || 1, __RequestVerificationToken: token });
        if (variantId) body.append('variantId', variantId);
        const res = await fetch('/Cart/Add', { method: 'POST', body });
        const data = res.ok ? await res.json().catch(() => null) : null;
        if (data && data.success) {
            updateCartBadge(data.cartCount);
            showToast('Added to bag');
            return true;
        }
        showToast((data && data.message) || 'Sorry, we couldn’t add this to your bag.');
    } catch (err) {
        console.error('Add to bag failed', err);
        showToast('Sorry, we couldn’t add this to your bag.');
    } finally {
        if (btn) btn.disabled = false;
    }
    return false;
}

// ─── Card CTAs: "Add to Cart" (simple) and "Select Options" (variants) ─────
document.querySelectorAll('.add-to-cart-btn').forEach(btn => {
    btn.addEventListener('click', (e) => { e.preventDefault(); addToBag(btn.dataset.productId, null, 1, btn); });
});
document.querySelectorAll('.select-options-btn').forEach(btn => {
    btn.addEventListener('click', (e) => { e.preventDefault(); openQuickView(btn.dataset.productSlug); });
});

// Hover "Quick view" icon on product cards — opens the popup for any product.
document.querySelectorAll('.quick-view-btn').forEach(btn => {
    btn.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); openQuickView(btn.dataset.productSlug); });
});

// ─── Compare (client-side list + floating bar) ─────────────────────────────
const COMPARE_KEY = 'sg-compare';
function getCompare() { try { return JSON.parse(localStorage.getItem(COMPARE_KEY) || '[]'); } catch { return []; } }
function setCompare(a) { localStorage.setItem(COMPARE_KEY, JSON.stringify(a)); renderCompareBar(); syncCompareButtons(); }
function syncCompareButtons() {
    const a = getCompare();
    document.querySelectorAll('.compare-toggle').forEach(b => {
        const on = a.includes(b.dataset.productSlug);
        b.classList.toggle('bg-brand-500', on);
        b.classList.toggle('text-white', on);
    });
}
function renderCompareBar() {
    const bar = document.getElementById('compare-bar'); if (!bar) return;
    const a = getCompare();
    if (a.length === 0) { bar.classList.add('hidden'); return; }
    bar.classList.remove('hidden');
    const countEl = document.getElementById('compare-count'); if (countEl) countEl.textContent = a.length;
    const go = document.getElementById('compare-go'); if (go) go.href = '/products/compare?slugs=' + a.map(encodeURIComponent).join(',');
}
document.querySelectorAll('.compare-toggle').forEach(btn => {
    btn.addEventListener('click', (e) => {
        e.preventDefault(); e.stopPropagation();
        let a = getCompare(); const slug = btn.dataset.productSlug;
        if (a.indexOf(slug) !== -1) { a = a.filter(x => x !== slug); showToast('Removed from compare'); }
        else { if (a.length >= 4) { showToast('You can compare up to 4 items'); return; } a.push(slug); showToast('Added to compare'); }
        setCompare(a);
    });
});
document.getElementById('compare-clear')?.addEventListener('click', (e) => { e.preventDefault(); setCompare([]); });
// Keep the saved list in sync with a compare page opened directly (its slugs win).
if (window.__comparePageSlugs) { localStorage.setItem(COMPARE_KEY, JSON.stringify(window.__comparePageSlugs)); }
renderCompareBar();
syncCompareButtons();

// ─── Quick-view popup (Select Options) ─────────────────────────────────────
let qvState = null; // { price, salePrice, variants, selected:{}, variantId }
function openQuickView(slug) {
    const overlay = document.getElementById('quickview-overlay');
    if (!overlay) { window.location.href = '/products/' + slug; return; }
    fetch('/api/product-quickview?slug=' + encodeURIComponent(slug))
        .then(r => r.ok ? r.json() : Promise.reject())
        .then(d => { populateQuickView(d); overlay.classList.remove('hidden'); document.body.style.overflow = 'hidden'; })
        .catch(() => { window.location.href = '/products/' + slug; });
}
function closeQuickView() {
    const overlay = document.getElementById('quickview-overlay');
    if (overlay) overlay.classList.add('hidden');
    document.body.style.overflow = '';
    qvState = null;
}
function fmtNaira(n) { return '₦' + Math.round(n).toLocaleString('en-US'); }

function populateQuickView(d) {
    qvState = { productId: d.id, price: d.price, salePrice: d.salePrice, variants: d.variants || [], selected: {}, variantId: null, defaultImage: d.primaryImage };
    document.getElementById('qv-category').textContent = d.category || '';
    document.getElementById('qv-name').textContent = d.name;
    const img = document.getElementById('qv-image');
    img.src = d.primaryImage; img.alt = d.name;
    document.getElementById('qv-detail-link').href = '/products/' + d.slug;

    // Discount badge (product-level % off, mirrors the card badge)
    const badge = document.getElementById('qv-badge');
    if (badge) {
        const pct = (d.salePrice != null && d.price > 0) ? Math.round((d.price - d.salePrice) / d.price * 100) : 0;
        if (pct > 0) { badge.textContent = '-' + pct + '%'; badge.classList.remove('hidden'); }
        else badge.classList.add('hidden');
    }

    // Build the option dropdowns.
    const optsWrap = document.getElementById('qv-options');
    optsWrap.innerHTML = '';
    (d.attributes || []).forEach(attr => {
        const label = document.createElement('label');
        label.className = 'block text-xs tracking-[0.2em] uppercase text-neutral-500 mb-2';
        label.textContent = attr.name;
        const sel = document.createElement('select');
        sel.className = 'attr-select w-full border border-neutral-300 px-4 py-3 text-sm text-neutral-700 bg-white';
        sel.dataset.attr = attr.name;
        sel.innerHTML = '<option value="">Choose an option</option>' +
            attr.values.map(v => '<option value="' + v.replace(/"/g, '&quot;') + '">' + v + '</option>').join('');
        sel.addEventListener('change', () => {
            if (sel.value) qvState.selected[attr.name] = sel.value; else delete qvState.selected[attr.name];
            onQuickViewSelect();
        });
        const block = document.createElement('div');
        block.appendChild(label); block.appendChild(sel);
        optsWrap.appendChild(block);
    });

    document.getElementById('qv-msg').classList.add('hidden');
    updateQuickViewPrice();
}

function onQuickViewSelect() {
    const entries = Object.entries(qvState.selected);
    // Image: first variant matching the chosen attributes that has an image.
    const img = document.getElementById('qv-image');
    let url = qvState.defaultImage;
    if (entries.length) {
        const m = qvState.variants.find(v => v.imageUrl && entries.every(([k, val]) => v.attributes[k] === val));
        if (m) url = m.imageUrl;
    }
    if (img.getAttribute('src') !== url) img.src = url;

    // Fully resolved variant?
    const selects = document.querySelectorAll('#qv-options .attr-select');
    const allPicked = [...selects].every(s => s.value !== '');
    qvState.variantId = null;
    if (allPicked) {
        const match = qvState.variants.find(v => entries.every(([k, val]) => v.attributes[k] === val));
        if (match) qvState.variantId = match.id;
    }
    document.getElementById('qv-msg').classList.add('hidden');
    updateQuickViewPrice();
}

function updateQuickViewPrice() {
    let adj = 0;
    if (qvState.variantId) {
        const v = qvState.variants.find(x => x.id === qvState.variantId);
        if (v && v.priceAdjustment) adj = v.priceAdjustment;
    }
    const eff = document.getElementById('qv-price-effective');
    const reg = document.getElementById('qv-price-regular');
    const regular = qvState.price + adj;
    if (qvState.salePrice != null) {
        eff.textContent = fmtNaira(qvState.salePrice + adj); eff.classList.add('text-brand-600');
        reg.textContent = fmtNaira(regular); reg.classList.remove('hidden');
    } else {
        eff.textContent = fmtNaira(regular); eff.classList.remove('text-brand-600');
        reg.classList.add('hidden');
    }
}

document.getElementById('quickview-close')?.addEventListener('click', closeQuickView);
document.getElementById('quickview-overlay')?.addEventListener('click', (e) => {
    if (e.target.id === 'quickview-overlay') closeQuickView();
});
document.getElementById('qv-add')?.addEventListener('click', async (e) => {
    if (!qvState) return;
    const selects = document.querySelectorAll('#qv-options .attr-select');
    const allPicked = [...selects].every(s => s.value !== '');
    const msg = document.getElementById('qv-msg');
    if (!allPicked || !qvState.variantId) {
        if (msg) { msg.textContent = 'Please select all options before adding to bag.'; msg.classList.remove('hidden'); }
        return;
    }
    const v = qvState.variants.find(x => x.id === qvState.variantId);
    if (v && !v.inStock) {
        if (msg) { msg.textContent = 'Sorry, this option is out of stock.'; msg.classList.remove('hidden'); }
        return;
    }
    const btn = e.currentTarget;
    const ok = await addToBag(qvState.productId, qvState.variantId, 1, btn);
    if (ok) closeQuickView();
});

// ─── Wishlist Toggle (list page) ──────────────────────────────────────────
document.querySelectorAll('.wishlist-toggle').forEach(btn => {
    btn.addEventListener('click', async (e) => {
        e.preventDefault();
        e.stopPropagation();

        const productId = btn.dataset.productId;
        const token = await SiteHeader.ensureToken();

        try {
            const res = await fetch('/Wishlist/Toggle', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: `productId=${productId}&__RequestVerificationToken=${encodeURIComponent(token)}`
            });
            const data = await res.json();
            if (data.success) {
                const svg = btn.querySelector('svg');
                if (svg) {
                    svg.setAttribute('fill', data.added ? 'currentColor' : 'none');
                }
            }
        } catch (err) {
            console.error('Wishlist toggle failed', err);
        }
    });
});

// ── Auto-submit selects (CSP-safe; replaces inline onchange) ──────────────────
document.querySelectorAll('[data-autosubmit]').forEach(function (el) {
    el.addEventListener('change', function () {
        const f = el.form || document.getElementById('filter-form');
        if (f) f.submit();
    });
});

// ── Profile edit toggle (CSP-safe; replaces inline onclick) ───────────────────
(function () {
    const toggle = document.getElementById('edit-toggle');
    const form = document.getElementById('edit-form');
    const cancel = document.getElementById('edit-cancel');
    if (toggle && form) toggle.addEventListener('click', function () {
        form.classList.toggle('hidden'); toggle.classList.toggle('hidden');
    });
    if (cancel && form && toggle) cancel.addEventListener('click', function () {
        form.classList.add('hidden'); toggle.classList.remove('hidden');
    });
}());

// ─── Product listing: "View more" (progressive load) + Back to top ────────────
// Cards on first paint are wired by the load-time binders above. Cards appended by
// "View more" are fresh DOM, so re-wire each new card's actions here (scoped to the
// card, so existing cards are never double-bound).
function bindProductCards(root) {
    root.querySelectorAll('.add-to-bag-quick').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.preventDefault(); e.stopPropagation();
            if (btn.dataset.hasVariants === 'true') { openQuickView(btn.dataset.productSlug); return; }
            addToBag(btn.dataset.productId, null, 1, btn);
        });
    });
    root.querySelectorAll('.add-to-cart-btn').forEach(btn => {
        btn.addEventListener('click', (e) => { e.preventDefault(); addToBag(btn.dataset.productId, null, 1, btn); });
    });
    root.querySelectorAll('.select-options-btn').forEach(btn => {
        btn.addEventListener('click', (e) => { e.preventDefault(); openQuickView(btn.dataset.productSlug); });
    });
    root.querySelectorAll('.quick-view-btn').forEach(btn => {
        btn.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); openQuickView(btn.dataset.productSlug); });
    });
    root.querySelectorAll('.compare-toggle').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.preventDefault(); e.stopPropagation();
            let a = getCompare(); const slug = btn.dataset.productSlug;
            if (a.indexOf(slug) !== -1) { a = a.filter(x => x !== slug); showToast('Removed from compare'); }
            else { if (a.length >= 4) { showToast('You can compare up to 4 items'); return; } a.push(slug); showToast('Added to compare'); }
            setCompare(a);
        });
    });
    root.querySelectorAll('.wishlist-toggle').forEach(btn => {
        btn.addEventListener('click', async (e) => {
            e.preventDefault(); e.stopPropagation();
            const productId = btn.dataset.productId;
            const token = await SiteHeader.ensureToken();
            try {
                const res = await fetch('/Wishlist/Toggle', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                    body: `productId=${productId}&__RequestVerificationToken=${encodeURIComponent(token)}`
                });
                const data = await res.json();
                if (data.success) { const svg = btn.querySelector('svg'); if (svg) svg.setAttribute('fill', data.added ? 'currentColor' : 'none'); }
            } catch (err) { console.error('Wishlist toggle failed', err); }
        });
    });
    syncCompareButtons();
    // Pre-fill "saved to wishlist" hearts for this batch from the cached header state.
    SiteHeader.ready?.().then(s => {
        if (!s) return;
        const ids = new Set((s.wishlistProductIds || []).map(String));
        root.querySelectorAll('.wishlist-toggle').forEach(btn => {
            if (ids.has(String(btn.dataset.productId))) {
                const svg = btn.querySelector('svg');
                if (svg) svg.setAttribute('fill', 'currentColor');
            }
        });
    });
}

(function () {
    const btn = document.getElementById('load-more-btn');
    const grid = document.getElementById('product-grid');
    const shownEl = document.getElementById('shown-count');
    if (btn && grid) {
        btn.addEventListener('click', async () => {
            const url = btn.dataset.url;
            let next = parseInt(btn.dataset.next, 10);
            const totalPages = parseInt(btn.dataset.total, 10);
            const totalCount = parseInt(btn.dataset.count, 10);
            const pageSize = parseInt(btn.dataset.size, 10);
            const label = btn.querySelector('[data-label]');
            const original = label ? label.textContent : '';
            btn.disabled = true;
            if (label) label.textContent = 'Loading…';
            try {
                const sep = url.indexOf('?') === -1 ? '?' : '&';
                const res = await fetch(`${url}${sep}page=${next}&partial=cards`, { headers: { 'X-Requested-With': 'XMLHttpRequest' } });
                if (!res.ok) throw new Error('bad status ' + res.status);
                const html = (await res.text()).trim();
                const tmp = document.createElement('div');
                tmp.innerHTML = html;
                const newCards = Array.from(tmp.children);
                newCards.forEach(card => { grid.appendChild(card); bindProductCards(card); });
                if (shownEl) shownEl.textContent = Math.min(next * pageSize, totalCount);
                next += 1;
                btn.dataset.next = next;
                if (next > totalPages) {
                    const wrap = document.getElementById('load-more-wrap');
                    (wrap || btn).remove();
                } else {
                    btn.disabled = false;
                    if (label) label.textContent = original;
                }
            } catch (err) {
                console.error('View more failed', err);
                btn.disabled = false;
                if (label) label.textContent = original;
                showToast('Sorry, we couldn’t load more pieces. Please try again.');
            }
        });
    }
    const top = document.getElementById('back-to-top');
    if (top) top.addEventListener('click', (e) => { e.preventDefault(); window.scrollTo({ top: 0, behavior: 'smooth' }); });
}());

// ─── Welcome / exit-intent popup (first-order discount → newsletter signup) ────
(function () {
    const pop = document.getElementById('welcome-popup');
    if (!pop) return;                                   // only present when the offer is enabled
    const KEY = 'sg-welcome-v1';
    if (localStorage.getItem(KEY)) return;              // shown / dismissed / subscribed already
    if (/^\/(checkout|cart)/i.test(location.pathname)) return; // never interrupt a purchase

    let shown = false;
    function show() {
        if (shown) return;
        shown = true;
        localStorage.setItem(KEY, 'shown');             // at most once per browser
        pop.classList.remove('hidden');
        const inp = pop.querySelector('[data-welcome-email]');
        if (inp) setTimeout(() => inp.focus(), 50);
    }
    function hide() { pop.classList.add('hidden'); }

    // Desktop exit-intent: cursor leaves the viewport past the top edge.
    document.addEventListener('mouseout', (e) => { if (!e.relatedTarget && e.clientY <= 0) show(); });
    // Touch / no-exit-intent fallback: after 30s on the site.
    setTimeout(show, 30000);

    pop.querySelectorAll('[data-welcome-close]').forEach(b => b.addEventListener('click', hide));
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !pop.classList.contains('hidden')) hide(); });

    const form = pop.querySelector('[data-welcome-submit]');
    const msg = pop.querySelector('[data-welcome-msg]');
    if (form) form.addEventListener('submit', async (e) => {
        e.preventDefault();
        const email = (pop.querySelector('[data-welcome-email]').value || '').trim();
        const btn = form.querySelector('button[type=submit]');
        btn.disabled = true; if (msg) msg.textContent = '';
        try {
            const res = await fetch('/Home/WelcomeOffer', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: 'email=' + encodeURIComponent(email)
            });
            const data = await res.json();
            if (data.success) {
                localStorage.setItem(KEY, 'subscribed');
                if (data.code) {
                    pop.querySelector('[data-welcome-form]').classList.add('hidden');
                    pop.querySelector('[data-welcome-success-msg]').textContent = data.message;
                    pop.querySelector('[data-welcome-code]').textContent = data.code;
                    pop.querySelector('[data-welcome-success]').classList.remove('hidden');
                } else if (msg) {
                    msg.textContent = data.message;
                    msg.className = 'text-xs text-center mt-3 text-emerald-600';
                    setTimeout(hide, 1800);
                }
            } else if (msg) {
                msg.textContent = data.message;
                msg.className = 'text-xs text-center mt-3 text-red-500';
                btn.disabled = false;
            }
        } catch {
            if (msg) { msg.textContent = 'Something went wrong. Please try again.'; msg.className = 'text-xs text-center mt-3 text-red-500'; }
            btn.disabled = false;
        }
    });
}());
