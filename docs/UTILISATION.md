# GHOST CLONAGE WINDOWS — Guide d'utilisation

Trois fonctions, un seul écran : **CLONER**, **CRÉER UNE IMAGE**, **RESTAURER**.

## Avant de commencer

- Lancez le logiciel (il demande les droits administrateur : l'accès direct aux disques l'exige).
- Aucune connexion Internet, aucun compte, aucune télémétrie.
- **Pour copier le Windows sur lequel vous travaillez**, utilisez le support de démarrage (voir plus bas). Copié « à chaud », un système en cours d'exécution n'est pas garanti cohérent (fichiers ouverts) — le logiciel vous en avertit.

## 1. Cloner un disque

1. Onglet **CLONER**.
2. À gauche, cliquez le **disque source**. À droite, le **disque destination**.
   Chaque carte affiche : nom, marque/modèle, capacité, espace utilisé, SSD/HDD, nombre de partitions, GPT/MBR.
3. Cliquez **CLONER LE DISQUE**.
4. Une fenêtre rouge liste le disque qui sera **effacé**. Cochez la case, puis validez. *Rien n'est écrit avant cette confirmation.*
5. Suivez la progression. À la fin, le logiciel **relit** le disque destination, compare les empreintes SHA-256 et contrôle les partitions. Le message « Opération terminée » n'apparaît que si tout est vérifié.

Le logiciel **refuse** : disque source = destination, destination = disque Windows en cours d'exécution, destination trop petite, tailles de secteur différentes (512 / 4096).

Si la destination est plus grande, l'espace en plus reste **non alloué** (étendez ensuite la dernière partition avec la Gestion des disques de Windows).

## 2. Créer une image

1. Onglet **CRÉER UNE IMAGE**, choisissez le disque source.
2. **Parcourir…** pour choisir le fichier `.gci` — sur **un autre disque** que la source (le logiciel le refuse sinon).
3. **CRÉER L'IMAGE**. L'image est écrite dans un fichier temporaire, compressée, puis **relue et vérifiée en entier** ; elle ne prend son nom définitif qu'une fois valide. Une image interrompue ou corrompue n'est **jamais** présentée comme une sauvegarde.

## 3. Restaurer une image (y compris sur un autre ordinateur)

1. Onglet **RESTAURER**, **Parcourir…** → choisissez le `.gci`. Les informations de l'image (date, machine d'origine, partitions) s'affichent. Une image invalide est signalée immédiatement.
2. Choisissez le **disque destination**, cliquez **RESTAURER L'IMAGE**.
3. La fenêtre de confirmation affiche aussi le **rapport de compatibilité** : mode UEFI/BIOS, matériel différent, contrôleur de stockage, licence Windows, BitLocker.
4. L'image est d'abord vérifiée en entier **avant** d'effacer quoi que ce soit.

> **Important** : sur un matériel différent, le démarrage n'est **pas garanti**. Pilotes de stockage, mode de démarrage et licence peuvent poser problème. Conservez toujours l'image d'origine et testez.

## BitLocker

Les partitions BitLocker sont copiées **telles quelles, chiffrées**. Le chiffrement n'est jamais contourné ni déchiffré. Sur un autre ordinateur (ou si la protection dépend du TPM), la **clé de récupération BitLocker** sera demandée.

## Support USB de démarrage (WinPE)

Pour cloner/imager le disque Windows actif, ou restaurer sur un disque système, démarrez depuis une clé WinPE :

1. Installez (une fois) le **Windows ADK** et son module **Windows PE** (gratuits, Microsoft).
2. Menu Démarrer → **GHOST CLONAGE WINDOWS → Créer le support USB de démarrage (WinPE)**, ou lancez `winpe\Build-WinPE-USB.ps1 -UsbDrive E:`. La clé est **formatée** (confirmation par saisie).
3. Démarrez l'ordinateur sur la clé : un menu console (liste, cloner, image, restaurer, vérifier) s'affiche.

L'interface graphique WPF ne fonctionne pas dans WinPE : la clé embarque la **version console**, qui utilise exactement les mêmes moteurs.
Ce support est fourni mais **doit être testé par vos soins** avant d'en dépendre (voir `VALIDATION.md`).

## Journaux

Chaque opération écrit un journal local dans `%LocalAppData%\GhostClonage\logs`. Rien n'est envoyé nulle part.

## Interruption

Le bouton **ANNULER** arrête l'opération de façon contrôlée (entre deux blocs). Le disque destination est alors **incomplet** et ne démarrera pas : relancez l'opération. Une création d'image annulée supprime son fichier temporaire.

## Désinstallation

Paramètres Windows → Applications → GHOST CLONAGE WINDOWS → Désinstaller (ou menu Démarrer). Les journaux sont conservés.
