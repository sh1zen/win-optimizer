using System;

namespace WinOptimizer.Core.Features.Common.Models;

public class OperationResult
{
    public bool Success { get; }
    public string? ErrorMessage { get; }
    public Exception? Exception { get; }

    private OperationResult(bool success, string? errorMessage = null, Exception? exception = null)
    {
        Success = success;
        ErrorMessage = errorMessage;
        Exception = exception;
    }

    public static OperationResult Succeeded()
    {
        return new OperationResult(success: true);
    }

    public static OperationResult Failed(string message)
    {
        return new OperationResult(success: false, errorMessage: message);
    }

    public static OperationResult Failed(string message, Exception exception)
    {
        return new OperationResult(success: false, errorMessage: message, exception: exception);
    }
}

public class OperationResult<T>
{
    public bool Success { get; }
    public T? Result { get; }
    public string? ErrorMessage { get; }
    public Exception? Exception { get; }
    public bool RequiresConfirmation { get; }
    public bool IsCancelled { get; }
    public bool IsDeferred { get; }
    public string? InfoMessage { get; }

    private OperationResult(
        bool success,
        T? result = default,
        string? errorMessage = null,
        Exception? exception = null,
        bool requiresConfirmation = false,
        string? infoMessage = null,
        bool isCancelled = false,
        bool isDeferred = false)
    {
        Success = success;
        Result = result;
        ErrorMessage = errorMessage;
        Exception = exception;
        RequiresConfirmation = requiresConfirmation;
        InfoMessage = infoMessage;
        IsCancelled = isCancelled;
        IsDeferred = isDeferred;
    }

    public static OperationResult<T> Succeeded(T result)
    {
        return new OperationResult<T>(success: true, result: result);
    }

    public static OperationResult<T> Succeeded(T result, string infoMessage)
    {
        return new OperationResult<T>(success: true, result: result, infoMessage: infoMessage);
    }

    public static OperationResult<T> Failed(string message)
    {
        return new OperationResult<T>(success: false, errorMessage: message);
    }

    public static OperationResult<T> Failed(string message, Exception exception)
    {
        return new OperationResult<T>(success: false, errorMessage: message, exception: exception);
    }

    public static OperationResult<T> Cancelled(string message = "Operation was cancelled")
    {
        return new OperationResult<T>(success: false, errorMessage: message, isCancelled: true);
    }

    /// <summary>Retains completed outcomes when cancellation interrupts a batch.</summary>
    public static OperationResult<T> CancelledAfter(T result, string? infoMessage = null) =>
        new(success: false, result: result, errorMessage: "Operation was cancelled", infoMessage: infoMessage, isCancelled: true);

    public static OperationResult<T> ConfirmationRequired(string message)
    {
        return new OperationResult<T>(success: false, errorMessage: message, requiresConfirmation: true);
    }

    public static OperationResult<T> DeferredSuccess(T result, string infoMessage)
    {
        return new OperationResult<T>(success: true, result: result, infoMessage: infoMessage, isDeferred: true);
    }
}
