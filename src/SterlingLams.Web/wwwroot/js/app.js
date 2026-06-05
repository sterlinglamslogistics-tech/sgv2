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

// ─── Cart Badge Update ────────────────────────────────────────────────────
function updateCartBadge(count) {
    const badge = document.getElementById('cart-badge');
    if (!badge) return;
    badge.textContent = count;
    badge.classList.toggle('hidden', count === 0);
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

// ─── Wishlist Toggle (list page) ──────────────────────────────────────────
document.querySelectorAll('.wishlist-toggle').forEach(btn => {
    btn.addEventListener('click', async (e) => {
        e.preventDefault();
        e.stopPropagation();

        const productId = btn.dataset.productId;
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';

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
