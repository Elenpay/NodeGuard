namespace FundsManager.Helpers;

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