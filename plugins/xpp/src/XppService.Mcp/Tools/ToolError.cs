using System.Text.Json;
using Grpc.Core;

namespace Xpp.Service.Mcp.Tools;

/// <summary>
/// Uniform structured error for tool methods. A tool that lets an exception
/// escape gets the SDK's contentless "An error occurred invoking '&lt;tool&gt;'."
/// envelope — indistinguishable from a transient fault, so the rational agent
/// response is a blind retry that wastes turns on what is almost always a
/// deterministic failure. Catching and RETURNING this instead surfaces the
/// actual cause as normal tool output (the agent reads it and fixes the input).
///
/// Mirrors the structured shape the write tools already use
/// (<c>AuthoringTools.FormatBridgeFailure</c>): RpcException carries the
/// service/bridge status + detail; anything else carries the exception message
/// and type. Read/search/inspect tools route their bodies through
/// <see cref="From"/> so the whole tool surface fails legibly.
/// </summary>
internal static class ToolError
{
    public static string From(string tool, Exception ex)
    {
        if (ex is RpcException rx)
        {
            return JsonSerializer.Serialize(new
            {
                error = "tool_failed",
                tool,
                code = rx.StatusCode.ToString(),
                message = string.IsNullOrEmpty(rx.Status.Detail) ? rx.Message : rx.Status.Detail,
                hint = "Surfaced from XppService/bridge. The message names the cause; this is a "
                     + "deterministic failure, not a transient fault — fix the inputs rather than retrying.",
            });
        }
        return JsonSerializer.Serialize(new
        {
            error = "tool_failed",
            tool,
            code = "internal",
            message = ex.Message,
            exceptionType = ex.GetType().Name,
            hint = "Unhandled exception inside the tool. Deterministic — inspect the message/inputs "
                 + "rather than retrying.",
        });
    }
}
