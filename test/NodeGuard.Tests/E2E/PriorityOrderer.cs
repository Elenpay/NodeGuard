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

using Xunit.Abstractions;
using Xunit.Sdk;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// Orders a class's <c>[E2EFact]</c> methods by <see cref="TestPriorityAttribute"/> (lowest first). This is
/// what lets the whole e2e suite run in one ordered <c>dotnet test</c> pass: xUnit v2 can't order test
/// classes, so the scenarios live in one class and (3) must run after (1) — it reuses (1)'s channel.
/// </summary>
public sealed class PriorityOrderer : ITestCaseOrderer
{
    // Must match this type's full name + the test assembly (referenced from [TestCaseOrderer]).
    public const string TypeName = "NodeGuard.Tests.E2E.PriorityOrderer";
    public const string AssemblyName = "NodeGuard.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase
    {
        var attributeType = typeof(TestPriorityAttribute).AssemblyQualifiedName!;
        return testCases
            .OrderBy(tc => tc.TestMethod.Method
                .GetCustomAttributes(attributeType)
                .FirstOrDefault()
                ?.GetNamedArgument<int>(nameof(TestPriorityAttribute.Priority)) ?? 0)
            .ThenBy(tc => tc.TestMethod.Method.Name, StringComparer.Ordinal);
    }
}

/// <summary>Explicit run order for an <c>[E2EFact]</c> in a <see cref="PriorityOrderer"/>-ordered class.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestPriorityAttribute : Attribute
{
    public TestPriorityAttribute(int priority) => Priority = priority;

    public int Priority { get; }
}
