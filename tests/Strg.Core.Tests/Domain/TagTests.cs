using FluentAssertions;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Core.Tests.Domain;

public sealed class TagTests
{
    private static Tag NewTag(string key = "project", string value = "acme") => new()
    {
        FileId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        Key = key,
        Value = value,
    };

    [Fact]
    public void Key_IsNormalizedToLowercase_OnInit()
    {
        var tag = NewTag(key: "Project");

        tag.Key.Should().Be("project");
    }

    [Fact]
    public void Key_FromMixedCaseInput_StoresLowercase()
    {
        var tag = NewTag(key: "MIXED.case-Tag");

        tag.Key.Should().Be("mixed.case-tag");
    }

    [Fact]
    public void Value_IsMutable()
    {
        var tag = NewTag(value: "first");

        tag.Value = "second";

        tag.Value.Should().Be("second");
    }

    [Fact]
    public void ValueType_DefaultsToString()
    {
        var tag = NewTag();

        tag.ValueType.Should().Be(TagValueType.String);
    }

    [Fact]
    public void TwoUsersOnSameFile_HaveIndependentKeys()
    {
        var fileId = Guid.NewGuid();
        var tagA = new Tag
        {
            FileId = fileId,
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Key = "project",
            Value = "acme",
        };
        var tagB = new Tag
        {
            FileId = fileId,
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Key = "project",
            Value = "globex",
        };

        tagA.UserId.Should().NotBe(tagB.UserId);
        tagA.Key.Should().Be(tagB.Key);
    }
}
