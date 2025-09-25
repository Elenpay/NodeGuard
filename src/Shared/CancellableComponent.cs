namespace NodeGuard.Shared;
using Microsoft.AspNetCore.Components;

/// <summary>
/// Base class for components which require a cancellation token which
/// requests cancellation when the component is detached.
/// </summary>
public abstract class CancellableComponent : ComponentBase, IDisposable
{
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// Cancellation token which becomes cancelled when the component detaches
    /// </summary>
    protected CancellationToken ComponentCancellationToken => (_cancellationTokenSource ??= new()).Token;

    /// <summary>
    /// Dispose the component, cancelling the cancellation token if it has been created.
    /// </summary>
    public virtual void Dispose()
    {
        if (_cancellationTokenSource == null)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = null;
        GC.SuppressFinalize(this);
    }
}