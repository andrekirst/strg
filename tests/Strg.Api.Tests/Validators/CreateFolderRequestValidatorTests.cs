using FluentAssertions;
using FluentValidation.TestHelper;
using Strg.Api.Endpoints;
using Strg.Api.Validators;
using Xunit;

namespace Strg.Api.Tests.Validators;

/// <summary>
/// STRG-085 — unit tests for <see cref="CreateFolderRequestValidator"/>. Each rule is exercised
/// in isolation; integration coverage of the wire-level RFC 7807 envelope lives in
/// <c>Strg.Integration.Tests</c>.
/// </summary>
public sealed class CreateFolderRequestValidatorTests
{
    private readonly CreateFolderRequestValidator _validator = new();

    [Fact]
    public void EmptyPath_FailsWith_RequiredMessage()
    {
        var result = _validator.TestValidate(new CreateFolderRequest(string.Empty));

        result.ShouldHaveValidationErrorFor(r => r.Path)
            .WithErrorMessage("Path is required.");
    }

    [Fact]
    public void NullPath_FailsWith_RequiredMessage()
    {
        var result = _validator.TestValidate(new CreateFolderRequest(null!));

        result.ShouldHaveValidationErrorFor(r => r.Path)
            .WithErrorMessage("Path is required.");
    }

    [Fact]
    public void ValidPath_Passes()
    {
        var result = _validator.TestValidate(new CreateFolderRequest("a/b/c"));

        result.ShouldNotHaveValidationErrorFor(r => r.Path);
    }

    [Fact]
    public void TraversalPath_FailsWith_DotDotMessage()
    {
        var result = _validator.TestValidate(new CreateFolderRequest("../etc/passwd"));

        result.ShouldHaveValidationErrorFor(r => r.Path)
            .WithErrorMessage("Path must not contain '..'.");
    }

    [Fact]
    public void EmbeddedTraversal_AnywhereInPath_Fails()
    {
        // Defence-in-depth: traversal segments embedded after a legitimate prefix must still be
        // rejected. StoragePath.Parse handles this in the handler, but the request-body validator
        // is the front-door guard, so it MUST also reject mid-path traversal.
        var result = _validator.TestValidate(new CreateFolderRequest("a/b/../etc"));

        result.ShouldHaveValidationErrorFor(r => r.Path)
            .WithErrorMessage("Path must not contain '..'.");
    }

    [Fact]
    public void PathExceeding4096Chars_Fails()
    {
        var longPath = new string('a', 4097);

        var result = _validator.TestValidate(new CreateFolderRequest(longPath));

        result.ShouldHaveValidationErrorFor(r => r.Path);
    }

    [Fact]
    public void PathAtMaximumLength_Passes()
    {
        var maxPath = new string('a', 4096);

        var result = _validator.TestValidate(new CreateFolderRequest(maxPath));

        result.ShouldNotHaveValidationErrorFor(r => r.Path);
    }

    [Fact]
    public void Validator_ProducesPropertyName_ForFilter_CamelCasing()
    {
        // The endpoint filter camel-cases ValidationFailure.PropertyName for the wire-level errors
        // dictionary key. Pin that the validator emits the C# property name so the camel-casing
        // step has a deterministic input.
        var result = _validator.Validate(new CreateFolderRequest(string.Empty));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        result.Errors[0].PropertyName.Should().Be("Path");
    }
}
