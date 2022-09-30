using Blazorise;

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
        if (obj.Value == null || ((string)obj.Value).Trim() == "")
        {
            obj.ErrorText = "The name cannot be empty";
            obj.Status = ValidationStatus.Error;
        }
    }
}