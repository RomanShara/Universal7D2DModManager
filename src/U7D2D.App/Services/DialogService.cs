namespace U7D2D.App.Services;

public sealed class ConfirmRequest
{
    public ConfirmRequest(string title, string message, string confirmText, string cancelText, bool destructive, TaskCompletionSource<bool> completion)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
        Destructive = destructive;
        Completion = completion;
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }
    public bool Destructive { get; }
    public TaskCompletionSource<bool> Completion { get; }
}

/// <summary>Modal confirmation used before destructive operations (delete, replace, restore).</summary>
public sealed class DialogService
{
    public event Action? Changed;

    public ConfirmRequest? Current { get; private set; }

    public Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null, bool destructive = false)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Current = new ConfirmRequest(title, message,
            confirmText ?? Localization.S("dlg.confirm"),
            cancelText ?? Localization.S("dlg.cancel"),
            destructive, completion);
        Changed?.Invoke();
        return completion.Task;
    }

    public void Resolve(bool result)
    {
        var request = Current;
        Current = null;
        Changed?.Invoke();
        request?.Completion.TrySetResult(result);
    }
}
