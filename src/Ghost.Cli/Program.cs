using Ghost.Core.Disks;
using Ghost.Core.Imaging;
using Ghost.Core.Operations;
using Ghost.Core.Safety;
using Ghost.Windows;

// GHOST CLONAGE WINDOWS — version console (utilisable dans WinPE, où l'interface WPF n'est pas disponible).
Console.OutputEncoding = System.Text.Encoding.UTF8;

const string Usage = """
GHOST CLONAGE WINDOWS — console

  GhostClonageCli list
  GhostClonageCli clone   --src <N> --dst <M>
  GhostClonageCli image   --src <N> --out <fichier.gci>
  GhostClonageCli restore --image <fichier.gci> --dst <M>
  GhostClonageCli verify  --image <fichier.gci>
  GhostClonageCli points       --dir <dossier>
  GhostClonageCli point-create --src <N> --dir <dossier> [--label <nom>]
  GhostClonageCli point-revert --file <fichier.gci> --dst <M>

Un point de restauration = image du DISQUE ENTIER (toutes les partitions et l'espace hors partition).

N et M sont les numéros de disque affichés par « list ».
Toute opération destructive demande de retaper « EFFACER DISQUE M ».
""";

var provider = new WindowsDiskProvider();
var a = args.ToList();
if (a.Count == 0) { Console.WriteLine(Usage); return 1; }

string? Opt(string name) { int i = a.IndexOf(name); return i >= 0 && i + 1 < a.Count ? a[i + 1] : null; }
string Id(string? n) => "PhysicalDrive" + (n ?? throw new ArgumentException("Numéro de disque manquant."));

using var log = new FileLog(FileLog.DefaultDirectory, a[0]);
Console.WriteLine($"Journal : {log.Path}");

var prog = new Progress<ProgressInfo>(p =>
{
    Console.Write($"\r{p.Message,-60} {p.Percent,5:0.0}%  {SafetyGuard.Fmt((long)p.BytesPerSecond)}/s  reste {(p.Remaining is { } r ? r.ToString(@"hh\:mm\:ss") : "--:--:--")}   ");
});

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nAnnulation demandée…"); };

static bool Confirm(string diskId)
{
    string n = diskId.Replace("PhysicalDrive", "");
    Console.WriteLine($"\n!!! TOUTES LES DONNÉES du disque {n} seront DÉFINITIVEMENT EFFACÉES !!!");
    Console.Write($"Pour confirmer, tapez exactement : EFFACER DISQUE {n}\n> ");
    return Console.ReadLine()?.Trim() == $"EFFACER DISQUE {n}";
}

static int Report(OperationResult r)
{
    Console.WriteLine();
    Console.WriteLine(r.IsSuccess ? $"[OK] {r.Summary}" : $"[ERREUR/ANNULÉ] {r.Summary}");
    foreach (var w in r.Warnings) Console.WriteLine("  ! " + w);
    if (r.Sha256Hex != null) Console.WriteLine("  Empreinte : " + r.Sha256Hex);
    return r.IsSuccess ? 0 : 2;
}

static void PrintIssues(IEnumerable<SafetyIssue> issues)
{
    foreach (var i in issues) Console.WriteLine($"  [{i.Severity}] {i.Message}");
}

try
{
    switch (a[0])
    {
        case "list":
            foreach (var d in provider.ListDisks())
                Console.WriteLine($"{d.DisplayName,-9} {d.Model,-32} {SafetyGuard.Fmt(d.Size),10}  {d.Media,-7} {d.Style}  {d.PartitionCount} partition(s){(d.IsSystemDisk ? "  [SYSTÈME]" : "")}");
            return 0;

        case "clone":
        {
            string src = Id(Opt("--src")), dst = Id(Opt("--dst"));
            var pre = CloneEngine.Preflight(provider, src, dst);
            PrintIssues(pre.Issues);
            if (pre.IsBlocked) return 2;
            if (!Confirm(dst)) { Console.WriteLine("Confirmation refusée : rien n'a été modifié."); return 1; }
            return Report(await CloneEngine.RunAsync(new CloneRequest
            {
                Provider = provider, SourceId = src, DestinationId = dst,
                ErasureConfirmed = true, ConfirmedDestinationId = dst
            }, prog, log, cts.Token));
        }

        case "image":
        {
            string src = Id(Opt("--src"));
            string output = Opt("--out") ?? throw new ArgumentException("--out manquant.");
            var pre = ImageEngine.Preflight(provider, src);
            PrintIssues(pre.Issues);
            if (pre.IsBlocked) return 2;
            return Report(await ImageEngine.CreateAsync(new ImageRequest
            {
                Provider = provider, SourceId = src, OutputPath = output,
                OverwriteConfirmed = false
            }, prog, log, cts.Token));
        }

        case "restore":
        {
            string img = Opt("--image") ?? throw new ArgumentException("--image manquant.");
            string dst = Id(Opt("--dst"));
            var pre = RestoreEngine.Preflight(provider, img, dst);
            PrintIssues(pre.Issues);
            foreach (var c in pre.Compatibility) Console.WriteLine($"  [{c.Severity}] {c.Title} — {c.Detail}");
            if (pre.IsBlocked) { Console.WriteLine(pre.BlockingMessage); return 2; }
            if (!Confirm(dst)) { Console.WriteLine("Confirmation refusée : rien n'a été modifié."); return 1; }
            return Report(await RestoreEngine.RunAsync(new RestoreRequest
            {
                Provider = provider, ImagePath = img, DestinationId = dst,
                ErasureConfirmed = true, ConfirmedDestinationId = dst
            }, prog, log, cts.Token));
        }

        case "points":
        {
            string dir = Opt("--dir") ?? throw new ArgumentException("--dir manquant.");
            var l = RestorePointCatalog.List(dir);
            if (l.Points.Count == 0) Console.WriteLine("Aucun point de restauration valide dans ce dossier.");
            int i = 1;
            foreach (var p in l.Points)
                Console.WriteLine($"{i++,2}. {p.Label} — {p.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} — {SafetyGuard.Fmt(p.FileSize)} — {p.PartitionCount} partition(s) : {p.PartitionSummary}\n    {p.Path}");
            if (l.InvalidFiles > 0) Console.WriteLine($"({l.InvalidFiles} fichier(s) incomplet(s) ou corrompu(s) ignoré(s))");
            return 0;
        }

        case "point-create":
        {
            string src = Id(Opt("--src"));
            string dir = Opt("--dir") ?? throw new ArgumentException("--dir manquant.");
            string label = Opt("--label") ?? "Point de restauration";
            Directory.CreateDirectory(dir);
            var pre = ImageEngine.Preflight(provider, src);
            PrintIssues(pre.Issues);
            if (pre.IsBlocked) return 2;
            return Report(await ImageEngine.CreateAsync(new ImageRequest
            {
                Provider = provider, SourceId = src, OutputPath = RestorePointCatalog.BuildPath(dir, label, DateTime.Now),
                FullDisk = true, Label = label, IsRestorePoint = true
            }, prog, log, cts.Token));
        }

        case "point-revert":
        {
            string file = Opt("--file") ?? throw new ArgumentException("--file manquant.");
            string dst = Id(Opt("--dst"));
            var pre = RestoreEngine.Preflight(provider, file, dst);
            PrintIssues(pre.Issues);
            foreach (var c in pre.Compatibility) Console.WriteLine($"  [{c.Severity}] {c.Title} — {c.Detail}");
            if (pre.IsBlocked) { Console.WriteLine(pre.BlockingMessage); return 2; }
            if (!Confirm(dst)) { Console.WriteLine("Confirmation refusée : rien n'a été modifié."); return 1; }
            return Report(await RestoreEngine.RunAsync(new RestoreRequest
            {
                Provider = provider, ImagePath = file, DestinationId = dst,
                ErasureConfirmed = true, ConfirmedDestinationId = dst
            }, prog, log, cts.Token));
        }

        case "verify":
        {
            string img = Opt("--image") ?? throw new ArgumentException("--image manquant.");
            using var r = ImageReader.Open(img);
            var t = new ProgressTracker(prog);
            t.SetTotal(r.ExtentLength);
            t.Phase(OperationPhase.Verifying, "Vérification de l'image…");
            var v = r.Verify(t, cts.Token);
            Console.WriteLine($"\n{(v.IsValid ? "[OK]" : "[INVALIDE]")} {v.Message}");
            return v.IsValid ? 0 : 2;
        }

        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.WriteLine("Erreur : " + ex.Message);
    log.Error(ex.ToString());
    return 2;
}
