using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Ghost.App.ViewModels;
using Ghost.App.Views;
using Ghost.Windows;

namespace Ghost.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(new WindowsDiskProvider(), new DialogService(this));
        DataContext = _vm;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Fermeture pendant une opération : arrêt contrôlé uniquement, jamais d'interruption brutale.
        if (_vm.IsOperationRunning)
        {
            _vm.RequestClose();
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
