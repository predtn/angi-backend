using System.Text.Json;

namespace ANGI.WebApi.Common
{
    /// <summary>Builds the field keys of VALIDATION_FAILED <c>errors</c> in the camelCase of the JSON body.</summary>
    public static class ValidationErrorKeys
    {
        /// <summary>
        /// Converts every segment of a property path, not only the first letter of the whole path:
        /// <c>Answers[0].ScaleValue</c> becomes <c>answers[0].scaleValue</c>.
        /// </summary>
        public static string ToCamelCase(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath))
            {
                return propertyPath;
            }

            var segments = propertyPath.Split('.');
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var indexStart = segment.IndexOf('[');
                var name = indexStart < 0 ? segment : segment[..indexStart];
                var index = indexStart < 0 ? string.Empty : segment[indexStart..];
                segments[i] = JsonNamingPolicy.CamelCase.ConvertName(name) + index;
            }

            return string.Join('.', segments);
        }
    }
}
