/*
 * NodeGuard
 * Copyright (C) 2023  ClovrLabs
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
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 *
 */

using System.Globalization;
using System.Text.RegularExpressions;
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

        var environmentVariableMin = Environment.GetEnvironmentVariable("MINIMUM_WITHDRAWAL_BTC_AMOUNT");
        var environmentVariableMax = Environment.GetEnvironmentVariable("MAXIMUM_WITHDRAWAL_BTC_AMOUNT");
        decimal minimum, maximum;
        if (environmentVariableMin == null) minimum = 0.0m;
        else minimum = decimal.Parse(environmentVariableMin, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        if (environmentVariableMax == null) maximum = 21_000_000;
        else maximum = decimal.Parse(environmentVariableMax, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        if (amount < minimum && !isAmountDisabled)
        {
            obj.Status = ValidationStatus.Error;
            obj.ErrorText = $"Error, the minimum amount to withdraw is at least {minimum} BTC";
        }

        if (amount > maximum && !isAmountDisabled)
        {
            obj.Status = ValidationStatus.Error;
            obj.ErrorText = $"Error, the maximum amount to withdraw is {maximum} BTC";
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

    /// <summary>
    /// Validated a xpub expect header and lenght size
    /// </summary>
    /// <param name="xpub"></param>
    public static bool ValidateXPUB(string xpub)
    {
        var regex = new Regex("^([xyYzZtuUvV]pub[1-9A-HJ-NP-Za-km-z]{79,108})$");

        var result = false;

        if (!string.IsNullOrWhiteSpace(xpub))
            result = regex.IsMatch(xpub);

        return result;
    }
}