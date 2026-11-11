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
            // STEP-01: Return propertyPath early when propertyPath is blank.
            if (string.IsNullOrEmpty(propertyPath))
            {
                return propertyPath;
            }

            // STEP-02: Prepare segments through propertyPath.Split.
            var segments = propertyPath.Split('.');
            // STEP-03: Repeat while i < segments.Length.
            for (var i = 0; i < segments.Length; i++)
            {
                // STEP-04: Prepare segment, indexStart through segment.IndexOf, name and index.
                var segment = segments[i];
                var indexStart = segment.IndexOf('[');
                var name = indexStart < 0 ? segment : segment[..indexStart];
                var index = indexStart < 0 ? string.Empty : segment[indexStart..];
                // STEP-05: Set segments[i].
                segments[i] = JsonNamingPolicy.CamelCase.ConvertName(name) + index;
            }

            // STEP-06: Return the result of string.Join.
            return string.Join('.', segments);
        }
    }
}
