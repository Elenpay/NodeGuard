/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using NodeGuard.Helpers;

namespace NodeGuard.Tests.Jobs;

/// <summary>
/// The routing-engine job tests share this collection so they run sequentially: they tune the
/// engine by assigning to the static <c>Constants.ROUTING_ENGINE_*</c> fields and restore them in
/// a finally block, so running the classes in parallel lets one class observe another's tuning.
/// </summary>
[CollectionDefinition("RoutingEngine", DisableParallelization = true)]
public class RoutingEngineCollection
{
}

/// <summary>
/// Helpers shared by the routing-engine job tests.
/// </summary>
public static class RoutingEngineSwitch
{
    /// <summary>
    /// Runs <paramref name="body"/> with the global kill switch forced to <paramref name="enabled"/>,
    /// restoring the previous value afterwards even if the body throws. One copy, because a missed
    /// restore leaks static state into every other class in the collection.
    /// </summary>
    public static async Task WithEngine(bool enabled, Func<Task> body)
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        Constants.ROUTING_ENGINE_ENABLED = enabled;
        try
        {
            await body();
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
        }
    }
}
