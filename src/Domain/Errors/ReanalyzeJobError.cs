using System;

namespace Emenda.Domain.Errors;

/// <summary>
/// Stable error codes returned by IReanalyzeJobService. The Service layer
/// maps these to gRPC StatusCodes. The BFF layer maps gRPC codes to HTTP.
/// </summary>
public static class ReanalyzeJobErrorCode
{
    public const string NotFound = "NotFound";
    public const string FailedPrecondition = "FailedPrecondition";
    public const string InvalidArgument = "InvalidArgument";
}

public class ReanalyzeJobException : Exception
{
    public string ErrorCode { get; }

    public ReanalyzeJobException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
