using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Drives.GetDefault;

/// <summary>
/// Returns the calling user's effective default drive: the drive referenced by their
/// <see cref="UserDriveDefault"/> row when one exists, otherwise the tenant's
/// <see cref="Drive.IsDefault"/> drive, otherwise <see langword="null"/>. Null-return is the
/// documented contract — callers (the inbox feature) decide what to do when no default exists.
/// </summary>
public sealed record GetDefaultDriveQuery() : IQuery<Drive?>;
