using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Ghost.App.ViewModels;
using Ghost.Core.Operations;

namespace Ghost.App;

public partial class App : Application
{
    // Mode de contrôle automatique (CI) : GHOST_SMOKE=1, rapport écrit dans GHOST_SMOKE_OUT.
    // Il lance la vraie fenêtre, liste les vrais disques, puis quitte ; code 1 si la moindre erreur survient.
    private static readonly bool Smoke = Environment.GetEnvironmentVariable("GHOST_SMOKE") == "1";
    private static readonly string? SmokeOut = Environment.GetEnvironmentVariable("GHOST_SMOKE_OUT");
    private TextWriterTraceListener? _bindingListener;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            TryLog(args.Exception);
            if (Smoke)
            {
                WriteSmoke("ECHEC exception non gérée : " + args.Exception);
                Environment.Exit(1);
            }
            MessageBox.Show("Erreur inattendue : " + args.Exception.Message, "GHOST CLONAGE WINDOWS",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (Smoke)
        {
            var bindingLog = (SmokeOut ?? "smoke") + ".bindings.txt";
            _bindingListener = new TextWriterTraceListener(bindingLog);
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingListener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); RunSmokeReport(); };
            timer.Start();
        }

        base.OnStartup(e);
    }

    private void RunSmokeReport()
    {
        var sb = new StringBuilder();
        int code = 0;
        try
        {
            if (MainWindow is not MainWindow w) throw new InvalidOperationException("Fenêtre principale absente.");
            var vm = w.ViewModel;
            sb.AppendLine($"Fenêtre : visible={w.IsVisible} taille={w.ActualWidth:0}x{w.ActualHeight:0}");
            sb.AppendLine("État : " + vm.DiskStatus);
            foreach (var d in vm.SourceDisks)
                sb.AppendLine($"  {d.Title} | {d.Model} | {d.Capacity} | utilisé {d.Used} | {d.MediaType} | {d.Partitions} part. | {d.Table} | {d.Bus}{(d.IsSystem ? " | SYSTÈME" : "")}");

            if (!w.IsVisible) { sb.AppendLine("ECHEC : fenêtre non visible"); code = 1; }
            if (vm.SourceDisks.Count == 0) { sb.AppendLine("ECHEC : aucun disque détecté"); code = 1; }
            if (!vm.SourceDisks.Any(d => d.IsSystem)) { sb.AppendLine("ECHEC : aucun disque système identifié"); code = 1; }
            foreach (var d in vm.DestinationDisks.Where(d => d.IsSystem))
                if (d.CanBeDestination) { sb.AppendLine("ECHEC : un disque système est proposé comme destination"); code = 1; }
        }
        catch (Exception ex)
        {
            sb.AppendLine("ECHEC : " + ex);
            code = 1;
        }

        _bindingListener?.Flush();
        try
        {
            var bl = (SmokeOut ?? "smoke") + ".bindings.txt";
            string txt = File.Exists(bl) ? File.ReadAllText(bl) : "";
            if (txt.Contains("Error") || txt.Contains("Warning"))
            {
                sb.AppendLine("ECHEC erreurs de liaison XAML :");
                sb.AppendLine(txt);
                code = 1;
            }
        }
        catch { /* lecture impossible : pas d'erreur de liaison constatée */ }

        sb.AppendLine(code == 0 ? "SMOKE OK" : "SMOKE ECHEC");
        WriteSmoke(sb.ToString());
        Environment.Exit(code);
    }

    private static void WriteSmoke(string text)
    {
        try { if (SmokeOut != null) File.AppendAllText(SmokeOut, text + Environment.NewLine); } catch { }
    }

    private static void TryLog(Exception ex)
    {
        try
        {
            using var log = new FileLog(FileLog.DefaultDirectory, "erreur-interface");
            log.Error(ex.ToString());
        }
        catch { /* journal indisponible */ }
    }
}
