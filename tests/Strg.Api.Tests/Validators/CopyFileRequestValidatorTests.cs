using FluentValidation.TestHelper;
using Strg.Api.Endpoints;
using Strg.Api.Validators;
using Xunit;

namespace Strg.Api.Tests.Validators;

/// <summary>
/// STRG-085 — unit tests for <see cref="CopyFileRequestValidator"/>. Rules are isomorphic to
/// MoveFileRequestValidator's (the request shapes only differ in semantic, not in the
/// path-validation contract); the duplicated test methods serve as the regression pin in case
/// future evolution decouples the two validators (e.g. cross-drive copy adds a CopyFile-specific
/// constraint that move doesn't have).
/// </summary>
public sealed class CopyFileRequestValidatorTests
{
    private readonly CopyFileRequestValidator _validator = new();

    [Fact]
    public void EmptyTargetPath_FailsWith_RequiredMessage()
    {
        var result = _validator.TestValidate(new CopyFileRequest(string.Empty, null));

        result.ShouldHaveValidationErrorFor(r => r.TargetPath)
            .WithErrorMessage("TargetPath is required.");
    }

    [Fact]
    public void NullTargetPath_FailsWith_RequiredMessage()
    {
        var result = _validator.TestValidate(new CopyFileRequest(null!, null));

        result.ShouldHaveValidationErrorFor(r => r.TargetPath)
            .WithErrorMessage("TargetPath is required.");
    }

    [Fact]
    public void ValidTargetPath_Passes()
    {
        var result = _validator.TestValidate(new CopyFileRequest("a/b/c.txt", null));

        result.ShouldNotHaveValidationErrorFor(r => r.TargetPath);
    }

    [Fact]
    public void TraversalTargetPath_Fails()
    {
        var result = _validator.TestValidate(new CopyFileRequest("../../etc/passwd", null));

        result.ShouldHaveValidationErrorFor(r => r.TargetPath)
            .WithErrorMessage("TargetPath must not contain '..'.");
    }

    [Fact]
    public void TargetPathExceeding4096Chars_Fails()
    {
        var longPath = new string('a', 4097);

        var result = _validator.TestValidate(new CopyFileRequest(longPath, null));

        result.ShouldHaveValidationErrorFor(r => r.TargetPath);
    }

    [Fact]
    public void TargetPathAtMaximumLength_Passes()
    {
        var maxPath = new string('a', 4096);

        var result = _validator.TestValidate(new CopyFileRequest(maxPath, null));

        result.ShouldNotHaveValidationErrorFor(r => r.TargetPath);
    }
}
