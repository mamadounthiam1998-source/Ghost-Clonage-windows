using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Ghost.Core.Disks;
using Ghost.Core.Imaging;
using Ghost.Core.Operations;
using Ghost.Core.Safety;
using Ghost.Windows;

namespace Ghost.App.ViewModels;

public enum AppMode { Clone, Image, Restore, RestorePoint }

public enum OpKind { Clone, Image, Restore, CreatePoint, Revert }

public sealed class MainViewModel : ObservableObject
{
    private readonly IDiskProvider _provider;
    private readonly IDialogService _dialogs;
    private readonly string _settingsPath;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private OpKind _op = OpKind.Clone;

    public MainViewModel(IDiskProvider provider, IDialogService dialogs, string? settingsPath = null)
    {
        _provider = provider;
        _dialogs = dialogs;
        _settingsPath = settingsPath ?? AppSettings.DefaultPath;
        _settings = AppSettings.Load(_settingsPath);
        _pointsFolder = _settings.PointsFolder;

        StartCommand = new RelayCommand(async () => await StartAsync(), () => CanStart);
        RevertCommand = new RelayCommand(async () => await RevertAsync(), () => CanRevert);
        BrowsePointsFolderCommand = new RelayCommand(() =>
        {
            var f = _dialogs.PickFolder();
            if (f != null) PointsFolder = f;
        }, () => !IsBusy);
        RefreshPointsCommand = new RelayCommand(RefreshPoints, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RefreshCommand = new RelayCommand(RefreshDisks, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(() =>
        {
            var p = _dialogs.PickSaveImage();
            if (p != null) { OutputImagePath = p; _outputConfirmedOverwrite = true; }
        }, () => !IsBusy);
        BrowseImageCommand = new RelayCommand(() =>
        {
            var p = _dialogs.PickOpenImage();
            if (p != null) RestoreImagePath = p;
        }, () => !IsBusy);

        RefreshDisks();
        RefreshPoints();
    }

    // ---------- Mode ----------
    private AppMode _mode = AppMode.Clone;
    public AppMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            foreach (var n in new[] { nameof(IsClone), nameof(IsImage), nameof(IsRestore), nameof(SourceTitle),
                         nameof(DestinationTitle), nameof(ActionLabel), nameof(ShowSourceDisks), nameof(ShowSourceImage),
                         nameof(ShowDestinationDisks), nameof(ShowDestinationFile), nameof(IsRestorePoint),
                         nameof(ShowDestinationPoints), nameof(ShowRevert) }) Raise(n);
            ClearResult();
            RaiseCanStart();
            RaiseCanRevert();
            if (value == AppMode.RestorePoint) RefreshPoints();
        }
    }
    public bool IsClone { get => Mode == AppMode.Clone; set { if (value) Mode = AppMode.Clone; } }
    public bool IsImage { get => Mode == AppMode.Image; set { if (value) Mode = AppMode.Image; } }
    public bool IsRestore { get => Mode == AppMode.Restore; set { if (value) Mode = AppMode.Restore; } }
    public bool IsRestorePoint { get => Mode == AppMode.RestorePoint; set { if (value) Mode = AppMode.RestorePoint; } }

    public string SourceTitle => Mode switch
    {
        AppMode.Restore => "IMAGE SOURCE",
        AppMode.RestorePoint => "DISQUE ENTIER À PROTÉGER / À RESTAURER",
        _ => "DISQUE SOURCE"
    };
    public string DestinationTitle => Mode switch
    {
        AppMode.Image => "FICHIER IMAGE",
        AppMode.RestorePoint => "POINTS DE RESTAURATION",
        _ => "DISQUE DESTINATION"
    };
    public string ActionLabel => Mode switch
    {
        AppMode.Clone => "CLONER LE DISQUE",
        AppMode.Image => "CRÉER L'IMAGE",
        AppMode.RestorePoint => "CRÉER LE POINT DE RESTAURATION",
        _ => "RESTAURER L'IMAGE"
    };
    public bool ShowSourceDisks => Mode != AppMode.Restore;
    public bool ShowSourceImage => Mode == AppMode.Restore;
    public bool ShowDestinationDisks => Mode is AppMode.Clone or AppMode.Restore;
    public bool ShowDestinationFile => Mode == AppMode.Image;
    public bool ShowDestinationPoints => Mode == AppMode.RestorePoint;
    public bool ShowRevert => Mode == AppMode.RestorePoint && IsIdle;

    // ---------- Points de restauration ----------
    public ObservableCollection<RestorePointItem> Points { get; } = new();

    private RestorePointItem? _selectedPoint;
    public RestorePointItem? SelectedPoint
    {
        get => _selectedPoint;
        set { if (Set(ref _selectedPoint, value)) RaiseCanRevert(); }
    }

    private string _pointsFolder;
    public string PointsFolder
    {
        get => _pointsFolder;
        set
        {
            if (!Set(ref _pointsFolder, value ?? "")) return;
            _settings.PointsFolder = _pointsFolder;
            _settings.Save(_settingsPath);
            RefreshPoints();
            RaiseCanStart();
        }
    }

    private string _pointLabel = "Point de restauration";
    public string PointLabel { get => _pointLabel; set => Set(ref _pointLabel, value ?? ""); }

    private string _pointsInfo = "";
    public string PointsInfo { get => _pointsInfo; private set => Set(ref _pointsInfo, value); }

    public void RefreshPoints()
    {
        string? keep = SelectedPoint?.Path;
        Points.Clear();
        if (string.IsNullOrWhiteSpace(PointsFolder))
        {
            SelectedPoint = null;
            PointsInfo = "Choisissez un dossier sur un AUTRE disque que celui à protéger (disque externe, second disque, partage réseau).";
            return;
        }
        if (!Directory.Exists(PointsFolder))
        {
            SelectedPoint = null;
            PointsInfo = "Ce dossier n'existe pas encore : il sera créé au premier point.";
            return;
        }
        var l = RestorePointCatalog.List(PointsFolder);
        foreach (var p in l.Points) Points.Add(new RestorePointItem(p));
        SelectedPoint = Points.FirstOrDefault(x => x.Path == keep);
        PointsInfo = l.Points.Count == 0
            ? "Aucun point de restauration dans ce dossier."
            : $"{l.Points.Count} point(s) valide(s)." + (l.InvalidFiles > 0 ? $" {l.InvalidFiles} fichier(s) incomplet(s) ou corrompu(s) ignoré(s)." : "");
    }

    // ---------- Disques ----------
    public ObservableCollection<DiskItemViewModel> SourceDisks { get; } = new();
    public ObservableCollection<DiskItemViewModel> DestinationDisks { get; } = new();

    private DiskItemViewModel? _selectedSource;
    public DiskItemViewModel? SelectedSource
    {
        get => _selectedSource;
        set { if (Set(ref _selectedSource, value)) { ClearResult(); RaiseCanStart(); RaiseCanRevert(); } }
    }

    private DiskItemViewModel? _selectedDestination;
    public DiskItemViewModel? SelectedDestination
    {
        get => _selectedDestination;
        set { if (Set(ref _selectedDestination, value)) { ClearResult(); RaiseCanStart(); } }
    }

    private string _diskStatus = "";
    public string DiskStatus { get => _diskStatus; private set => Set(ref _diskStatus, value); }

    private bool _refreshing;

    public void RefreshDisks()
    {
        string? keepSrc = SelectedSource?.Id, keepDst = SelectedDestination?.Id;
        _refreshing = true;   // la relecture de la liste ne doit pas effacer le résultat de l'opération qui vient de finir
        try
        {
            var disks = _provider.ListDisks();
            SourceDisks.Clear();
            DestinationDisks.Clear();
            foreach (var d in disks)
            {
                SourceDisks.Add(new DiskItemViewModel(d));
                DestinationDisks.Add(new DiskItemViewModel(d));
            }
            SelectedSource = SourceDisks.FirstOrDefault(d => d.Id == keepSrc);
            SelectedDestination = DestinationDisks.FirstOrDefault(d => d.Id == keepDst && d.CanBeDestination);
            DiskStatus = disks.Count == 0
                ? "Aucun disque détecté. Vérifiez que le logiciel est exécuté en tant qu'administrateur."
                : $"{disks.Count} disque(s) détecté(s).";
        }
        catch (Exception ex)
        {
            DiskStatus = "Détection des disques impossible : " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ---------- Fichiers ----------
    private string _outputImagePath = "";
    private bool _outputConfirmedOverwrite;
    public string OutputImagePath
    {
        get => _outputImagePath;
        set
        {
            if (Set(ref _outputImagePath, value))
            {
                _outputConfirmedOverwrite = false;   // un chemin saisi à la main n'a pas fait l'objet d'une confirmation de remplacement
                ClearResult();
                RaiseCanStart();
            }
        }
    }

    private string _restoreImagePath = "";
    public string RestoreImagePath
    {
        get => _restoreImagePath;
        set { if (Set(ref _restoreImagePath, value)) { LoadImageInfo(); ClearResult(); RaiseCanStart(); } }
    }

    private string _imageInfo = "Aucune image sélectionnée.";
    public string ImageInfo { get => _imageInfo; private set => Set(ref _imageInfo, value); }
    private bool _imageValid;

    private void LoadImageInfo()
    {
        _imageValid = false;
        if (string.IsNullOrWhiteSpace(RestoreImagePath)) { ImageInfo = "Aucune image sélectionnée."; return; }
        try
        {
            using var r = ImageReader.Open(RestoreImagePath);
            var m = r.Metadata;
            var parts = string.Join("\n", m.Layout.Partitions.Select(p =>
                $"   • {p.Kind}{(string.IsNullOrEmpty(p.Name) ? "" : " « " + p.Name + " »")} — {SafetyGuard.Fmt(p.Length)}{(p.IsBitLocker ? " (BitLocker)" : "")}"));
            ImageInfo =
                $"Créée le {m.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} sur « {m.Machine.Manufacturer} {m.Machine.Product} »\n" +
                $"Disque d'origine : {m.SourceModel} — {SafetyGuard.Fmt(m.SourceDiskSize)} — {(m.Layout.Style == Ghost.Core.Partitions.PartitionStyle.Gpt ? "GPT" : m.Layout.Style == Ghost.Core.Partitions.PartitionStyle.Mbr ? "MBR" : "sans table")}\n" +
                $"Données imagées : {SafetyGuard.Fmt(r.ExtentLength)} — fichier : {SafetyGuard.Fmt(r.FileSize)}\n" +
                $"Partitions ({m.Layout.Partitions.Count}) :\n{parts}";
            _imageValid = true;
        }
        catch (Exception ex)
        {
            ImageInfo = "⚠ Image invalide ou incomplète : " + ex.Message;
        }
    }

    // ---------- Progression ----------
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(IsIdle));
                Raise(nameof(ShowRevert));
                RaiseCanStart();
                RaiseCanRevert();
                (BrowsePointsFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RefreshPointsCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (BrowseOutputCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (BrowseImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IsIdle => !IsBusy;

    private bool _showProgress;
    public bool ShowProgress { get => _showProgress; private set => Set(ref _showProgress, value); }

    private double _percent;
    public double Percent { get => _percent; private set { if (Set(ref _percent, value)) Raise(nameof(PercentText)); } }
    public string PercentText => $"{Percent:0.0} %";

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _detailText = "";
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

    private string _bytesText = "—";
    public string BytesText { get => _bytesText; private set => Set(ref _bytesText, value); }
    private string _speedText = "—";
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    private string _elapsedText = "—";
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    private string _remainingText = "—";
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    // ---------- Résultat ----------
    private bool _hasResult;
    public bool HasResult { get => _hasResult; private set => Set(ref _hasResult, value); }
    private string _resultTitle = "";
    public string ResultTitle { get => _resultTitle; private set => Set(ref _resultTitle, value); }
    private string _resultText = "";
    public string ResultText { get => _resultText; private set => Set(ref _resultText, value); }
    private string _resultWarnings = "";
    public string ResultWarnings { get => _resultWarnings; private set { if (Set(ref _resultWarnings, value)) Raise(nameof(HasWarnings)); } }
    public bool HasWarnings => !string.IsNullOrEmpty(ResultWarnings);
    private string _resultKind = "ok";   // ok | error | cancelled
    public string ResultKind { get => _resultKind; private set => Set(ref _resultKind, value); }
    private string _logPath = "";
    public string LogPath { get => _logPath; private set => Set(ref _logPath, value); }

    private void ClearResult()
    {
        if (IsBusy || _refreshing) return;
        HasResult = false;
        ShowProgress = false;
        Percent = 0;
    }

    // ---------- Commandes ----------
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand BrowseImageCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand BrowsePointsFolderCommand { get; }
    public ICommand RefreshPointsCommand { get; }

    public bool CanStart => !IsBusy && Mode switch
    {
        AppMode.Clone => SelectedSource != null && SelectedDestination != null,
        AppMode.Image => SelectedSource != null && !string.IsNullOrWhiteSpace(OutputImagePath),
        AppMode.RestorePoint => SelectedSource != null && !string.IsNullOrWhiteSpace(PointsFolder),
        _ => _imageValid && SelectedDestination != null
    };

    public bool CanRevert => !IsBusy && Mode == AppMode.RestorePoint && SelectedSource != null && SelectedPoint != null;

    private void RaiseCanStart()
    {
        Raise(nameof(CanStart));
        (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseCanRevert()
    {
        Raise(nameof(CanRevert));
        (RevertCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public bool IsOperationRunning => IsBusy;

    public bool RequestClose()
    {
        if (!IsBusy) return true;
        if (!_dialogs.ConfirmCancelOnClose()) return false;
        _cts?.Cancel();
        return false; // la fenêtre se fermera à la main une fois l'arrêt contrôlé terminé
    }

    private string PhaseLabel(OperationPhase p) => p switch
    {
        OperationPhase.Analysing => "Analyse du disque",
        OperationPhase.Preparing => "Préparation",
        OperationPhase.Working => _op switch
        {
            OpKind.Clone => "Clonage en cours",
            OpKind.Image => "Création de l'image en cours",
            OpKind.CreatePoint => "Création du point de restauration en cours",
            OpKind.Revert => "Retour au point de restauration en cours",
            _ => "Restauration en cours"
        },
        OperationPhase.Verifying => "Vérification",
        OperationPhase.Finalizing => "Finalisation",
        OperationPhase.Completed => "Opération terminée",
        OperationPhase.Failed => "Erreur",
        OperationPhase.Cancelled => "Opération annulée",
        _ => ""
    };

    private static string Dur(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    private void OnProgress(ProgressInfo p)
    {
        StatusText = PhaseLabel(p.Phase);
        DetailText = p.Message;
        Percent = p.Percent;
        BytesText = $"{SafetyGuard.Fmt(p.BytesDone)} / {SafetyGuard.Fmt(p.BytesTotal)}";
        SpeedText = p.BytesPerSecond > 1 ? $"{SafetyGuard.Fmt((long)p.BytesPerSecond)}/s" : "—";
        ElapsedText = Dur(p.Elapsed);
        RemainingText = p.Remaining is { } r ? Dur(r) : "—";
    }

    private Task StartAsync()
    {
        if (!CanStart) return Task.CompletedTask;
        return Mode switch
        {
            AppMode.Clone => ExecuteAsync(OpKind.Clone, "clonage", RunCloneAsync),
            AppMode.Image => ExecuteAsync(OpKind.Image, "image", RunImageAsync),
            AppMode.RestorePoint => ExecuteAsync(OpKind.CreatePoint, "point-restauration", RunCreatePointAsync),
            _ => ExecuteAsync(OpKind.Restore, "restauration", RunRestoreAsync)
        };
    }

    private Task RevertAsync()
    {
        if (!CanRevert) return Task.CompletedTask;
        return ExecuteAsync(OpKind.Revert, "retour-point", RunRevertAsync);
    }

    private async Task ExecuteAsync(OpKind kind, string logName,
        Func<IProgress<ProgressInfo>, ILog, Task<OperationResult?>> work)
    {
        ClearResult();
        _op = kind;
        FileLog? log = null;
        try
        {
            log = new FileLog(FileLog.DefaultDirectory, logName);
            LogPath = log.Path;
        }
        catch { /* journal indisponible : l'opération reste possible, un NullLog est utilisé */ }
        ILog logger = (ILog?)log ?? NullLog.Instance;

        _cts = new CancellationTokenSource();
        var progress = new Progress<ProgressInfo>(OnProgress);
        try
        {
            IsBusy = true;
            ShowProgress = true;
            Percent = 0;
            StatusText = PhaseLabel(OperationPhase.Analysing);
            DetailText = "Analyse en cours…";
            BytesText = SpeedText = ElapsedText = RemainingText = "—";

            var result = await work(progress, logger);

            if (result == null)   // abandonné avant toute écriture (refus utilisateur ou blocage)
            {
                ShowProgress = false;
                return;
            }
            ShowResult(result);
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            ShowResult(new OperationResult(OperationOutcome.Failed, "Erreur inattendue : " + ex.Message,
                Array.Empty<string>(), 0, TimeSpan.Zero));
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            (log as IDisposable)?.Dispose();
            RefreshDisks();
            if (kind is OpKind.CreatePoint or OpKind.Revert) { _refreshing = true; try { RefreshPoints(); } finally { _refreshing = false; } }
        }
    }

    private void ShowResult(OperationResult r)
    {
        HasResult = true;
        switch (r.Outcome)
        {
            case OperationOutcome.Success:
                ResultKind = "ok";
                ResultTitle = "Opération terminée";
                StatusText = "Opération terminée";
                Percent = 100;
                break;
            case OperationOutcome.Cancelled:
                ResultKind = "cancelled";
                ResultTitle = "Opération annulée";
                StatusText = "Opération annulée";
                break;
            default:
                ResultKind = "error";
                ResultTitle = "Erreur";
                StatusText = "Erreur";
                break;
        }
        ResultText = r.Summary;
        ResultWarnings = r.Warnings.Count == 0 ? "" : string.Join("\n", r.Warnings.Select(w => "• " + w));
        if (r.Sha256Hex != null) ResultText += $"\nEmpreinte SHA-256 : {r.Sha256Hex}";
        ElapsedText = Dur(r.Duration);
    }

    private static IReadOnlyList<string> Lines(IEnumerable<SafetyIssue> issues) =>
        issues.Where(i => i.Severity != IssueSeverity.Blocking).Select(i => i.Message).ToList();

    private async Task<OperationResult?> RunCloneAsync(IProgress<ProgressInfo> progress, ILog log)
    {
        var src = SelectedSource!; var dst = SelectedDestination!;
        var pre = await Task.Run(() => CloneEngine.Preflight(_provider, src.Id, dst.Id));
        if (pre.IsBlocked) { _dialogs.ShowBlocked("Clonage impossible", pre.BlockingMessage); return null; }

        var ok = _dialogs.ConfirmErasure(new ConfirmRequest(
            "Confirmation d'effacement",
            "TOUTES LES DONNÉES du disque destination vont être DÉFINITIVEMENT SUPPRIMÉES.",
            dst.Describe(), Lines(pre.Issues), "EFFACER ET CLONER"));
        if (!ok) return null;

        return await CloneEngine.RunAsync(new CloneRequest
        {
            Provider = _provider, SourceId = src.Id, DestinationId = dst.Id,
            ErasureConfirmed = true, ConfirmedDestinationId = dst.Id
        }, progress, log, _cts!.Token);
    }

    private async Task<OperationResult?> RunImageAsync(IProgress<ProgressInfo> progress, ILog log)
    {
        var src = SelectedSource!;
        var pre = await Task.Run(() => ImageEngine.Preflight(_provider, src.Id));
        if (pre.IsBlocked) { _dialogs.ShowBlocked("Création d'image impossible", pre.BlockingMessage); return null; }

        return await ImageEngine.CreateAsync(new ImageRequest
        {
            Provider = _provider, SourceId = src.Id, OutputPath = OutputImagePath,
            OverwriteConfirmed = _outputConfirmedOverwrite   // la boîte « Enregistrer sous » a déjà demandé le remplacement
        }, progress, log, _cts!.Token);
    }

    private async Task<OperationResult?> RunCreatePointAsync(IProgress<ProgressInfo> progress, ILog log)
    {
        var src = SelectedSource!;
        string label = string.IsNullOrWhiteSpace(PointLabel) ? "Point de restauration" : PointLabel.Trim();
        try { Directory.CreateDirectory(PointsFolder); }
        catch (Exception ex)
        {
            _dialogs.ShowBlocked("Point de restauration impossible", "Le dossier des points n'est pas utilisable : " + ex.Message);
            return null;
        }

        string path = RestorePointCatalog.BuildPath(PointsFolder, label, DateTime.Now);
        return await ImageEngine.CreateAsync(new ImageRequest
        {
            Provider = _provider, SourceId = src.Id, OutputPath = path,
            FullDisk = true,             // TOUT le disque : toutes les partitions (ex. Windows + Skype Room) et l'espace hors partition
            Label = label, IsRestorePoint = true
        }, progress, log, _cts!.Token);
    }

    private async Task<OperationResult?> RunRevertAsync(IProgress<ProgressInfo> progress, ILog log)
    {
        var dst = SelectedSource!;           // le disque à ramener à l'état du point
        var point = SelectedPoint!.Point;
        var pre = await Task.Run(() => RestoreEngine.Preflight(_provider, point.Path, dst.Id));
        if (pre.IsBlocked)
        {
            string msg = pre.BlockingMessage;
            if (pre.Issues.Any(i => i.Code == "DEST_SYSTEM_DISK"))
                msg += "\n\nPour revenir à un point sur le disque Windows actif : démarrez l'ordinateur sur la clé USB WinPE " +
                       "(menu Démarrer → GHOST CLONAGE WINDOWS → Créer le support USB de démarrage), puis choisissez « Revenir à un point de restauration ».";
            _dialogs.ShowBlocked("Retour au point impossible", msg);
            return null;
        }

        var details = new List<string>(Lines(pre.Issues));
        details.AddRange(pre.Compatibility.Select(c => $"{c.Title} — {c.Detail}"));
        var ok = _dialogs.ConfirmErasure(new ConfirmRequest(
            "Revenir à ce point de restauration",
            $"Le disque va être ramené EXACTEMENT à l'état du point « {point.Label} » du {point.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}. " +
            "TOUT ce qui a changé sur le disque depuis (fichiers, programmes, mises à jour) sera DÉFINITIVEMENT PERDU.",
            dst.Describe(), details, "REVENIR À CE POINT"));
        if (!ok) return null;

        return await RestoreEngine.RunAsync(new RestoreRequest
        {
            Provider = _provider, ImagePath = point.Path, DestinationId = dst.Id,
            ErasureConfirmed = true, ConfirmedDestinationId = dst.Id
        }, progress, log, _cts!.Token);
    }

    private async Task<OperationResult?> RunRestoreAsync(IProgress<ProgressInfo> progress, ILog log)
    {
        var dst = SelectedDestination!;
        var pre = await Task.Run(() => RestoreEngine.Preflight(_provider, RestoreImagePath, dst.Id));
        if (pre.IsBlocked) { _dialogs.ShowBlocked("Restauration impossible", pre.BlockingMessage); return null; }

        var details = new List<string>(Lines(pre.Issues));
        details.AddRange(pre.Compatibility.Select(c => $"{c.Title} — {c.Detail}"));
        var ok = _dialogs.ConfirmErasure(new ConfirmRequest(
            "Confirmation d'effacement",
            "TOUTES LES DONNÉES du disque destination vont être DÉFINITIVEMENT REMPLACÉES par l'image.",
            dst.Describe(), details, "EFFACER ET RESTAURER"));
        if (!ok) return null;

        return await RestoreEngine.RunAsync(new RestoreRequest
        {
            Provider = _provider, ImagePath = RestoreImagePath, DestinationId = dst.Id,
            ErasureConfirmed = true, ConfirmedDestinationId = dst.Id
        }, progress, log, _cts!.Token);
    }
}
