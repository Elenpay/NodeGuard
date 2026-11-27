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

using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;

namespace NodeGuard.Tests;

/// <summary>
/// OneSignalApi 2.3.0 was built against Polly 7, but NSpark needs Polly 8. Polly 8 still ships the
/// v7 API; JIT-compiling every OneSignalApi method binds each Polly member it uses, so a member
/// missing from Polly 8 fails here instead of when a notification is sent
/// </summary>
public class PollyCompatibilityTests
{
    [Fact]
    public void OneSignalApi_BindsToPolly8()
    {
        var oneSignal = typeof(OneSignalApi.Api.DefaultApi).Assembly;
        var polly = typeof(Polly.Policy).Assembly;

        oneSignal.GetReferencedAssemblies().Should().Contain(a => a.Name == "Polly");
        polly.GetName().Version!.Major.Should().Be(8);

        var failures = new List<string>();
        foreach (var type in oneSignal.GetTypes().Where(t => !t.ContainsGenericParameters))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                                   BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null))
            {
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                catch (Exception e) when (e is MissingMemberException or TypeLoadException)
                {
                    failures.Add($"{type.FullName}.{method.Name}: {e.Message}");
                }
            }
        }

        failures.Should().BeEmpty();
    }
}
