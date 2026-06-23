/*
 * Sterlin Glams POS — offline data layer + sale queue (Phase 1 + 2).
 *
 * Phase 1: caches a /Pos/Snapshot (catalogue + store stock + categories + discount reasons +
 *   customers) in IndexedDB and shims window.fetch so the read endpoints fall back to it offline.
 * Phase 2: when /Pos/Checkout can't reach the server, the sale is captured locally (idempotent
 *   client id), the local stock is decremented and a receipt-less "saved offline" success is
 *   returned so the till keeps selling. Queued sales auto-sync the moment the network returns and
 *   via a manual "Sync with cloud" menu button, with a "Successfully synced HH:MM" toast.
 *
 * Loaded before the page's inline script so the shim is active first. Exposes window.SGPOS.
 */
(function () {
  'use strict';
  if (!location.pathname.toLowerCase().startsWith('/pos')) return;

  var DB_NAME = 'sgpos', STORE = 'kv', SNAP_KEY = 'snapshot', QUEUE_KEY = 'queue';
  var SYNC_INTERVAL_MS = 30 * 60 * 1000; // periodic auto-sync cadence (30 minutes)
  var mem = null;          // in-memory snapshot
  var queue = [];          // in-memory outbound sale queue
  var syncing = false;
  var realFetch = window.fetch.bind(window);

  // ── IndexedDB (best-effort) ────────────────────────────────────────────────
  function openDb() {
    return new Promise(function (resolve, reject) {
      try {
        var req = indexedDB.open(DB_NAME, 1);
        req.onupgradeneeded = function () { req.result.createObjectStore(STORE); };
        req.onsuccess = function () { resolve(req.result); };
        req.onerror = function () { reject(req.error); };
      } catch (e) { reject(e); }
    });
  }
  function idbGet(key) {
    return openDb().then(function (db) { return new Promise(function (res) {
      var r = db.transaction(STORE, 'readonly').objectStore(STORE).get(key);
      r.onsuccess = function () { res(r.result || null); }; r.onerror = function () { res(null); };
    }); }).catch(function () { return null; });
  }
  function idbPut(key, val) {
    return openDb().then(function (db) { return new Promise(function (res) {
      var tx = db.transaction(STORE, 'readwrite'); tx.objectStore(STORE).put(val, key);
      tx.oncomplete = function () { res(true); }; tx.onerror = function () { res(false); };
    }); }).catch(function () { return false; });
  }
  function saveQueue() { return idbPut(QUEUE_KEY, queue); }

  // ── Local responders (mirror the read endpoints) ───────────────────────────
  function jsonResponse(data) {
    return new Response(JSON.stringify(data), { status: 200, headers: { 'Content-Type': 'application/json; charset=utf-8' } });
  }
  function ci(s) { return (s || '').toString().toLowerCase(); }

  function localSearch(params) {
    if (!mem) return [];
    var q = ci(params.get('q')).trim(), cat = params.get('categoryId');
    return mem.products.filter(function (p) {
      if (cat && String(p.categoryId) !== String(cat)) return false;
      if (!q) return true;
      return ci(p.name).indexOf(q) >= 0 || ci(p.sku).indexOf(q) >= 0 || ci(p.barcode).indexOf(q) >= 0;
    }).slice(0, 40);
  }
  function localCustomers(params) {
    if (!mem) return [];
    var q = ci(params.get('q')).trim(), list = mem.customers;
    if (!q) list = list.slice(0, 20);
    else list = list.filter(function (c) { return ci(c.name).indexOf(q) >= 0 || ci(c.phone).indexOf(q) >= 0 || ci(c.email).indexOf(q) >= 0; }).slice(0, 15);
    return list.map(function (c) { return { id: c.id, name: c.name, phone: c.phone }; });
  }
  function localFor(path, params) {
    var p = path.toLowerCase();
    if (p === '/pos/search') return localSearch(params);
    if (p === '/pos/categories') return mem ? mem.categories : [];
    if (p === '/pos/discountreasons') return mem ? mem.discountReasons : [];
    if (p === '/pos/customersearch') return localCustomers(params);
    return undefined;
  }

  // ── Offline checkout capture ────────────────────────────────────────────────
  function rid() { return (Date.now().toString(36) + Math.random().toString(36).slice(2, 10)); }
  function findProduct(id) { return mem ? mem.products.find(function (p) { return p.id === id; }) : null; }

  function captureOfflineSale(body) {
    var sale;
    try { sale = JSON.parse(body || '{}'); } catch (e) { return { success: false, message: 'Bad sale data.' }; }
    var items = sale.items || [];
    if (!items.length) return { success: false, message: 'Cart is empty.' };

    var subtotal = 0, discount = 0, lines = [];
    items.forEach(function (it) {
      var prod = findProduct(it.productId);
      var unit = prod ? prod.price : 0;
      var variantName = null;
      if (prod && it.variantId && prod.variants) {
        var v = prod.variants.find(function (x) { return x.id === it.variantId; });
        if (v) { if (v.priceAdjustment) unit += v.priceAdjustment; variantName = v.name; }
      }
      var qty = Math.max(1, it.quantity || 1);
      var lineDisc = Math.max(0, Math.min(it.discountAmount || 0, unit * qty));
      subtotal += unit * qty;
      discount += lineDisc;
      lines.push({ name: prod ? prod.name : ('#' + it.productId), variant: variantName, qty: qty, lineTotal: unit * qty - lineDisc });
    });
    var total = subtotal - discount;
    var tendered = sale.amountTendered > 0 ? sale.amountTendered : total;
    var change = Math.max(0, tendered - total);

    var clientId = rid();
    var number = 'OFFLINE-' + clientId.slice(-6).toUpperCase();
    queue.push({
      clientId: clientId,
      paymentMethod: sale.paymentMethod || 'Cash',
      amountTendered: sale.amountTendered || 0,
      customerUserId: sale.customerUserId || null,
      createdAt: new Date().toISOString(),
      items: items
    });
    saveQueue();

    // Decrement the local stock snapshot so the catalogue reflects what's left this shift.
    items.forEach(function (it) {
      var prod = findProduct(it.productId);
      if (prod) prod.stock = Math.max(0, (prod.stock || 0) - Math.max(1, it.quantity || 1));
    });
    if (mem) idbPut(SNAP_KEY, mem);

    var cust = sale.customerUserId && mem ? mem.customers.find(function (c) { return c.id === sale.customerUserId; }) : null;
    var receipt = {
      number: number, offline: true,
      siteName: (mem && mem.siteName) || 'Sterlin Glams',
      storeName: (mem && mem.storeName) || '',
      registerName: (mem && mem.registerName) || '',
      cashierName: (mem && mem.cashierName) || '',
      // Offline receipt inlines the logo as a data URI, which only works for a same-origin image
      // (cross-origin Cloudinary responses are opaque/unreadable). Fall back to the bundled logo.
      logoUrl: (mem && mem.logoUrl && mem.logoUrl.charAt(0) === '/') ? mem.logoUrl : '/images/sg-logo.png',
      header: (mem && mem.receiptHeader) || '',
      footer: (mem && mem.receiptFooter) || 'Thank you for shopping with us!',
      dateStr: new Date().toLocaleString([], { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }),
      payment: sale.paymentMethod || 'Cash',
      customer: cust ? (cust.name + (cust.phone ? ' · ' + cust.phone : '')) : null,
      lines: lines, total: total, tendered: tendered, change: change
    };

    updateSyncUi();
    return { success: true, offline: true, orderNumber: number, total: total, change: change, receipt: receipt };
  }

  // ── fetch shim ──────────────────────────────────────────────────────────────
  window.fetch = function (input, init) {
    try {
      var method = ((init && init.method) || (input && input.method) || 'GET').toUpperCase();
      var urlStr = typeof input === 'string' ? input : (input && input.url) || '';
      var url = new URL(urlStr, location.origin);
      var path = url.pathname.toLowerCase();

      // Offline-capture checkout: network-first; queue locally only on a true network failure.
      if (method === 'POST' && path === '/pos/checkout' && url.origin === location.origin) {
        return realFetch(input, init).catch(function () {
          return jsonResponse(captureOfflineSale(init && init.body));
        });
      }

      // Read endpoints: network-first, fall back to the snapshot offline.
      if (method === 'GET' && url.origin === location.origin && localFor(path, url.searchParams) !== undefined) {
        return realFetch(input, init).catch(function () { return jsonResponse(localFor(path, url.searchParams) || []); });
      }

      return realFetch(input, init);
    } catch (e) { return realFetch(input, init); }
  };

  // ── Snapshot refresh ──────────────────────────────────────────────────────
  function refreshSnapshot() {
    return realFetch('/Pos/Snapshot', { headers: { 'X-Requested-With': 'fetch' } })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (data) {
        if (!data || data.ok === false) return false;
        // Re-apply pending offline deductions so the fresh snapshot still reflects un-synced sales.
        applyQueueToSnapshot(data);
        mem = data; idbPut(SNAP_KEY, data); updateBanner(); warmImages(data); return true;
      })
      .catch(function () { return false; });
  }

  // Warm the image cache (via the service worker) so product cards + the receipt logo show offline.
  // Throttled + once per page load (the SW cache persists across refreshes).
  var warmed = false;
  function warmImages(snap) {
    if (warmed || !snap || !navigator.onLine) return;
    warmed = true;
    var urls = [];
    if (snap.logoUrl) urls.push(snap.logoUrl);
    (snap.products || []).forEach(function (p) { if (p.image) urls.push(p.image); });
    urls = urls.filter(function (u, i) { return urls.indexOf(u) === i; });
    // Warm via <img> (not fetch) — the POS CSP is connect-src 'self', which blocks cross-origin
    // fetch() to Cloudinary, but img-src allows https images. The service worker caches them.
    var idx = 0;
    function next() {
      if (idx >= urls.length) return;
      var im = new Image();
      im.onload = im.onerror = function () { next(); };
      im.src = urls[idx++];
    }
    for (var i = 0; i < 6 && i < urls.length; i++) next(); // 6 in flight
  }
  function applyQueueToSnapshot(snap) {
    queue.forEach(function (s) { (s.items || []).forEach(function (it) {
      var prod = snap.products.find(function (p) { return p.id === it.productId; });
      if (prod) prod.stock = Math.max(0, (prod.stock || 0) - Math.max(1, it.quantity || 1));
    }); });
  }

  // ── Sync engine ─────────────────────────────────────────────────────────────
  function token() { return (document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || ''; }

  function sync(manual) {
    if (syncing) return Promise.resolve({ ok: false, busy: true });
    if (!queue.length) { updateSyncUi(); if (manual) toast('Nothing to sync — all sales are up to date.'); return Promise.resolve({ ok: true, synced: 0 }); }
    if (!navigator.onLine) { if (manual) toast("You're offline — sales will sync when you reconnect.", true); return Promise.resolve({ ok: false, offline: true }); }

    syncing = true; updateSyncUi();
    var batch = queue.slice();
    return realFetch('/Pos/SyncSales', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token() },
      body: JSON.stringify({ sales: batch })
    })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (resp) {
        if (!resp || !resp.results) { if (manual) toast('Could not sync right now. Will retry.', true); return { ok: false }; }
        var doneIds = {}, oversold = 0;
        resp.results.forEach(function (res) {
          if (res.success) { doneIds[res.clientId] = true; if (res.oversold) oversold++; }
        });
        queue = queue.filter(function (s) { return !doneIds[s.clientId]; });
        saveQueue();
        var n = Object.keys(doneIds).length;
        var t = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        if (n > 0) toast('Successfully synced ' + n + ' sale' + (n === 1 ? '' : 's') + ' at ' + t + (oversold ? ' · ' + oversold + ' need review' : ''));
        else if (manual) toast('Could not sync right now. Will retry.', true);
        refreshSnapshot();
        return { ok: true, synced: n, oversold: oversold };
      })
      .catch(function () { if (manual) toast('Could not sync right now. Will retry.', true); return { ok: false }; })
      .finally(function () { syncing = false; updateSyncUi(); });
  }

  // ── UI: offline banner, sync indicators, toast ───────────────────────────────
  var bar, toastEl;
  function syncedText() {
    if (!mem || !mem.syncedAt) return 'not yet synced';
    return 'synced ' + new Date(mem.syncedAt).toLocaleString([], { hour: '2-digit', minute: '2-digit', day: '2-digit', month: 'short' });
  }
  function ensureBar() {
    if (bar) return bar;
    bar = document.createElement('div');
    bar.id = 'sgpos-offline-bar';
    bar.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:9999;display:none;background:#92400e;color:#fff;' +
      'font:500 13px/1.4 ui-sans-serif,system-ui,sans-serif;padding:7px 14px;text-align:center;box-shadow:0 1px 4px rgba(0,0,0,.2)';
    document.body.appendChild(bar);
    return bar;
  }
  function updateBanner() {
    var b = ensureBar();
    if (navigator.onLine) { b.style.display = 'none'; document.body.style.paddingTop = ''; }
    else {
      var pending = queue.length ? ' · ' + queue.length + ' sale' + (queue.length === 1 ? '' : 's') + ' waiting to sync' : '';
      b.textContent = '⚠ Offline — selling from saved catalogue (' + syncedText() + ')' + pending + '.';
      b.style.display = 'block'; document.body.style.paddingTop = b.offsetHeight + 'px';
    }
  }
  function updateSyncUi() {
    var badge = document.getElementById('sync-pending-count');
    var status = document.getElementById('sync-status');
    if (badge) { if (queue.length) { badge.textContent = queue.length; badge.classList.remove('hidden'); } else badge.classList.add('hidden'); }
    if (status) status.textContent = syncing ? 'Syncing…' : (queue.length ? (queue.length + ' sale(s) waiting to sync') : ('All sales synced · ' + syncedText()));
    updateBanner();
  }
  function toast(msg, warn) {
    if (!toastEl) {
      toastEl = document.createElement('div');
      toastEl.style.cssText = 'position:fixed;left:50%;bottom:22px;transform:translateX(-50%);z-index:10000;display:none;' +
        'padding:10px 18px;border-radius:8px;color:#fff;font:500 13px/1.4 ui-sans-serif,system-ui,sans-serif;box-shadow:0 4px 16px rgba(0,0,0,.25);max-width:90vw;text-align:center';
      document.body.appendChild(toastEl);
    }
    toastEl.style.background = warn ? '#b45309' : '#047857';
    toastEl.textContent = msg;
    toastEl.style.display = 'block';
    clearTimeout(toastEl._t);
    toastEl._t = setTimeout(function () { toastEl.style.display = 'none'; }, 4200);
  }

  function wireMenuButton() {
    var btn = document.getElementById('menu-sync-btn');
    if (btn && !btn._wired) { btn._wired = true; btn.addEventListener('click', function () { sync(true); }); }
  }

  // ── Offline receipt (self-contained: inlined logo + barcode so it prints with no network) ─────
  function esc(s) { return (s == null ? '' : String(s)).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function money(n) { return '₦' + Number(n || 0).toLocaleString(); }

  function logoDataUrl(url) {
    if (!url) return Promise.resolve('');
    return realFetch(url).then(function (r) { return r.blob(); }).then(function (blob) {
      return new Promise(function (res) { var fr = new FileReader(); fr.onload = function () { res(fr.result); }; fr.onerror = function () { res(''); }; fr.readAsDataURL(blob); });
    }).catch(function () { return ''; });
  }
  function barcodeSvg(text) {
    try {
      var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      window.JsBarcode(svg, text, { format: 'CODE128', displayValue: true, width: 1, height: 34, margin: 0, fontSize: 10 });
      svg.setAttribute('style', 'display:block;margin:0 auto;max-width:100%;height:auto');
      return new XMLSerializer().serializeToString(svg);
    } catch (e) { return '<div style="font-size:11px">' + esc(text) + '</div>'; }
  }

  function printOfflineReceipt(r) {
    if (!r) return;
    logoDataUrl(r.logoUrl).then(function (logo) {
      var bc = barcodeSvg(r.number);
      var rows = (r.lines || []).map(function (l) {
        return '<tr><td>' + l.qty + '× ' + esc(l.name) + (l.variant ? ' (' + esc(l.variant) + ')' : '') + '</td><td class="right">' + money(l.lineTotal) + '</td></tr>';
      }).join('');
      var html = '<!doctype html><html><head><meta charset="utf-8"><title>Receipt ' + esc(r.number) + '</title><style>' +
        "body{font-family:'Courier New',monospace;color:#111;margin:0;padding:16px}.r{width:300px;margin:0 auto}" +
        "h1{font-size:16px;text-align:center;margin:0 0 2px;letter-spacing:2px}.c{text-align:center}.muted{color:#555}" +
        ".row{display:flex;justify-content:space-between;font-size:12px;margin:2px 0}hr{border:none;border-top:1px dashed #999;margin:10px 0}" +
        "table{width:100%;border-collapse:collapse;font-size:12px}td{padding:2px 0}.right{text-align:right}.total{font-size:14px;font-weight:bold}" +
        ".btns{width:300px;margin:16px auto 0;display:flex;gap:8px}.btns button{flex:1;padding:8px;font-size:12px;border:1px solid #ccc;background:#fff;cursor:pointer;border-radius:3px}" +
        "@media print{.btns{display:none}body{padding:0}}</style></head><body><div class=\"r\">" +
        (logo ? '<img src="' + logo + '" alt="" style="max-width:200px;max-height:80px;display:block;margin:0 auto 6px;object-fit:contain"/>' : '') +
        '<h1>' + esc((r.siteName || '').toUpperCase()) + '</h1>' +
        '<p class="c muted" style="font-size:12px;margin:0 0 6px">' + esc(r.storeName) + '</p>' +
        (r.header ? '<p class="c muted" style="font-size:11px;margin:0 0 8px">' + esc(r.header) + '</p>' : '') +
        '<p class="c" style="font-size:11px;color:#b45309;margin:0 0 6px">OFFLINE SALE — pending sync</p><hr/>' +
        '<div class="row"><span>Receipt</span><span>' + esc(r.number) + '</span></div>' +
        '<div class="row"><span>Date</span><span>' + esc(r.dateStr) + '</span></div>' +
        '<div class="row"><span>Payment</span><span>' + esc(r.payment) + '</span></div>' +
        (r.cashierName ? '<div class="row"><span>Served by</span><span>' + esc(r.cashierName) + '</span></div>' : '') +
        (r.customer ? '<div class="row"><span>Customer</span><span>' + esc(r.customer) + '</span></div>' : '') +
        '<hr/><table>' + rows + '</table><hr/>' +
        '<div class="row total"><span>TOTAL</span><span>' + money(r.total) + '</span></div>' +
        '<div class="row"><span>Tendered</span><span>' + money(r.tendered) + '</span></div>' +
        '<div class="row"><span>Change</span><span>' + money(r.change) + '</span></div>' +
        '<hr/><p class="c muted" style="font-size:11px;white-space:pre-line">' + esc(r.footer) + '</p>' +
        '<div style="margin-top:8px">' + bc + '</div></div>' +
        '<div class="btns"><button onclick="window.print()">Print</button><button onclick="window.close()">Close</button></div>' +
        '<script>window.onload=function(){setTimeout(function(){window.print();},250);};<\/script></body></html>';
      var w = window.open('', '_blank');
      if (!w) { alert('Allow pop-ups to print the receipt.'); return; }
      w.document.open(); w.document.write(html); w.document.close();
    });
  }

  // ── Init ──────────────────────────────────────────────────────────────────
  function init() {
    Promise.all([idbGet(SNAP_KEY), idbGet(QUEUE_KEY)]).then(function (vals) {
      if (vals[0] && !mem) mem = vals[0];
      if (Array.isArray(vals[1])) queue = vals[1];
      wireMenuButton();
      updateSyncUi();
      if (navigator.onLine) refreshSnapshot().then(function () { if (queue.length) sync(false); });
    });
    window.addEventListener('online', function () { updateBanner(); refreshSnapshot().then(function () { sync(false); }); });
    window.addEventListener('offline', updateBanner);

    // Periodic auto-sync (default every 30 min): refresh the catalogue/stock snapshot and flush any
    // queued offline sales, even on a till that stays online all day (no offline->online toggle).
    setInterval(function () {
      if (!navigator.onLine) return;
      refreshSnapshot().then(function () { if (queue.length) sync(false); });
    }, SYNC_INTERVAL_MS);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();

  window.SGPOS = {
    refresh: refreshSnapshot,
    snapshot: function () { return mem; },
    queue: function () { return queue.slice(); },
    pending: function () { return queue.length; },
    sync: function () { return sync(true); },
    printOfflineReceipt: printOfflineReceipt,
    lastSyncedText: syncedText,
    isOnline: function () { return navigator.onLine; }
  };
})();
