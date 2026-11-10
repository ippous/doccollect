# Guide du demandeur

Vous êtes une étude notariale, un cabinet, une agence : vous demandez des documents à vos clients.

## 1. Première connexion

Vous avez reçu une invitation par e-mail. Cliquez sur le lien, choisissez un mot de passe (10 caractères minimum, avec majuscule, minuscule et chiffre), puis **activez la double authentification** :

- **Application d'authentification** (recommandé) : scannez le QR code avec Google Authenticator, Microsoft Authenticator, etc., puis saisissez le code à 6 chiffres.
- **Code par e-mail** : un code vous est envoyé à chaque connexion.

Notez les **codes de récupération** affichés : chacun permet une connexion si vous perdez votre téléphone.

## 2. Préparer vos modèles

Menu **Modèles** : un modèle est une liste de pièces réutilisable (« Vente — vendeur », « Succession »…).

- Le **catalogue** propose des modèles prêts à l'emploi ; **Copier pour l'adapter** crée votre version privée.
- Pour chaque pièce : libellé, précisions pour votre client, formats acceptés (PDF, photo…), taille et nombre de fichiers maximum, caractère obligatoire ou facultatif.
- **Type de document attendu** : à renseigner si vous utilisez la vérification IA (ex. « avis d'imposition sur le revenu »).
- **Transmission par défaut** : en une fois quand tout est là, ou au fil de l'eau.

## 3. Envoyer une demande

**Nouvelle demande** : titre, votre référence, modèle, éventuelles pièces spécifiques au dossier, e-mail du client, message, échéance.

Options :
- **Transmission** en une fois ou au fil de l'eau.
- **Validation de chaque pièce** : une pièce ne compte comme reçue qu'après votre accord ; vous pouvez la refuser avec un motif, le client est prévenu et en dépose une nouvelle.

Votre client reçoit un e-mail avec un lien sécurisé.

## 4. Suivre les dossiers

La liste **Dossiers** montre l'avancement (pièces obligatoires reçues / attendues) et le statut :

| Statut | Signification |
|---|---|
| En attente de pièces | des pièces obligatoires manquent |
| Complet | tout est là (en cours de transmission) |
| Transmis | tout vous a été transmis |
| Clos / Annulé | dossier terminé |

Dans un dossier : télécharger chaque document ou **tout en .zip**, accepter/refuser, **Relancer le fournisseur**, **Transmettre maintenant** (même incomplet). Si la vérification IA est active, une pastille signale un document qui ne semble pas correspondre.

Des relances automatiques partent chaque semaine pour les dossiers incomplets, et deux jours avant l'échéance.

## 5. Paramètres

- **Stockage** : par défaut, celui de la plateforme ; vous pouvez utiliser le vôtre (NAS en SFTP, Azure, Google Cloud). **Chiffrement** recommandé. Un stockage se supprime seulement lorsqu'il est vide et plus utilisé (ni comme stockage principal, ni par un canal de copie).
- **Vérification par IA** (option payante) : choix du fournisseur selon vos contraintes de localisation.
- **Intégration à votre site** : déclarez l'adresse de votre site et copiez le code fourni dans votre page.
- **Canaux de transmission** : e-mail de notification (les documents restent dans votre espace), webhook vers votre logiciel, copie automatique vers votre stockage.

## 6. Équipe

Menu **Équipe** : invitez vos collègues ; chacun active sa propre double authentification. La consommation de vérifications IA y est affichée.
