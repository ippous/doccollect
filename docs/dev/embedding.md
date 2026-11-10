# Intégration dans le site d'un demandeur, et mobile

## Module embarqué

Le demandeur déclare son site (Paramètres › Intégration, ex. `https://www.etude-martin.fr`) puis insère :

```html
<div data-doccollect data-tenant="etude-martin"></div>
<script src="https://doccollect.jaume-eric.com/embed.js" async></script>
```

Attributs : `data-tenant` (obligatoire), `data-dossier` (ouvrir un dossier précis), `data-height` (hauteur minimale, 600 px par défaut).

Fonctionnement :

- `embed.js` crée une iframe vers `/embed/{tenant}` ; la hauteur s'ajuste au contenu (`postMessage`, origine vérifiée).
- L'API renvoie `Content-Security-Policy: frame-ancestors 'self' <origines du demandeur>` : un site non déclaré ne peut pas afficher le module.
- Le fournisseur se connecte dans l'iframe (2FA comprise). Les jetons sont en `sessionStorage` : pas de cookie tiers (compatible Safari).
- Seules les demandes de ce demandeur sont listées.

Événements émis sur le conteneur :

```js
document.querySelector('[data-doccollect]').addEventListener('doccollect:complete', e => {
  console.log('Dossier complet', e.detail.dossierId);
});
// aussi : doccollect:uploaded { dossierId, item }
```

## Mobile

- L'interface est conçue d'abord pour le téléphone (cibles tactiles de 44 px, une colonne).
- Bouton **Prendre une photo** (`capture="environment"`) sur les pièces qui acceptent les images.
- **PWA** : manifeste et service worker → « Ajouter à l'écran d'accueil » sur Android et iOS. Le service worker ne met en cache que l'application, jamais l'API ni les documents.
- Lien direct vers l'application d'authentification (`otpauth://`) lors de l'activation de la 2FA sur le téléphone.

Une application native n'est pas nécessaire pour le dépôt ; si elle le devenait (notifications push, numérisation de documents), un client .NET MAUI Blazor Hybrid pourrait réutiliser `DocCollect.Client`.
