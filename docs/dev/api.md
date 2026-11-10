# API et webhooks

Toutes les routes sont sous `/api`, en JSON (énumérations en texte). Authentification : `Authorization: Bearer <jeton>` obtenu par `/api/auth/login`. Description OpenAPI : `/openapi/v1.json` (hors production). Erreurs : `{ "code": "...", "message": "..." }`.

## Authentification — `/api/auth`

| Méthode | Route | Accès | Rôle |
|---|---|---|---|
| POST | `/login` | anonyme | `{email, password, twoFactorCode?, recoveryCode?}` → jetons, ou `requiresTwoFactor` (+ envoi du code e-mail) |
| POST | `/refresh` | anonyme | `{refreshToken}` → nouveaux jetons |
| GET | `/me` | connecté | profil, rôles, état 2FA |
| POST | `/2fa/authenticator/setup` | connecté | clé + URI `otpauth://` |
| POST | `/2fa/email/send-code` | connecté | envoie un code |
| POST | `/2fa/enable` | connecté | `{method, code}` → codes de récupération + nouveaux jetons |
| POST | `/2fa/recovery-codes` | 2FA | régénère les codes |
| GET | `/invitations/{token}` | anonyme | informations d'invitation |
| POST | `/invitations/accept` | anonyme | crée le compte, renvoie un jeton restreint |
| POST | `/forgot-password`, `/reset-password` | anonyme | réinitialisation |

## Demandeur — `/api/requester` (rôle Requester + 2FA)

- `GET/PUT /tenant`, `/tenant/settings` — stockage, chiffrement, IA, origines d'intégration
- `GET /ai-providers`, `GET /ai-usage?year=`
- `GET /storage/providers` ; `GET/POST /storage/profiles`, `PUT/DELETE /storage/profiles/{id}`, `POST /storage/profiles/{id}/test`
- `GET/POST /templates`, `GET/PUT/DELETE /templates/{id}`, `POST /templates/{id}/copy`
- `GET /dossiers?status=&q=`, `POST /dossiers`, `GET /dossiers/{id}`
- `POST /dossiers/{id}/items/{itemId}/accept|reject`, `POST /dossiers/{id}/deliver|remind|cancel|close`
- `GET /dossiers/{id}/documents/{docId}/content`, `GET /dossiers/{id}/zip`, `GET /dossiers/{id}/deliveries`
- `GET/POST /channels`, `PUT/DELETE /channels/{id}`
- `GET /users`, `POST /users/invite`

## Fournisseur — `/api/provider` (rôle Provider + 2FA)

- `GET /dossiers`, `GET /dossiers/{id}`
- `POST /dossiers/{id}/items/{itemId}/documents` (multipart, champ `file`)
- `DELETE /dossiers/{id}/documents/{docId}` (avant transmission uniquement)
- `GET /dossiers/{id}/documents/{docId}/content`

## Administration — `/api/admin` (rôle Admin + 2FA)

Demandeurs, utilisateurs (déverrouillage, réinitialisation 2FA, invitation d'admins), stockages plateforme, modèles publics, fournisseurs IA, consommation IA, journal d'audit.

## Webhook de livraison

`POST` vers l'URL configurée, corps JSON :

```json
{
  "event": "dossier.complete",
  "dossier": { "id": "…", "reference": "2026-VTE-042", "title": "Vente Dupont", "status": "Complete" },
  "documents": [
    { "id": "…", "item": "Pièce d'identité", "fileName": "cni.pdf", "contentType": "application/pdf",
      "size": 182344, "sha256": "…", "aiVerdict": "Match", "contentBase64": null,
      "downloadUrl": "https://doccollect.jaume-eric.com/api/requester/dossiers/…/documents/…/content" }
  ],
  "sentAt": "2026-11-10T19:59:00Z"
}
```

`event` vaut `documents.received` (envoi au fil de l'eau) ou `dossier.complete`. `contentBase64` est rempli si l'option « Inclure le contenu » est active (fichiers ≤ 10 Mo).

En-têtes : `X-DocCollect-Timestamp` (secondes Unix) et `X-DocCollect-Signature: sha256=<hex>` = HMAC-SHA256(secret, `timestamp + "." + corps`). Toute réponse non 2xx déclenche une nouvelle tentative (1 min, 5 min, 30 min, 2 h, 12 h).

Vérification côté récepteur (C#) :

```csharp
var expected = "sha256=" + Convert.ToHexStringLower(
    HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
var valid = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature))
            && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp)) < 300;
```
