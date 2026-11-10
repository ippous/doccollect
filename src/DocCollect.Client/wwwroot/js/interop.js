// Fonctions JavaScript appelées depuis Blazor (window.dc.*).
window.dc = {
    storage: {
        get(kind, key) {
            try { return (kind === 'session' ? sessionStorage : localStorage).getItem(key); } catch { return null; }
        },
        set(kind, key, value) {
            try { (kind === 'session' ? sessionStorage : localStorage).setItem(key, value); } catch { /* stockage bloqué */ }
        },
        remove(kind, key) {
            try { (kind === 'session' ? sessionStorage : localStorage).removeItem(key); } catch { }
        }
    },

    // Téléchargement authentifié : fetch avec le jeton, puis enregistrement du blob.
    async download(url, token, fileName) {
        const resp = await fetch(url, { headers: token ? { Authorization: 'Bearer ' + token } : {} });
        if (!resp.ok) return false;
        const blob = await resp.blob();
        const href = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = href;
        a.download = fileName || 'document';
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(href), 10000);
        return true;
    },

    // QR code SVG pour l'application d'authentification (bibliothèque qrcode-generator, MIT).
    qrSvg(text) {
        const qr = qrcode(0, 'M');
        qr.addData(text);
        qr.make();
        return qr.createSvgTag({ cellSize: 4, margin: 2, scalable: true });
    },

    copy(text) {
        return navigator.clipboard?.writeText(text).then(() => true, () => false) ?? Promise.resolve(false);
    },

    // Mode embarqué : signale la hauteur du contenu à la page parente (voir embed.js).
    embed: {
        _observer: null,
        start() {
            if (window.parent === window || this._observer) return;
            const post = () => window.parent.postMessage({ type: 'doccollect:height', height: document.documentElement.scrollHeight }, '*');
            this._observer = new ResizeObserver(post);
            this._observer.observe(document.body);
            post();
        },
        notify(eventName, payload) {
            if (window.parent !== window) window.parent.postMessage({ type: 'doccollect:' + eventName, payload }, '*');
        }
    }
};
