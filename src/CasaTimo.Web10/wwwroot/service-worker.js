// Dev: no caching — always fetch fresh from network
self.addEventListener('fetch', () => {});

self.addEventListener('message', event => {
    if (event.data?.type === 'SKIP_WAITING') self.skipWaiting();
});
