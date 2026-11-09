# Validation — ce qui est prouvé, ce qui ne l'est pas

Ce document est volontairement franc : un outil qui écrit sur des disques doit être éprouvé avant d'être cru.

## Validé automatiquement (26 tests, `dotnet test`, disques virtuels)

| Domaine | Vérifié |
|---|---|
| Lecture GPT / MBR | Partitions EFI, MSR-like, Windows, WinRE ; CRC GPT ; en-tête corrompu détecté |
| Clone GPT → disque plus grand | Données identiques octet pour octet ; GPT de secours recréée et valide ; partitions relues identiques |
| Clone MBR | Code d'amorçage, signature et partitions identiques |
| Sécurité | Même disque, disque système en destination, destination trop petite, secteurs différents : **bloqués** |
| Confirmation | Sans confirmation explicite (ou pour un autre disque) : **aucun octet écrit** |
| BitLocker | Détecté ; copié chiffré tel quel ; avertissement affiché |
| Erreurs matérielles simulées | Lecture transitoire : relancée. Secteur illisible : arrêt + offset exact. Erreur d'écriture : échec. **Corruption silencieuse à l'écriture : détectée par SHA-256** |
| Annulation | Clone : état « annulé », destination déclarée incomplète. Image : fichier temporaire supprimé |
| Image | Création → vérification → restauration ; compression ; image corrompue **refusée avant tout effacement** ; image tronquée refusée |
| Compatibilité | UEFI/BIOS, matériel différent, licence, absence de garantie signalés |
| Points de restauration | Disque 2 partitions + données hors partition : retour **identique sur toute la taille du disque** ; une image ordinaire, elle, ne copie pas l'espace hors partition ; restauration sur disque plus grand (GPT de secours valide) ; refus sur disque plus petit ; retour sur le même disque après dégâts ; catalogue (valides seulement, plus récent d'abord, fichiers corrompus ignorés) |

Compilation de la couche Windows (accès disque natif), de l'interface WPF (XAML inclus) et de la console : validée par le compilateur. Le workflow GitHub Actions exécute les tests, compile sur Windows et construit l'installateur.

## NON validé (à faire sur matériel réel avant de faire confiance)

Ces points nécessitent Windows et de vrais disques ; ils n'ont **pas** pu être exécutés lors du développement :

1. **Accès disque physique Windows** (`WindowsDiskProvider`) : énumération, verrouillage/démontage des volumes, suppression de l'ancienne table, écriture brute. À essayer sur des disques **vides ou sacrifiables** (idéalement des disques virtuels VHDX montés).
2. **Lecture de disque système en ligne** : copie non cohérente par nature (pas de VSS). Utiliser WinPE.
3. **Démarrage effectif** d'un Windows cloné/restauré (même matériel, puis matériel différent).
4. **Support WinPE** : script `Build-WinPE-USB.ps1` et exécution de la console .NET autonome dans WinPE.
5. **Interface** : rendu, animations, comportement en DPI élevé, accessibilité.
6. **Installateur** : installation/désinstallation sur Windows 10 et 11 propres.
7. **Performances** : copie séquentielle simple (fiabilité d'abord), non optimisée.

### Procédure de test recommandée

1. Créez deux VHDX (Gestion des disques → Action → Créer un VHD), initialisez l'un en GPT avec quelques fichiers, montez les deux.
2. Clonez, comparez, démontez, attachez le clone et contrôlez l'accès aux fichiers.
3. Créez une image, vérifiez-la (`GhostClonageCli verify`), restaurez-la sur le second VHDX.
4. Seulement ensuite : disques physiques de test, puis données réelles — toujours en gardant l'original intact.

## Limites fonctionnelles connues

- Pas de redimensionnement automatique des partitions (l'espace en plus reste non alloué ; une destination plus petite n'est acceptée que si toutes les partitions y tiennent).
- Pas de copie « intelligente » au niveau système de fichiers (bitmap NTFS) : la copie est par secteur ; l'économie vient des blocs nuls et de la compression dans les images.
- Pas de VSS : copie à chaud non garantie cohérente.
- Pas de conversion 512 ↔ 4096 octets, pas de MBR ↔ GPT.
- Pas de disques dynamiques, de Storage Spaces ni de RAID logiciel.
- Image : une seule passe, pas d'incrémental.
- Chiffrement de l'image non fourni (les volumes BitLocker restent chiffrés).
- Si la table GPT primaire du disque source est invalide, l'opération est refusée (à réparer au préalable).
- Les disques de plusieurs To sont gérés (offsets 64 bits, empreintes par bloc de 4 Mio) mais n'ont pas été testés à cette échelle.
