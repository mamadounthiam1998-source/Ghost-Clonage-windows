# Format d'image GHOST (`.gci`) — version 1

Format documenté, entiers en little-endian.

```
[En-tête 64 o]   "GHOSTIMG" (8) | version u32 | taille de secteur u32 | taille de bloc u32 | réservé u32
                 | taille du disque i64 | taille de l'étendue imagée i64 | réservé jusqu'à 64
[Blocs …]        type u8 (0 = zéros, 1 = brut, 2 = Brotli)
                 | taille brute u32 | taille stockée u32 | SHA-256 des données brutes (32 o) | données stockées
[0xFF]           marqueur de fin
[Métadonnées]    longueur u32 + JSON UTF-8 (structure du disque, partitions, machine d'origine, version…)
[Pied 96 o]      "GHOSTEND" (8) | nombre de blocs i64 | taille de l'étendue i64 | offset des métadonnées i64
                 | SHA-256 global des données brutes (32 o) | SHA-256 des 64 premiers octets du pied (32 o)
```

## Garanties

- **Validité** : une image n'est reconnue que si son pied est présent, intègre et cohérent avec l'en-tête. Un fichier tronqué ou interrompu est refusé (`ImageReader.Open`).
- **Intégrité** : chaque bloc porte son SHA-256 ; le pied porte le SHA-256 de l'ensemble. `ImageReader.Verify` relit tout le fichier.
- **Écriture atomique** : l'image est écrite dans `fichier.gci.partial`, vérifiée, puis renommée. Annulation ou erreur → le `.partial` est supprimé.
- **Copie intelligente** : les blocs entièrement nuls sont stockés sans données ; les autres sont compressés (Brotli) si cela réduit la taille.
- **Disque entier / point de restauration** : les métadonnées portent `FullDisk` (tous les secteurs, jusqu'au dernier du disque), `IsRestorePoint` et `Label`. À la restauration sur un disque de taille différente, la GPT de secours d'origine n'est pas recopiée : elle est recréée en fin de disque destination (qui doit avoir au moins la taille du disque d'origine).
- **Étendue imagée (image ordinaire)** : du début du disque jusqu'à la fin de la dernière partition (structure GPT/MBR et espaces entre partitions inclus). Les disques sans table reconnue sont imagés en entier. La GPT de secours est recréée à la restauration, à la fin du disque destination.

## Ce que le format ne contient pas

Pas de chiffrement (les volumes BitLocker restent chiffrés tels quels dans l'image), pas de copie différentielle/incrémentale, pas de lecture des systèmes de fichiers (la copie est au niveau secteur ; les secteurs libres non nuls d'un volume sont donc copiés).
