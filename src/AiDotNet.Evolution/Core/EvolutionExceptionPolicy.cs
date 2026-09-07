namespace AiDotNet.Evolution;

/// <summary>Classifies exceptions at caller-extension boundaries without suppressing fatal runtime failures.</summary>
internal static class EvolutionExceptionPolicy
{
    /// <summary>Returns whether an exception may safely be converted into an evolution failure result.</summary>
    internal static bool IsRecoverable(Exception exception)
    {
        if (exception is OutOfMemoryException) return false;
        if (exception is StackOverflowException) return false;
        if (exception is AccessViolationException) return false;
        if (exception is AppDomainUnloadedException) return false;
        if (exception is BadImageFormatException) return false;
        if (exception is CannotUnloadAppDomainException) return false;
        if (exception is ThreadAbortException) return false;

        if (exception is AggregateException aggregate)
        {
            foreach (Exception innerException in aggregate.InnerExceptions)
            {
                if (!IsRecoverable(innerException)) return false;
            }
            return true;
        }

        return exception.InnerException is null || IsRecoverable(exception.InnerException);
    }
}
