using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.WebApi.Common
{
    /// <summary>
    /// Turns MVC model-binding errors into the VALIDATION_FAILED <c>errors</c> of the API. Every one of them comes from the
    /// framework, in English (FluentValidation runs later, in the use case), so the messages are replaced with Vietnamese
    /// ones (coding_rule.md, Errors) and the keys use the API field names.
    /// </summary>
    public static class ModelBindingErrors
    {
        public const string BodyKey = "body";
        public const string MissingBodyMessage = "Nội dung JSON là bắt buộc và không được là null.";
        public const string MalformedBodyMessage = "Nội dung JSON không hợp lệ.";
        public const string InvalidValueMessage = "Giá trị không hợp lệ.";

        /// <summary>
        /// Groups the invalid entries. An error on the whole body (key "", "$" or a body parameter name) goes under
        /// <see cref="BodyKey"/>; a field error keeps the field path without the "$." prefix of JSON paths.
        /// </summary>
        public static Dictionary<string, string[]> From(
            ModelStateDictionary modelState,
            IReadOnlyCollection<string> bodyParameterNames)
        {
            var errors = new Dictionary<string, List<string>>();
            foreach (var (key, entry) in modelState)
            {
                if (entry.Errors.Count == 0)
                {
                    continue;
                }

                var isBody = key.Length == 0 || key == "$" ||
                    bodyParameterNames.Contains(key, StringComparer.OrdinalIgnoreCase);
                var field = isBody
                    ? BodyKey
                    : ValidationErrorKeys.ToCamelCase(key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key);
                // MVC reports a JSON syntax error at the root under "$", and an empty or null body under "".
                var malformed = key == "$" || entry.Errors.Any(error => error.Exception is JsonException);
                var message = !isBody ? InvalidValueMessage : malformed ? MalformedBodyMessage : MissingBodyMessage;

                if (!errors.TryGetValue(field, out var messages))
                {
                    errors[field] = messages = [];
                }

                if (!messages.Contains(message))
                {
                    messages.Add(message);
                }
            }

            return errors.ToDictionary(item => item.Key, item => item.Value.ToArray());
        }
    }
}
