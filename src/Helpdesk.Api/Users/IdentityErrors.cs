using Microsoft.AspNetCore.Identity;

namespace Helpdesk.Api.Users;

public static class IdentityErrors
{
    /// <summary>
    /// Groups Identity's errors by the request field they belong to, for a validation problem response.
    /// </summary>
    public static Dictionary<string, string[]> ToValidationErrors(
        this IEnumerable<IdentityError> errors, string passwordField = "password")
    {
        var byField = new Dictionary<string, List<string>>();

        foreach (var error in errors)
        {
            var (field, message) = error.Code switch
            {
                "PasswordMismatch" => ("currentPassword", "The current password is incorrect."),
                // Identity reports a taken address twice, as a user name and as an email.
                "DuplicateEmail" or "DuplicateUserName" => ("email", "An account with this email already exists."),
                "InvalidEmail" or "InvalidUserName" => ("email", "Enter a valid email address."),
                var code when code.StartsWith("Password", StringComparison.Ordinal) => (passwordField, error.Description),
                _ => ("", error.Description),
            };

            if (!byField.TryGetValue(field, out var messages))
            {
                byField[field] = messages = [];
            }

            if (!messages.Contains(message))
            {
                messages.Add(message);
            }
        }

        return byField.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }
}
