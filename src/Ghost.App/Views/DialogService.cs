using System.Windows;
using Ghost.App.ViewModels;
using Ghost.Core.Imaging;
using Microsoft.Win32;

namespace Ghost.App.Views;

public sealed class DialogService : IDialogService
{
    private readonly Window _owner;
    public DialogService(Window owner) => _owner = owner;

    public bool ConfirmErasure(ConfirmRequest request)
    {
        var w = new ConfirmWindow(request) { Owner = _owner };
        return w.ShowDialog() == true;
    }

    public void ShowBlocked(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public string? PickSaveImage()
    {
        var d = new SaveFileDialog
        {
            Title = "Enregistrer l'image disque",
            Filter = "Image GHOST CLONAGE (*.gci)|*.gci",
            DefaultExt = ImageFormat.Extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"Sauvegarde-{DateTime.Now:yyyy-MM-dd}.gci"
        };
        return d.ShowDialog(_owner) == true ? d.FileName : null;
    }

    public string? PickOpenImage()
    {
        var d = new OpenFileDialog
        {
            Title = "Choisir une image à restaurer",
            Filter = "Image GHOST CLONAGE (*.gci)|*.gci",
            CheckFileExists = true
        };
        return d.ShowDialog(_owner) == true ? d.FileName : null;
    }

    public bool ConfirmCancelOnClose() =>
        MessageBox.Show(_owner,
            "Une opération est en cours. Voulez-vous l'interrompre de manière contrôlée ?\n\nLe disque destination sera alors incomplet.",
            "GHOST CLONAGE WINDOWS", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
        == MessageBoxResult.Yes;
}
