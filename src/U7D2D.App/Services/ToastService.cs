namespace U7D2D.App.Services;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record ToastMessage(Guid Id, ToastKind Kind, string Title, string Message)
{
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
}

/// <summary>Publishes transient notifications rendered by <c>ToastHost</c>.</summary>
public sealed class ToastService
{
    public event Action<ToastMessage>? Published;

    public void Info(string title, string message = "") => Show(ToastKind.Info, title, message);
    public void Success(string title, string message = "") => Show(ToastKind.Success, title, message);
    public void Warning(string title, string message = "") => Show(ToastKind.Warning, title, message);
    public void Error(string title, string message = "") => Show(ToastKind.Error, title, message);

    public void Show(ToastKind kind, string title, string message) =>
        Published?.Invoke(new ToastMessage(Guid.NewGuid(), kind, title, message));
}
