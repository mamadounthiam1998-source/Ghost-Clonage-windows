namespace Ghost.App.ViewModels;

public sealed record ConfirmRequest(
    string Title,
    string Headline,
    string TargetDescription,
    IReadOnlyList<string> Details,
    string ConfirmLabel);

public interface IDialogService
{
    /// <summary>Confirmation explicite d'effacement. Vrai uniquement si l'utilisateur a coché ET validé.</summary>
    bool ConfirmErasure(ConfirmRequest request);

    void ShowBlocked(string title, string message);

    string? PickSaveImage();
    string? PickOpenImage();

    /// <summary>Confirme la fermeture pendant une opération en cours.</summary>
    bool ConfirmCancelOnClose();
}
