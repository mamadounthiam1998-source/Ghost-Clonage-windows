using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ghost.App;
using Ghost.App.ViewModels;
using Ghost.Core.Devices;
using Ghost.Core.Disks;
using Ghost.Core.Tests;
using Xunit;

namespace Ghost.App.Tests;

/// <summary>Faux dialogues : confirment ou refusent à la demande, et enregistrent ce qui est affiché.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public bool Confirm { get; set; } = true;
    public int ConfirmCalls { get; private set; }
    public ConfirmRequest? LastRequest { get; private set; }
    public List<string> Blocked { get; } = new();

    public bool ConfirmErasure(ConfirmRequest request) { ConfirmCalls++; LastRequest = request; return Confirm; }
    public void ShowBlocked(string title, string message) => Blocked.Add(title + " : " + message);
    public string? PickSaveImage() => null;
    public string? PickOpenImage() => null;
    public string? PickFolder() => null;
    public bool ConfirmCancelOnClose() => true;
}

public class MainWindowUiTests
{
    private static void RunSta(Action a)
    {
        Exception? ex = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { ex = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (ex != null) ExceptionDispatchInfo.Capture(ex).Throw();
    }

    /// <summary>Fait tourner la vraie boucle de messages WPF jusqu'à ce que la condition soit vraie.</summary>
    private static void PumpUntil(Func<bool> cond, int timeoutMs, string what)
    {
        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();
        bool timedOut = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            if (cond()) frame.Continue = false;
            else if (sw.ElapsedMilliseconds > timeoutMs) { timedOut = true; frame.Continue = false; }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.False(timedOut, $"Délai dépassé en attendant : {what}");
    }

    /// <summary>Laisse la boucle de messages tourner un court instant (rendu, animations, liaisons).</summary>
    private static void Settle(int ms = 300)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = Find<T>(c);
            if (r != null) return r;
        }
        return default;
    }

    private static void Shot(Window w, string name)
    {
        try
        {
            string dir = Environment.GetEnvironmentVariable("GHOST_UI_SHOTS") ?? Path.Combine(Path.GetTempPath(), "ghost-ui-shots");
            Directory.CreateDirectory(dir);
            w.UpdateLayout();
            int pw = (int)w.ActualWidth, ph = (int)w.ActualHeight;
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(w);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        catch { /* la capture est un bonus : elle ne doit jamais faire échouer le test */ }
    }

    [Fact]
    public void Real_window_runs_all_three_modes_end_to_end_without_binding_errors()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            var unhandled = new List<Exception>();
            var bindingOut = new StringWriter();
            var listener = new TextWriterTraceListener(bindingOut);
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            // Disques virtuels : source GPT, destination plus grande avec de vieilles données, 3e disque vide
            var sp = env.NewDisk("src", 64L << 20);
            var dp = env.NewDisk("dst", 96L << 20);
            env.NewDisk("dst2", 96L << 20);
            env.NewDisk("sys", 64L << 20);
            env.Provider.SystemDiskIds.Add("sys");
            using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildGpt(d, DiskBuilder.WindowsLikeParts());
            using (var d = FileBlockDevice.Open(dp, true)) DiskBuilder.FillPattern(d, 0, d.Length, 77, false);
            var dstBefore = File.ReadAllBytes(dp);

            // Application WPF réelle : charge App.xaml (thème, styles, animations)
            var app = new Ghost.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            app.DispatcherUnhandledException += (_, e) => { unhandled.Add(e.Exception); e.Handled = true; };

            // Comme dans l'application réelle : les continuations « await » reviennent sur le thread de l'interface
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var dialogs = new FakeDialogs();
            var window = new MainWindow(env.Provider, dialogs, env.PathOf("settings.json"));
            window.Show();
            var vm = window.ViewModel;
            Settle(600);

            // 1) Démarrage : disques affichés, disque système non proposé en destination
            Assert.Equal(4, vm.SourceDisks.Count);
            Assert.False(vm.DestinationDisks.Single(x => x.Id == "sys").CanBeDestination);
            Assert.False(vm.CanStart);
            Shot(window, "1-accueil-cloner");

            // 2) Les trois modes s'affichent sans erreur
            vm.Mode = AppMode.Image; Settle(); Shot(window, "2-mode-image");
            vm.Mode = AppMode.Restore; Settle(); Shot(window, "3-mode-restaurer");
            vm.Mode = AppMode.Clone; Settle();

            // 3) Clonage refusé par l'utilisateur : rien n'est écrit
            vm.SelectedSource = vm.SourceDisks.Single(x => x.Id == "src");
            vm.SelectedDestination = vm.DestinationDisks.Single(x => x.Id == "dst");
            Assert.True(vm.CanStart);
            dialogs.Confirm = false;
            vm.StartCommand.Execute(null);
            PumpUntil(() => !vm.IsBusy && dialogs.ConfirmCalls == 1, 20000, "refus de confirmation");
            Assert.False(vm.HasResult);
            Assert.Equal(dstBefore, File.ReadAllBytes(dp));

            // 4) Clonage confirmé : opération complète, barre à 100 %, résultat « terminé »
            dialogs.Confirm = true;
            vm.StartCommand.Execute(null);
            PumpUntil(() => vm.IsBusy, 10000, "démarrage du clonage");
            Shot(window, "4-clonage-en-cours");
            PumpUntil(() => vm.HasResult, 60000, "fin du clonage");
            Settle();
            Assert.True(vm.ResultKind == "ok", "Clonage : " + vm.ResultText);
            Assert.Equal("Opération terminée", vm.StatusText);
            Assert.Equal(100.0, vm.Percent);
            var bar = Find<ProgressBar>(window);
            Assert.NotNull(bar);
            Assert.Equal(100.0, bar!.Value);
            Assert.Contains("EFFACER", dialogs.LastRequest!.ConfirmLabel);
            Assert.Contains("dst", dialogs.LastRequest.TargetDescription);
            Shot(window, "5-clonage-termine");

            // 5) Création d'image
            var img = env.PathOf("ui.gci");
            vm.Mode = AppMode.Image; Settle();
            vm.SelectedSource = vm.SourceDisks.Single(x => x.Id == "src");
            vm.OutputImagePath = img;
            Assert.True(vm.CanStart);
            vm.StartCommand.Execute(null);
            PumpUntil(() => vm.HasResult, 60000, "fin de la création d'image");
            Assert.True(vm.ResultKind == "ok", "Image : " + vm.ResultText);
            Assert.True(File.Exists(img));
            Assert.False(File.Exists(img + ".partial"));

            // 6) Restauration (avec rapport de compatibilité dans la confirmation)
            vm.Mode = AppMode.Restore; Settle();
            vm.RestoreImagePath = img;
            Assert.Contains("Partitions (3)", vm.ImageInfo);
            vm.SelectedDestination = vm.DestinationDisks.Single(x => x.Id == "dst2");
            Assert.True(vm.CanStart);
            Shot(window, "6-restauration-prete");
            vm.StartCommand.Execute(null);
            PumpUntil(() => vm.HasResult, 60000, "fin de la restauration");
            Assert.True(vm.ResultKind == "ok", "Restauration : " + vm.ResultText);
            Assert.Contains(dialogs.LastRequest!.Details, d => d.Contains("garantie"));
            Shot(window, "7-restauration-terminee");

            // 7) Image invalide : refusée dès la sélection, jamais restaurable
            var bad = env.PathOf("mauvaise.gci");
            File.WriteAllBytes(bad, new byte[4096]);
            vm.RestoreImagePath = bad;
            Assert.StartsWith("⚠", vm.ImageInfo);
            Assert.False(vm.CanStart);

            // 8) Point de restauration du DISQUE ENTIER, puis retour à ce point après dégâts
            vm.Mode = AppMode.RestorePoint; Settle();
            Shot(window, "8-points-restauration");
            vm.PointsFolder = env.PathOf("points");               // le dossier est créé par le logiciel
            vm.PointLabel = "Avant mise à jour";
            vm.SelectedSource = vm.SourceDisks.Single(x => x.Id == "src");
            Assert.True(vm.CanStart);
            Assert.False(vm.CanRevert);                           // aucun point choisi
            File.Copy(sp, env.PathOf("reference.img"));
            vm.StartCommand.Execute(null);
            PumpUntil(() => vm.HasResult, 60000, "création du point de restauration");
            Assert.True(vm.ResultKind == "ok", "Point : " + vm.ResultText);
            Assert.Single(vm.Points);
            Assert.Equal("Avant mise à jour", vm.Points[0].Label);
            Assert.Contains("disque entier", vm.Points[0].Origin);
            Assert.True(vm.HasResult, "le résultat doit rester affiché après le rafraîchissement de la liste des points");
            Shot(window, "9-point-cree");

            using (var d = FileBlockDevice.Open(sp, true))        // « mise à jour qui tourne mal »
                DiskBuilder.FillPattern(d, 2048L * 512, 8L << 20, 999, false);
            Assert.False(DiskBuilder.RangesEqual(sp, env.PathOf("reference.img"), 64L << 20));

            vm.SelectedPoint = vm.Points[0];
            Assert.True(vm.CanRevert);
            dialogs.Confirm = true;
            vm.RevertCommand.Execute(null);
            PumpUntil(() => vm.HasResult, 60000, "retour au point de restauration");
            Assert.True(vm.ResultKind == "ok", "Retour : " + vm.ResultText);
            Assert.Contains("REVENIR", dialogs.LastRequest!.ConfirmLabel);
            Assert.True(DiskBuilder.RangesEqual(sp, env.PathOf("reference.img"), 64L << 20),
                "le disque doit être revenu exactement à l'état du point");
            Shot(window, "10-retour-termine");

            // Retour sur le disque Windows actif : refusé avec explication WinPE, rien n'est écrit
            int confirms = dialogs.ConfirmCalls;
            vm.SelectedSource = vm.SourceDisks.Single(x => x.Id == "sys");
            vm.SelectedPoint = vm.Points[0];
            Assert.True(vm.CanRevert);
            vm.RevertCommand.Execute(null);
            PumpUntil(() => !vm.IsBusy && dialogs.Blocked.Count >= 1, 20000, "refus du retour sur le disque système");
            Assert.Contains(dialogs.Blocked, m => m.Contains("WinPE"));
            Assert.Equal(confirms, dialogs.ConfirmCalls);

            // Aucune erreur de liaison, aucune exception non gérée
            listener.Flush();
            window.Close();
            app.Shutdown();
            Assert.Empty(unhandled);
            Assert.True(string.IsNullOrWhiteSpace(bindingOut.ToString()), "Erreurs de liaison XAML :\n" + bindingOut);
        });
    }
}
