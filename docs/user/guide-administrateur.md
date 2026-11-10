# Guide de l'administrateur de la plateforme

## Premier démarrage

Le premier administrateur est créé à partir des variables `DC_ADMIN_EMAIL` / `DC_ADMIN_PASSWORD`. À la première connexion, activez la double authentification (application TOTP recommandée) et rangez les codes de récupération.

## Demandeurs

**Demandeurs** › Nouveau demandeur : nom, identifiant court (utilisé dans l'adresse d'intégration, ex. `etude-martin`), e-mail du responsable. Le responsable reçoit une invitation et gère ensuite lui-même son équipe, ses modèles et ses paramètres.

L'activation de l'**option IA** (payante) se fait par demandeur, depuis la liste.

## Stockages de la plateforme

**Stockages** : ajoutez au moins un profil et marquez-le **par défaut** — il reçoit les documents des demandeurs qui n'ont pas leur propre stockage. Recommandé : SFTP vers le NAS via Tailscale (voir docs/dev/storage-providers.md). Utilisez **Tester la connexion** après chaque modification.

Un stockage ne peut être **supprimé que s'il est vide et inutilisé** : aucun document, aucun demandeur qui l'utilise, aucun canal « copie vers mon stockage » qui le cible, et pas le stockage par défaut (désignez-en un autre d'abord). Sinon, le bouton Supprimer est grisé et le motif est affiché.

## Modèles publics

**Modèles publics** : le catalogue visible par tous les demandeurs (vente immobilière, succession, location, bilan comptable sont fournis au départ). Les demandeurs les copient pour les adapter ; vos modifications n'affectent pas leurs copies ni les dossiers en cours.

## Utilisateurs

- **Déverrouiller** un compte bloqué après des échecs de connexion.
- **Réinitialiser la 2FA** d'un utilisateur qui a perdu son téléphone et ses codes : à vérifier par un autre canal (téléphone) avant d'agir. Il devra la réactiver à sa prochaine connexion.
- **Inviter un administrateur**.

## IA et facturation

Liste des fournisseurs d'IA configurés (avec leur localisation) et consommation mensuelle par demandeur : nombre de documents vérifiés et jetons consommés, base de la facturation de l'option.

## Journaux et e-mails

**Journaux** : derniers événements de l'application (filtre par niveau, recherche, actualisation automatique, détail des exceptions). Ils sont conservés en mémoire (5 000 entrées, remis à zéro au redémarrage) ; l'historique complet reste disponible avec `docker compose logs app` sur le VPS.

En haut de la page, l'état de l'**envoi des e-mails** : fonctionne / en échec, avec le message technique du serveur d'envoi (authentification refusée, serveur injoignable…), un bouton d'e-mail de test et les transmissions en échec de tous les demandeurs.

## Exploitation

- Santé : `https://doccollect.jaume-eric.com/healthz`.
- Journal d'audit : `GET /api/admin/audit`.
- Sauvegardes, rotation des clés, mises en production : docs/dev/deployment.md et docs/dev/security.md.
