// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using EncoDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// Keeps a control in its "&lt;prefix&gt;Low" or "&lt;prefix&gt;Normal" visual state as a battery's
/// <see cref="BatteryItemViewModel.IsLow"/> changes. The states set the critical colour through ThemeResource, so it
/// follows the control's own theme (EncoDeck can differ from Windows). Listens only while the control is loaded: the
/// battery view models live as long as the app.
/// </summary>
internal sealed class LowBatteryStates
{
    private readonly Control _control;
    private readonly string _prefix;
    private BatteryItemViewModel? _item;

    public LowBatteryStates(Control control, string prefix = "")
    {
        _control = control;
        _prefix = prefix;
        control.Loaded += (_, _) => Attach();
        // Moving the control to another parent can raise Unloaded after the new Loaded.
        control.Unloaded += (_, _) => { if (!control.IsLoaded) Detach(); };
    }

    public BatteryItemViewModel? Item
    {
        get => _item;
        set
        {
            Detach();
            _item = value;
            if (_control.IsLoaded) Attach();
        }
    }

    private void Attach()
    {
        if (_item is null) return;
        _item.PropertyChanged -= OnItemChanged;
        _item.PropertyChanged += OnItemChanged;
        Update();
    }

    private void Detach()
    {
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BatteryItemViewModel.IsLow) or null or "") Update();
    }

    private void Update() => VisualStateManager.GoToState(_control, _prefix + (_item?.IsLow == true ? "Low" : "Normal"), false);
}
