// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

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
    /// Disposes the component and cancels the associated cancellation token.
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
