// SPDX-License-Identifier: GPL-3.0-only
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EncoDeck.App.Controls;

/// <summary>
/// A progress ring for a choice waiting on the earbuds that appears only when the answer is slow
/// (issue #16): most settings are confirmed within a few hundred milliseconds, and a spinner that
/// flashes after every click makes the control feel slower than it is. The choice itself is shown at once.
/// </summary>
internal sealed class PendingSpinner
{
    /// <summary>How long a choice waits before the ring appears.</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(700);

    private readonly ProgressRing _ring;
    private readonly UIElement? _replaces;
    private readonly DispatcherQueueTimer? _timer;
    private bool _waiting;

    /// <param name="ring">The ring to show.</param>
    /// <param name="replaces">An element the ring stands in for while it is shown (a mode icon), if any.</param>
    public PendingSpinner(ProgressRing ring, UIElement? replaces = null)
    {
        _ring = ring;
        _replaces = replaces;
        _timer = DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_timer is not null)
        {
            _timer.Interval = Delay;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => Show(_waiting);
        }
        Show(false);
    }

    /// <summary>Starts the delay when a wait begins; hides the ring as soon as it ends.</summary>
    public bool Waiting
    {
        get => _waiting;
        set
        {
            if (_waiting == value) return;
            _waiting = value;
            if (!value)
            {
                _timer?.Stop();
                Show(false);
            }
            else if (_timer is null)
            {
                Show(true);
            }
            else
            {
                _timer.Start();
            }
        }
    }

    private void Show(bool visible)
    {
        _ring.IsActive = visible;
        _ring.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_replaces is not null) _replaces.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }
}
