using System.Windows;
using Ghost.App.ViewModels;

namespace Ghost.App.Views;

/// <summary>
/// Confirmation d'effacement. Le bouton rouge reste désactivé tant que la case n'est pas cochée ;
/// « Annuler » est le bouton par défaut (Entrée n'efface jamais).
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(ConfirmRequest r)
    {
        InitializeComponent();
        TitleText.Text = r.Title;
        HeadlineText.Text = r.Headline;
        TargetText.Text = r.TargetDescription;
        DetailsList.ItemsSource = r.Details;
        ConfirmBtn.Content = r.ConfirmLabel;
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e) =>
        ConfirmBtn.IsEnabled = UnderstandBox.IsChecked == true;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (UnderstandBox.IsChecked != true) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
