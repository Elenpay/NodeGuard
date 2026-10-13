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

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NodeGuard.Services.Spark;

public class SparkPaymentLauncherTests
{
    private readonly ISparkSwapService _swaps = Substitute.For<ISparkSwapService>();
    private readonly SparkPayment _payment = new(new Node { Id = 1, Name = "alice" }, 7, "lnbcrt5m1invoice", 5_000_000, null, 600);

    private SparkPaymentLauncher Launcher() => new(
        new ServiceCollection().AddScoped(_ => _swaps).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        NullLogger<SparkPaymentLauncher>.Instance);

    [Fact]
    public async Task APayment_IsMadeByTheSwapServiceOfAScopeOfItsOwn()
    {
        await Launcher().PayAsync(_payment);

        await _swaps.Received(1).PayAsync(_payment);
    }

    [Fact]
    public async Task APaymentThatCouldNotBeFollowed_IsLeftToTheMonitor_WithoutThrowing()
    {
        _swaps.PayAsync(_payment).ThrowsAsync(new InvalidOperationException("LND is down"));

        var act = () => Launcher().PayAsync(_payment);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Launch_PaysInTheBackground()
    {
        var paid = new TaskCompletionSource();
        _swaps.PayAsync(_payment).Returns(_ =>
        {
            paid.SetResult();
            return Task.CompletedTask;
        });

        Launcher().Launch(_payment);

        await paid.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
