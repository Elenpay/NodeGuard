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

﻿using NBitcoin;

namespace FundsManager.Helpers
{
    public static class CurrentNetworkHelper
    {
        /// <summary>
        /// Gets the current settings for the system network
        /// </summary>
        /// <returns></returns>
        public static Network GetCurrentNetwork()
        {
            var network = Environment.GetEnvironmentVariable("BITCOIN_NETWORK")?.ToUpper();

            var result = network switch
            {
                "REGTEST" => Network.RegTest,
                "MAINNET" => Network.Main,
                "TESTNET" => Network.TestNet,
                _ => Network.RegTest
            };
            return result;
        }
    }
}