// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

namespace NodeGuard.Helpers;

public class NoUTXOsAvailableException : Exception { }

public class UTXOsNoLongerValidException : Exception { }

public class NBXplorerNotFullySyncedException : Exception
{
    public NBXplorerNotFullySyncedException() : base("Error, nbxplorer not fully synched") { }
}

public class ShowToUserException : Exception
{
    public ShowToUserException(string? message) : base(message) { }
}

public class PeerNotOnlineException : Exception
{
    public PeerNotOnlineException(string? message = null) : base(message) { }
}

public class RemoteCanceledFundingException : Exception
{
    public RemoteCanceledFundingException(string? message = null) : base(message) { }
}

public class NotEnoughRoomInUtxosForFeesException : Exception
{
    public NotEnoughRoomInUtxosForFeesException() : base("Not enough room in the UTXOs to cover the fees") { }
}

public class NotEnoughBalanceInWalletException : Exception
{
    public NotEnoughBalanceInWalletException(string? message = null) : base(message) { }
}

public class BumpingException : Exception
{
    public BumpingException(string? message = null) : base(message) { }
}
