using MediatR;

namespace DeChimex.Application.Common.Messaging;

/// <summary>A command that mutates state and returns a response.</summary>
public interface ICommand<out TResponse> : IRequest<TResponse> { }

/// <summary>A command that mutates state without a payload response.</summary>
public interface ICommand : IRequest { }

/// <summary>A read-only query.</summary>
public interface IQuery<out TResponse> : IRequest<TResponse> { }

/// <summary>Handler for a command with a response.</summary>
public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse> { }

/// <summary>Handler for a query.</summary>
public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse> { }
