namespace NodeGuard.Helpers;

public class NoUTXOsAvailableException: Exception {}

public class UTXOsNoLongerValidException: Exception {}

public class NBXplorerNotFullySyncedException: Exception
{
   public NBXplorerNotFullySyncedException(): base("Error, nbxplorer not fully synched") {}
}

public class ShowToUserException : Exception
{
   public ShowToUserException(string? message): base(message) {}
}

public class PeerNotOnlineException : Exception
{
   public PeerNotOnlineException(string? message = null): base(message) {}
}

public class RemoteCanceledFundingException : Exception
{
   public RemoteCanceledFundingException(string? message = null): base(message) {}
}

public class NotEnoughRoomInUtxosForFeesException : Exception
{
   public NotEnoughRoomInUtxosForFeesException(): base("Not enough room in the UTXOs to cover the fees") {}
}

public class NotEnoughBalanceInWalletException : Exception
{
   public NotEnoughBalanceInWalletException(string? message = null): base(message) {}
}
