# GHOST CLONAGE WINDOWS

Logiciel Windows (C# / .NET 10 / WPF, MVVM) de **clonage de disque**, de **création d'image** et de **restauration**, avec vérification SHA-256, sans télémétrie ni connexion Internet.

> **Statut honnête** : le moteur (clonage, image, restauration, sécurité, intégrité) est testé sur disques virtuels (26 tests). L'accès aux disques physiques Windows, l'interface, l'installateur et le support WinPE sont écrits et compilent, mais **n'ont pas encore été exécutés sur du matériel réel** — voir [`docs/VALIDATION.md`](docs/VALIDATION.md). Ne l'utilisez pas sur des données irremplaçables avant d'avoir suivi la procédure de test.

## Fonctions

- **Cloner** un disque vers un autre (GPT/MBR, EFI, MSR, récupération, secteurs de démarrage), avec vérification SHA-256 de tout ce qui est écrit et contrôle des partitions.
- **Créer une image** `.gci` compressée (Brotli, blocs nuls ignorés), vérifiée en entier avant d'être reconnue valide.
- **Restaurer** une image (même ordinateur ou autre) avec rapport de compatibilité matérielle.
- **Points de restauration** : image du **disque entier** (toutes les partitions + espace hors partition) rangée dans un dossier, avec liste et retour en un clic. Sur le disque système actif, le retour se fait depuis la clé WinPE.
- Protections : disque système jamais écrasé, confirmation explicite obligatoire, capacité/secteurs contrôlés, BitLocker jamais contourné, journal local.

Documentation : [`docs/UTILISATION.md`](docs/UTILISATION.md) · [`docs/FORMAT-IMAGE.md`](docs/FORMAT-IMAGE.md) · [`docs/VALIDATION.md`](docs/VALIDATION.md)

## Structure

```
src/Ghost.Core      moteurs (clone / image / restauration), format .gci, sécurité, compatibilité — portable, testé
src/Ghost.Windows   accès natif aux disques physiques (DeviceIoControl / ReadFile / WriteFile)
src/Ghost.App       interface WPF futuriste (MVVM), manifeste administrateur
src/Ghost.Cli       version console (utilisée dans WinPE)
tests/              tests xUnit sur disques virtuels (fichiers)
installer/          script Inno Setup
tools/winpe/        création du support USB de démarrage
```

## Compiler

GitHub Actions (`.github/workflows/build.yml`) compile sur `windows-latest` : tests → publication autonome win-x64 → installateur Inno Setup → artefacts (installateur, version portable, kit WinPE, SHA-256).

En local (Windows, SDK .NET 10) :

```bash
dotnet test tests/Ghost.Core.Tests
dotnet publish src/Ghost.App -c Release -r win-x64 --self-contained -o publish/app
dotnet publish src/Ghost.Cli -c Release -r win-x64 --self-contained -o publish/cli
```

## Principes de sécurité

Aucune opération destructive sans confirmation explicite (imposée par le moteur, pas seulement par l'interface) ; aucun résultat positif si une vérification échoue ; aucune donnée envoyée hors de la machine ; élévation administrateur demandée au lancement car l'accès disque brut l'exige.
