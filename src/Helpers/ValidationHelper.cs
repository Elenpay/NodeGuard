using System.Globalization;
using Blazorise;
using FundsManager.Data.Models;

namespace FundsManager.Helpers;

public static class ValidationHelper
{
    /// <summary>
    /// Validates that the name of the item introduced in the form is not null and is not only whitespaces.
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public static void ValidateName(ValidatorEventArgs obj)
    {
        obj.Status = ValidationStatus.Success;
        if (string.IsNullOrWhiteSpace((string)obj.Value))
        {
            obj.ErrorText = "The name cannot be empty";
            obj.Status = ValidationStatus.Error;
        }
    }
    
    public static void ValidateUsername(ValidatorEventArgs obj, List<ApplicationUser> users, string currentUserId)
    {
        obj.Status = ValidationStatus.Success;
        if (string.IsNullOrWhiteSpace((string)obj.Value))
        {
            obj.ErrorText = "The Username cannot be empty";
            obj.Status = ValidationStatus.Error;
            return;
        }
        if (users.Any(user => user.UserName.Equals(obj.Value) && currentUserId != user.Id))
        {
            obj.ErrorText = "A user with the same username already exists";
            obj.Status = ValidationStatus.Error;
            return;
        }
        
    }

    public static void ValidateChannelCapacity(ValidatorEventArgs obj)
    {
        obj.Status = ValidationStatus.Success;
        string environmentVariable = Environment.GetEnvironmentVariable("MINIMUM_CHANNEL_CAPACITY_SATS") ?? throw new InvalidOperationException();
        long minimum = long.Parse(environmentVariable, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (((long)obj.Value) < minimum)
        {
            obj.ErrorText = "The amount must be greater than 20.000";
            obj.Status = ValidationStatus.Error;
        }
    }

    public static void ValidateWithdrawalAmount(ValidatorEventArgs obj, Boolean isAmountDisabled)
    {
        var amount = (decimal)obj.Value;

        obj.Status = ValidationStatus.Success;

        var environmentVariable = Environment.GetEnvironmentVariable("MINIMUM_WITHDRAWAL_BTC_AMOUNT") ?? throw new InvalidOperationException();

        var minimum = decimal.Parse(environmentVariable, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (amount < minimum && !isAmountDisabled)
        {
            obj.Status = ValidationStatus.Error;
            obj.ErrorText = $"Error, the minimum amount to withdraw is at least {minimum} BTC";
        }
    }

    public static void ValidateXPUB(ValidatorEventArgs obj)
    {
        obj.Status = ValidationStatus.Success;
        if (string.IsNullOrWhiteSpace((string)obj.Value))
        {
            obj.ErrorText = "The XPUB field cannot be empty";
            obj.Status = ValidationStatus.Error;
        }

    }

    public static void ValidatePubKey(ValidatorEventArgs obj, List<Node> nodes, string currentPubKey)
    {
        obj.Status = ValidationStatus.Success;
        if (string.IsNullOrWhiteSpace((string)obj.Value))
        {
            obj.ErrorText = "The PubKey cannot be empty";
            obj.Status = ValidationStatus.Error;
            return;
        }
        if (nodes.Any(node => node.PubKey.Equals(obj.Value) && currentPubKey != node.PubKey))
        {
            obj.ErrorText = "A node with the same pubkey already exists";
            obj.Status = ValidationStatus.Error;
            return;
        }
    }

    public static void validateDestNode(ValidatorEventArgs obj, Node? destNode)
    {
        obj.Status = ValidationStatus.Success;
        if (destNode == null)
        {
            obj.ErrorText = "Select a proper destination node";
            obj.Status = ValidationStatus.Error;
        }
    }

    public static void validateMaxDecimal(ValidatorEventArgs obj)
    {
        obj.Status = ValidationStatus.Success;
        
        var environmentVariable = Environment.GetEnvironmentVariable("MAX_BTC") ?? throw new InvalidOperationException();
        var max = decimal.Parse(environmentVariable, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        
        if ((decimal)obj.Value > max)
        {
            obj.ErrorText = "Select a proper destination node";
            obj.Status = ValidationStatus.Error;
        }
    }
}