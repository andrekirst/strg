namespace Strg.Application.Abstractions;

/// <summary>
/// Marker for commands that must run inside an explicit DB transaction. TransactionBehavior
/// wraps the handler in IStrgDbContext.Database.BeginTransactionAsync. Most commands should
/// NOT implement this: the MassTransit EF Outbox already wraps SaveChangesAsync atomically, so
/// a single-save command already has the atomicity guarantee it needs. Reserve this marker for
/// commands that span multiple SaveChangesAsync calls that must commit together.
/// </summary>
public interface ITransactionalCommand;
