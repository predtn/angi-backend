using System.Text.Json;
using ANGI.WebApi.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ANGI.Test.WebApi.Common
{
    public sealed class ModelBindingErrorsTests
    {
        private static readonly string[] BodyParameters = ["request"];

        // TEST-01: Put every whole-body error under "body" with one Vietnamese message.
        /// <summary>Verifies the keys MVC uses for a missing or null body collapse into one entry.</summary>
        [Fact]
        public void From_WithMissingBody_ShouldReportBodyOnce()
        {
            var modelState = new ModelStateDictionary();
            modelState.AddModelError(string.Empty, "A non-empty request body is required.");
            modelState.AddModelError("request", "The request field is required.");

            var errors = ModelBindingErrors.From(modelState, BodyParameters);

            errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["body"] = [ModelBindingErrors.MissingBodyMessage]
            });
        }

        // TEST-02: Report malformed JSON on the body, not as a missing body.
        /// <summary>Verifies a JsonException on the root marks the body as malformed.</summary>
        [Fact]
        public void From_WithMalformedJson_ShouldReportMalformedBody()
        {
            var modelState = new ModelStateDictionary();
            modelState.AddModelError("$", new JsonException("'}' is invalid"), new EmptyModelMetadataProvider().GetMetadataForType(typeof(object)));

            var errors = ModelBindingErrors.From(modelState, BodyParameters);

            errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["body"] = [ModelBindingErrors.MalformedBodyMessage]
            });
        }

        // TEST-03: Key a field error by the API field name, without the JSON path prefix, in Vietnamese.
        /// <summary>Verifies JSON paths and query names map to camelCase field keys.</summary>
        [Fact]
        public void From_WithFieldErrors_ShouldUseFieldNamesAndVietnamese()
        {
            var modelState = new ModelStateDictionary();
            modelState.AddModelError("$.latitude", "The JSON value could not be converted to System.Decimal.");
            modelState.AddModelError("$.businessHours[0].dayOfWeek", "The JSON value could not be converted.");
            modelState.AddModelError("Page", "The value 'abc' is not valid for Page.");

            var errors = ModelBindingErrors.From(modelState, BodyParameters);

            errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["latitude"] = [ModelBindingErrors.InvalidValueMessage],
                ["businessHours[0].dayOfWeek"] = [ModelBindingErrors.InvalidValueMessage],
                ["page"] = [ModelBindingErrors.InvalidValueMessage]
            });
        }
    }
}
