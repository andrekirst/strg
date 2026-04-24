using FluentValidation;

namespace Strg.Application.Features.Ping;

public sealed class PingValidator : AbstractValidator<PingCommand>
{
    public PingValidator()
    {
        RuleFor(c => c.Message).NotEmpty().MaximumLength(200);
    }
}
