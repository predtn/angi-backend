using ANGI.WebApi.Common;
using FluentAssertions;

namespace ANGI.UnitTests.WebApi.Common
{
    public sealed class ValidationErrorKeysTests
    {
        // TEST-01: Camel-case every segment of a property path, keeping indexes and JSON-path markers.
        /// <summary>Verifies flat, nested, indexed and already camel-cased paths.</summary>
        [Theory]
        [InlineData("Email", "email")]
        [InlineData("Answers", "answers")]
        [InlineData("Answers[0].ScaleValue", "answers[0].scaleValue")]
        [InlineData("Items[2].Tags[1].TagId", "items[2].tags[1].tagId")]
        [InlineData("$.answers[0].scaleValue", "$.answers[0].scaleValue")]
        [InlineData("request", "request")]
        [InlineData("", "")]
        public void ToCamelCase_ShouldConvertEverySegment(string propertyPath, string expected)
        {
            ValidationErrorKeys.ToCamelCase(propertyPath).Should().Be(expected);
        }
    }
}
