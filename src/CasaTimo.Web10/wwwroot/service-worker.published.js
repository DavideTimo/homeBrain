// Production service worker — cache-first with versioned cache
self.importScripts('./service-worker-assets.js');

self.addEventListener('install',  e => e.waitUntil(onInstall(e)));
self.addEventListener('activate', e => e.waitUntil(onActivate(e)));
self.addEventListener('fetch',    e => e.respondWith(onFetch(e)));
self.addEventListener('message',  e => { if (e.data?.type === 'SKIP_WAITING') self.skipWaiting(); });

const cachePrefix = 'casatimo-v';
const cacheName   = `${cachePrefix}${self.assetsManifest.version}`;

const includePatterns = [/\.dll$/, /\.wasm/, /\.html$/, /\.js$/, /\.json$/, /\.css$/, /\.woff2?$/, /\.png$/, /\.ico$/];
const excludePatterns = [/^service-worker\.js$/];

async function onInstall(event) {
    const assets = self.assetsManifest.assets
        .filter(a => includePatterns.some(p => p.test(a.url)))
        .filter(a => !excludePatterns.some(p => p.test(a.url)))
        .map(a => new Request(a.url, { integrity: a.hash, cache: 'no-cache' }));

    await caches.open(cacheName).then(c => c.addAll(assets));
}

async function onActivate() {
    const keys = await caches.keys();
    await Promise.all(keys
        .filter(k => k.startsWith(cachePrefix) && k !== cacheName)
        .map(k => caches.delete(k)));
}

async function onFetch(event) {
    if (event.request.method !== 'GET') return fetch(event.request);

    // API calls: always network
    if (event.request.url.includes('/api/') || event.request.url.includes('/auth/')) {
        return fetch(event.request);
    }

    const cache = await caches.open(cacheName);
    const request = event.request.mode === 'navigate' ? 'index.html' : event.request;
    return (await cache.match(request)) ?? fetch(event.request);
}

self.addEventListener('push', event => {
    const data = event.data?.json() ?? { title: 'Casa Timò', body: 'Notifica' };
    event.waitUntil(self.registration.showNotification(data.title, {
        body: data.body,
        icon: data.icon ?? '/icon-192.png',
        badge: '/icon-192.png',
        vibrate: [200, 100, 200]
    }));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    event.waitUntil(clients.openWindow('/'));
});
