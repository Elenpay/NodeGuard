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

namespace NodeGuard.Helpers;

/// <summary>Identifies the API token that authenticated a call, without its secret value.</summary>
public sealed record AuthenticatedApiToken(int Id, string Name)
{
    /// <summary>
    /// Key under which the authenticating token is stored in <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/>,
    /// so <see cref="NodeGuard.Services.AuditService"/> records it on every audit of the call.
    /// </summary>
    public const string HttpContextItemKey = "NodeGuard.ApiToken";
}
