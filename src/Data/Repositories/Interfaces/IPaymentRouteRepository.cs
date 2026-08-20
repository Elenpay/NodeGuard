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

using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IPaymentRouteRepository
{
    /// <summary>
    /// Inserts a payment (with its hops), or refreshes an existing row in place. Keyed by
    /// PaymentHash; returns whether a new row was created.
    ///
    /// <para>An insert-only write cannot be correct here: LND lets a failed payment hash be
    /// retried, so the same hash can reach a terminal state twice (FAILED, then SUCCEEDED on the
    /// retry). Skipping the second one leaves the payment permanently recorded as failed.</para>
    /// </summary>
    Task<(bool inserted, string? error)> UpsertAsync(PaymentRoute payment);

    /// <summary>Payments (with hops eagerly loaded) originated by <paramref name="originNodePubKey"/> and created within [start, end].</summary>
    Task<List<PaymentRoute>> GetByCreatedAtRangeAsync(string originNodePubKey, DateTimeOffset start, DateTimeOffset end);
}
