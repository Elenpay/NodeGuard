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

namespace NodeGuard.Tests.E2E;

/// <summary>
/// All E2E test classes share this collection so they run sequentially: they compete for the
/// same seeded dev wallets and mine blocks on the same regtest chain, so running them in
/// parallel makes them flaky.
/// </summary>
[CollectionDefinition("E2E", DisableParallelization = true)]
public class E2ECollection
{
}
