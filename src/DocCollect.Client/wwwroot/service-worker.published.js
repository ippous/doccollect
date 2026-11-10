// Service worker de production : met en cache l'application (installation sur l'écran d'accueil du mobile).
// Les appels d'API et les documents ne sont JAMAIS mis en cache.
self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'doccollect-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.svg$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/, /^embed\.js$/];

async function onInstall() {
    const assetsRequests = self.assetsManifest.assets
        .filter(a => offlineAssetsInclude.some(p => p.test(a.url)))
        .filter(a => !offlineAssetsExclude.some(p => p.test(a.url)))
        .map(a => new Request(a.url, { integrity: a.hash, cache: 'no-cache' }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate() {
    const keys = await caches.keys();
    await Promise.all(keys.filter(k => k.startsWith(cacheNamePrefix) && k !== cacheName).map(k => caches.delete(k)));
}

async function onFetch(event) {
    const url = new URL(event.request.url);
    if (event.request.method !== 'GET' || url.pathname.startsWith('/api/') || url.pathname.startsWith('/embed')) {
        return fetch(event.request);
    }
    const request = event.request.mode === 'navigate' ? 'index.html' : event.request;
    const cache = await caches.open(cacheName);
    return (await cache.match(request)) || fetch(event.request);
}
