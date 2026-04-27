using FluentAssertions;
using FluentValidation.TestHelper;
using Strg.Application.Features.Files.Download;
using Xunit;

namespace Strg.Application.Tests.Features.Files.Download;

/// <summary>
/// Pins the syntactic-validation contract for <see cref="DownloadFileCommand"/>. Every rule
/// in <see cref="DownloadFileValidator"/> is exercised at the validator boundary so a future
/// "tidy up the rules" refactor cannot loosen them silently. Semantic rules
/// (drive/file existence, range satisfiability against actual size) live in the resolver and
/// are covered by <c>FileDownloadResolverTests</c>.
/// </summary>
public sealed class DownloadFileValidatorTests
{
    private readonly DownloadFileValidator _validator = new();

    [Fact]
    public void DriveId_Empty_FailsValidation()
    {
        var command = new DownloadFileCommand(Guid.Empty, Guid.NewGuid(), Range: null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(c => c.DriveId);
    }

    [Fact]
    public void FileId_Empty_FailsValidation()
    {
        var command = new DownloadFileCommand(Guid.NewGuid(), Guid.Empty, Range: null);
        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(c => c.FileId);
    }

    [Fact]
    public void HappyPath_NoRange_Passes()
    {
        var command = new DownloadFileCommand(Guid.NewGuid(), Guid.NewGuid(), Range: null);
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void HappyPath_BoundedRange_Passes()
    {
        var command = new DownloadFileCommand(Guid.NewGuid(), Guid.NewGuid(), new DownloadRange(0, 99));
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void HappyPath_OpenEndedRange_Passes()
    {
        var command = new DownloadFileCommand(Guid.NewGuid(), Guid.NewGuid(), new DownloadRange(100, null));
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void HappyPath_SuffixRange_Passes()
    {
        var command = new DownloadFileCommand(Guid.NewGuid(), Guid.NewGuid(), new DownloadRange(null, 200));
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Range_NegativeFrom_FailsValidation()
    {
        var command = new DownloadFileCommand(Guid.NewGuid(), Guid.NewGuid(), new DownloadRange(-1, 99));
        var result = _validator.TestValidate(command);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Range_NegativeTo_FailsValidation()
    {
        var command = new DownloadFileCommand(Guid.NewGuid(), Guid.NewGuid(), new DownloadRange(0, -1));
        var result = _validator.TestValidate(command);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Range_FromGreaterThanTo_FailsValidation()
    {
        var command = new DownloadFileCommand(Guid.NewGuid(), Guid.NewGuid(), new DownloadRange(100, 50));
        var result = _validator.TestValidate(command);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Range_BothNull_FailsValidation()
    {
        var command = new DownloadFileCommand(Guid.NewGuid(), Guid.NewGuid(), new DownloadRange(null, null));
        var result = _validator.TestValidate(command);
        result.IsValid.Should().BeFalse();
    }
}
