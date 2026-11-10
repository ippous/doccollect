/*
 * DocCollect — intégration dans le site d'un demandeur.
 *
 * <div data-doccollect data-tenant="etude-martin"></div>
 * <script src="https://doccollect.jaume-eric.com/embed.js" async></script>
 *
 * Attributs :
 *   data-tenant   (obligatoire) identifiant court du demandeur
 *   data-dossier  (facultatif)  ouvre directement un dossier
 *   data-height   (facultatif)  hauteur minimale en pixels (défaut 600)
 *
 * Le site du demandeur doit être déclaré dans DocCollect (Paramètres › Intégration), sinon le navigateur
 * refuse l'affichage (en-tête CSP frame-ancestors).
 * Événements émis sur l'élément conteneur : « doccollect:uploaded », « doccollect:complete ».
 */
(function () {
    var script = document.currentScript;
    var origin = script ? new URL(script.src).origin : 'https://doccollect.jaume-eric.com';

    function mount(el) {
        if (el.__doccollect) return;
        el.__doccollect = true;
        var tenant = el.getAttribute('data-tenant');
        if (!tenant) { console.error('DocCollect : attribut data-tenant manquant.'); return; }
        var dossier = el.getAttribute('data-dossier');
        var path = '/embed/' + encodeURIComponent(tenant) + (dossier ? '/dossiers/' + encodeURIComponent(dossier) : '');

        var iframe = document.createElement('iframe');
        iframe.src = origin + path;
        iframe.title = 'Dépôt de documents';
        iframe.style.width = '100%';
        iframe.style.border = '0';
        iframe.style.minHeight = (parseInt(el.getAttribute('data-height'), 10) || 600) + 'px';
        iframe.setAttribute('allow', 'camera');
        iframe.setAttribute('loading', 'lazy');
        el.appendChild(iframe);

        window.addEventListener('message', function (e) {
            if (e.origin !== origin || e.source !== iframe.contentWindow || !e.data || typeof e.data.type !== 'string') return;
            if (e.data.type === 'doccollect:height') {
                iframe.style.height = Math.max(e.data.height, 200) + 'px';
            } else if (e.data.type.indexOf('doccollect:') === 0) {
                el.dispatchEvent(new CustomEvent(e.data.type, { detail: e.data.payload, bubbles: true }));
            }
        });
    }

    function init() {
        var nodes = document.querySelectorAll('[data-doccollect]');
        for (var i = 0; i < nodes.length; i++) mount(nodes[i]);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
