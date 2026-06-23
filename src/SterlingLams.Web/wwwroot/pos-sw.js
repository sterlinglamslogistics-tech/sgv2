/*
 * Sterlin Glams POS — service worker.
 * Scope: /Pos. Makes the till load instantly and survive a flaky/lost connection:
 *  - app shell + static assets cached (so the page loads offline),
 *  - product & logo images cached (so cards and the receipt logo show offline),
 *  - catalogue data + offline sales are handled in-page by pos-offline.js (IndexedDB).
 * POSTs and JSON endpoints stay network-only here.
 *
 * Bump CACHE when shell assets change; IMG_CACHE persists across shell updates.
 */
const CACHE = 'sgpos-shell-v9';
const IMG_CACHE = 'sgpos-img-v1';

const PRECACHE = [
  '/css/app.css',
  '/js/pos-pwa.js',
  '/js/pos-offline.js',
  '/js/jsbarcode.min.js',
  '/pos.webmanifest',
  '/icons/pos-192.png',
  '/icons/pos-512.png',
  '/favicon-32.png',
  '/apple-touch-icon.png',
  '/images/sg-logo.png'   // offline receipt logo fallback
];

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(CACHE)
      .then((cache) => Promise.allSettled(PRECACHE.map((u) => cache.add(u))))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE && k !== IMG_CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return; // never touch POST/checkout/etc.

  const url = new URL(req.url);

  // Images (product thumbnails on Cloudinary, the logo, icons) — cache-first, ANY origin, so cards
  // and the receipt logo render offline. Opaque (no-cors) responses are cacheable.
  const isImage = req.destination === 'image'
    || /\.(png|jpe?g|webp|gif|svg)$/i.test(url.pathname)
    || url.hostname.endsWith('res.cloudinary.com');
  if (isImage) { event.respondWith(handleImage(req)); return; }

  if (url.origin !== self.location.origin) return; // other cross-origin (fonts/CDN) pass through

  // App navigations: network-first, fall back to the cached shell offline.
  if (req.mode === 'navigate') {
    event.respondWith(
      fetch(req)
        .then((res) => { cachePut(req, res.clone()); return res; })
        .catch(() => caches.match(req).then((c) => c || caches.match('/Pos')))
    );
    return;
  }

  // Static shell assets: stale-while-revalidate.
  if (/\.(css|js|woff2?|ttf|ico|webmanifest)$/i.test(url.pathname)) {
    event.respondWith(
      caches.match(req).then((cached) => {
        const network = fetch(req).then((res) => { cachePut(req, res.clone()); return res; }).catch(() => cached);
        return cached || network;
      })
    );
  }
  // Everything else (JSON API endpoints): default network — pos-offline.js serves these offline.
});

async function handleImage(req) {
  const imgCache = await caches.open(IMG_CACHE);
  // ignoreVary: Cloudinary sends "Vary: Accept", and the <img> request's Accept differs from the
  // warm fetch's — without this the cached image never matches and stays blank offline.
  const hit = await imgCache.match(req, { ignoreVary: true });
  if (hit) return hit;
  const pre = await caches.match(req, { ignoreVary: true }); // precached icons / logo
  if (pre) return pre;
  try {
    const res = await fetch(req);
    try { await imgCache.put(req, res.clone()); } catch (e) { /* opaque/partial — ignore */ }
    return res;
  } catch (e) {
    return Response.error(); // offline + not cached → broken img (acceptable)
  }
}

function cachePut(req, res) {
  if (!res || res.status !== 200 || res.type === 'opaque') return;
  caches.open(CACHE).then((cache) => cache.put(req, res)).catch(() => {});
}
