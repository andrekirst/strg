using FluentValidation.TestHelper;
using Strg.Api.Endpoints;
using Strg.Api.Validators;
using Xunit;

namespace Strg.Api.Tests.Validators;

/// <summary>
/// STRG-085 — unit tests for <see cref="MoveFileRequestValidator"/>. Mirrors the
/// CreateFolderRequestValidatorTests shape since the rules are isomorphic — empty-path,
/// length cap, traversal token. Per-test files are kept separate (not parameterized across
/// validators) because the property name on each request type is distinct (<c>Path</c> vs
/// <c>TargetPath</c>) and the wire-shape contract on the validator (PropertyName) is what the
/// endpoint filter camel-cases into the RFC 7807 errors-dictionary key.
/// </summary>
public sealed class MoveFileRequestValidatorTests
{
    private readonly MoveFileRequestValidator _validator = new();

    [Fact]
    public void EmptyTargetPath_FailsWith_RequiredMessage()
    {
        var result = _validator.TestValidate(new MoveFileRequest(string.Empty, null));

        result.ShouldHaveValidationErrorFor(r => r.TargetPath)
            .WithErrorMessage("TargetPath is required.");
    }

    [Fact]
    public void NullTargetPath_FailsWith_RequiredMessage()
    {
        var result = _validator.TestValidate(new MoveFileRequest(null!, null));

        result.ShouldHaveValidationErrorFor(r => r.TargetPath)
            .WithErrorMessage("TargetPath is required.");
    }

    [Fact]
    public void ValidTargetPath_Passes()
    {
        var result = _validator.TestValidate(new MoveFileRequest("a/b/c.txt", null));

        result.ShouldNotHaveValidationErrorFor(r => r.TargetPath);
    }

    [Fact]
    public void ValidTargetPath_WithCrossDriveId_Passes()
    {
        var result = _validator.TestValidate(new MoveFileRequest("a/b/c.txt", Guid.NewGuid()));

        result.ShouldNotHaveValidationErrorFor(r => r.TargetPath);
    }

    [Fact]
    public void TraversalTargetPath_Fails()
    {
        var result = _validator.TestValidate(new MoveFileRequest("../../etc/passwd", null));

        result.ShouldHaveValidationErrorFor(r => r.TargetPath)
            .WithErrorMessage("TargetPath must not contain '..'.");
    }

    [Fact]
    public void TargetPathExceeding4096Chars_Fails()
    {
        var longPath = new string('a', 4097);

        var result = _validator.TestValidate(new MoveFileRequest(longPath, null));

        result.ShouldHaveValidationErrorFor(r => r.TargetPath);
    }

    [Fact]
    public void TargetPathAtMaximumLength_Passes()
    {
        var maxPath = new string('a', 4096);

        var result = _validator.TestValidate(new MoveFileRequest(maxPath, null));

        result.ShouldNotHaveValidationErrorFor(r => r.TargetPath);
    }
}
