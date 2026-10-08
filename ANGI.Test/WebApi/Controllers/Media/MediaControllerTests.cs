using ANGI.Application.Common.Interfaces.UseCases.Media;
using ANGI.Application.DTOs.Media;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Media;
using ANGI.WebApi.Models.Media;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.Test.WebApi.Controllers.Media;

public sealed class MediaControllerTests
{
    // TEST-01: Return the MEDIA-01 payload in ApiResponse with HTTP 201.
    /// <summary>Verifies that the media controller remains a thin successful HTTP boundary.</summary>
    [Fact]
    public async Task Upload_WithValidForm_ShouldReturnCreatedApiResponse()
    {
        var expected = new MediaFileDto
        {
            Id = 91,
            Url = "https://cdn.test/photo.jpg",
            MimeType = "image/jpeg",
            SizeBytes = 3,
            Purpose = "restaurant"
        };
        var useCase = new Mock<IUploadMediaUseCase>();
        useCase.Setup(x => x.ExecuteAsync(It.IsAny<UploadMediaRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = new MediaController(useCase.Object);
        var bytes = new byte[] { 1, 2, 3 };
        var form = new UploadMediaForm
        {
            File = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "photo.jpg")
            {
                Headers = new HeaderDictionary(),
                ContentType = "image/jpeg"
            },
            Purpose = "restaurant"
        };

        var action = await controller.Upload(form, CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        var response = result.Value.Should().BeOfType<ApiResponse<MediaFileDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        useCase.Verify(x => x.ExecuteAsync(
            It.Is<UploadMediaRequestDto>(request =>
                request.FileName == "photo.jpg" &&
                request.MimeType == "image/jpeg" &&
                request.SizeBytes == 3 &&
                request.Purpose == "restaurant"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
