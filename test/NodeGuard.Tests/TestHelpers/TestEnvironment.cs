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
using System.Runtime.CompilerServices;

namespace NodeGuard.TestHelpers;

internal static class TestEnvironment
{
    /// <summary>
    /// Fills in the environment variables that Constants reads in its static constructor.
    /// </summary>
    /// <remarks>
    /// Constants tolerates them being missing under test, but the values it exposes are readonly
    /// and initialized on first touch, so a test cannot set one after the fact. A module
    /// initializer runs before any code in this assembly, which makes the value deterministic no
    /// matter which test happens to touch Constants first. Anything already set by the environment
    /// wins, so a CI run can still point these elsewhere.
    /// </remarks>
    [ModuleInitializer]
    internal static void Initialize()
    {
        SetIfMissing("NBXPLORER_URI", "http://localhost:32838");
    }

    private static void SetIfMissing(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
