// Dev: no caching — always fetch fresh from network
self.addEventListener('fetch', () => {});

self.addEventListener('message', event => {
    if (event.data?.type === 'SKIP_WAITING') self.skipWaiting();
});

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
