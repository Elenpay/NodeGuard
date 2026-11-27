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
using Microsoft.EntityFrameworkCore;

namespace NodeGuard.Data.Repositories;

public class SparkSignerIdentityRepositoryTests
{
    private const string Identity = "032986ec06431b7b80a4171836174ac10c07e1cb1dff4af93082ee5f3ebca64a0a";
    private const string Other = "022bf283544b16c0622daecb79422007d167eca6ce9f0c98c0c49833b1f7170bfe";

    private readonly Random _random = new();

    private SparkSignerIdentityRepository Repository()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "SparkSignerIdentity" + _random.Next())
            .Options;
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(() => new ApplicationDbContext(options));
        return new SparkSignerIdentityRepository(factory.Object);
    }

    [Fact]
    public async Task NothingIsSaved_Initially() =>
        (await Repository().GetAsync("ed0210c8", 0)).Should().BeNull();

    [Fact]
    public async Task TheFirstIdentity_IsSaved_WithItsDates()
    {
        var repository = Repository();

        var (saved, created) = await repository.GetOrAddAsync("ed0210c8", 0, Identity);

        created.Should().BeTrue();
        saved.IdentityPublicKey.Should().Be(Identity);
        saved.CreationDatetime.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        (await repository.GetAsync("ed0210c8", 0))!.IdentityPublicKey.Should().Be(Identity);
    }

    [Fact]
    public async Task ASavedIdentity_IsNeverOverwritten()
    {
        var repository = Repository();
        await repository.GetOrAddAsync("ed0210c8", 0, Identity);

        var (saved, created) = await repository.GetOrAddAsync("ed0210c8", 0, Other);

        created.Should().BeFalse();
        saved.IdentityPublicKey.Should().Be(Identity);
        (await repository.GetAsync("ed0210c8", 0))!.IdentityPublicKey.Should().Be(Identity);
    }

    [Fact]
    public async Task EachSeedAndAccount_HasItsOwn()
    {
        var repository = Repository();
        await repository.GetOrAddAsync("ed0210c8", 0, Identity);

        (await repository.GetOrAddAsync("ed0210c8", 1, Other)).Created.Should().BeTrue();
        (await repository.GetOrAddAsync("c0bd1022", 0, Other)).Created.Should().BeTrue();
        (await repository.GetAsync("ed0210c8", 0))!.IdentityPublicKey.Should().Be(Identity);
    }
}
