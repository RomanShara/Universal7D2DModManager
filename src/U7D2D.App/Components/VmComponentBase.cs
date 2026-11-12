using Microsoft.AspNetCore.Components;
using U7D2D.App.Services;
using System.ComponentModel;

namespace U7D2D.App.Components;

/// <summary>Base component that re-renders when its view model raises PropertyChanged (from any thread).</summary>
public abstract class VmComponentBase : ComponentBase, IDisposable
{
    private INotifyPropertyChanged? _subscribed;

    protected abstract INotifyPropertyChanged ViewModel { get; }

    protected override void OnInitialized()
    {
        Subscribe();
        Localization.Changed += OnLanguageChanged;
    }

    protected override void OnParametersSet() => Subscribe();

    // The language lives outside the view model, so pages listen to it directly. Otherwise a page
    // keeps whatever language it was first rendered with.
    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);

    private void Subscribe()
    {
        if (ReferenceEquals(_subscribed, ViewModel)) return;
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelChanged;
        _subscribed = ViewModel;
        _subscribed.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) =>
        _ = InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelChanged;
        _subscribed = null;
        Localization.Changed -= OnLanguageChanged;
    }
}
