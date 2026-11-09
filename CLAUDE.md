# GHOST CLONAGE WINDOWS — consignes de travail (économie de tokens)

Réponds en français, court. Pas de récapitulatif long, pas de re-explication de ce qui est déjà dit.

## Efficacité
- Ne relis pas un fichier entier : cherche (Grep) puis lis seulement les lignes utiles.
- Ne réaffiche pas le code que tu viens d'écrire. Résume en 3 à 6 lignes.
- Regroupe les modifications d'un même sujet dans UN commit et UN push (chaque push lance un build CI).
- Pour un simple changement de doc ou de commentaire, ajoute `[skip ci]` au message de commit.
- Ne lance pas de sous-agents pour une tâche simple.

## Tests et CI
- Avant de pousser : `dotnet test tests/Ghost.Core.Tests` (rapide, local). Le test WPF et les tests VHDX ne tournent que sur GitHub Actions.
- Pour suivre le build, interroge l'API GitHub et affiche UNIQUEMENT : état final, étapes en échec, et les lignes `ECHEC`/`Failed`. Jamais le journal complet.

## Projet
- `src/Ghost.Core` moteur (testé), `src/Ghost.Windows` accès disque natif, `src/Ghost.App` WPF/MVVM, `src/Ghost.Cli` console WinPE.
- Règles de sécurité à ne jamais affaiblir : confirmation explicite imposée par le moteur, disque système jamais écrasé, aucun succès affiché si une vérification échoue, BitLocker jamais contourné, aucune télémétrie.
- `docs/VALIDATION.md` dit ce qui est prouvé ou non : le tenir à jour, ne rien promettre de plus.
