namespace Jio.Core.Commands;

public interface ICommandHandler<TCommand> where TCommand : class
{
    Task<int> ExecuteAsync(TCommand command, CancellationToken cancellationToken = default);
}