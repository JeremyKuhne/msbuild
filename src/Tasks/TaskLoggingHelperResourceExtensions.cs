// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Resources;
using Microsoft.Build.Framework.Utilities;
using Microsoft.Build.Utilities;

#if DEBUG
using Microsoft.Build.Shared;
#endif

namespace Microsoft.Build.Tasks;

// Forward statically: inherited string/params instance methods take precedence over extension overloads.
internal static class TaskLoggingHelperResourceExtensions
{
    internal static string FormatResourceString(this TaskLoggingHelper log, StringResource resourceName, params object?[]? args)
    {
#if FEATURE_APPDOMAIN
        if (System.Runtime.Remoting.RemotingServices.IsTransparentProxy(log))
        {
            return log.FormatResourceString(resourceName.Name, args);
        }
#endif

        if (log is TaskLoggingHelperExtension extension)
        {
            return extension.FormatResourceString(resourceName, args);
        }

        if (log.GetType() == typeof(TaskLoggingHelper)
            && resourceName.Name is not null
            && log.TaskResources is { } resources
            && resourceName.GetString(resources, CultureInfo.CurrentUICulture) is string resourceString)
        {
            return log.FormatString(resourceString, args);
        }

        // Preserve custom virtual hooks and the standard missing-resource exceptions.
        return log.FormatResourceString(resourceName.Name, args);
    }

    internal static string GetResourceMessage(this TaskLoggingHelper log, StringResource resourceName)
        => log.GetType() == typeof(TaskLoggingHelper)
            || log is TaskLoggingHelperExtension { UsesDefaultResourceFormatting: true }
                ? log.FormatResourceString(resourceName, null)
                : log.GetResourceMessage(resourceName.Name);

    internal static void LogMessageFromResources(this TaskLoggingHelper log, StringResource messageResourceName, params object?[]? messageArgs)
    {
        Debug.Assert(messageArgs is null || messageArgs.Length == 0 || messageArgs[0] is not MessageImportance, "Did you call the wrong overload?");
        LogMessageFromResources(log, MessageImportance.Normal, messageResourceName, messageArgs);
    }

    internal static void LogMessageFromResources(this TaskLoggingHelper log, MessageImportance importance, StringResource messageResourceName, params object?[]? messageArgs)
    {
        ArgumentNullException.ThrowIfNull(messageResourceName.Name);

        if (!log.LogsMessagesOfImportance(importance))
        {
            return;
        }

        string message = log.GetResourceMessage(messageResourceName);
        log.LogMessage(importance, message, messageArgs);

#if DEBUG
        // Check the template without eagerly formatting the message arguments.
        Assumed.False(
            MessageParser.TryGetMSBuildCode(message, out string? code),
            $"This message contains an error code ({code}), yet it was logged as a regular message: {message}");
#endif
    }

    internal static void LogErrorFromResources(this TaskLoggingHelper log, StringResource messageResourceName, params object?[]? messageArgs)
    {
        LogErrorFromResources(log, null, null, null, null, 0, 0, 0, 0, messageResourceName, messageArgs);
    }

    internal static void LogErrorFromResources(
        this TaskLoggingHelper log,
        string? subcategoryResourceName,
        string? errorCode,
        string? helpKeyword,
        string? file,
        int lineNumber,
        int columnNumber,
        int endLineNumber,
        int endColumnNumber,
        StringResource messageResourceName,
        params object?[]? messageArgs)
    {
        ArgumentNullException.ThrowIfNull(messageResourceName.Name);
        string? subcategory = subcategoryResourceName is not null ? log.FormatResourceString(subcategoryResourceName) : null;
        string message = log.FormatResourceString(messageResourceName, messageArgs);

#if DEBUG
        Assumed.False(
            MessageParser.TryGetMSBuildCode(message, out string? code),
            $"Called {nameof(LogErrorFromResources)} instead of {nameof(LogErrorWithCodeFromResources)}, but message '{message}' does have an error code '{code}'");
#endif

        log.LogError(
            subcategory,
            errorCode,
            helpKeyword,
            file,
            lineNumber,
            columnNumber,
            endLineNumber,
            endColumnNumber,
            message);
    }

    internal static void LogErrorWithCodeFromResources(this TaskLoggingHelper log, StringResource messageResourceName, params object?[]? messageArgs)
    {
        LogErrorWithCodeFromResources(log, null, null, 0, 0, 0, 0, messageResourceName, messageArgs);
    }

    internal static void LogErrorWithCodeFromResources(
        this TaskLoggingHelper log,
        string? subcategoryResourceName,
        string? file,
        int lineNumber,
        int columnNumber,
        int endLineNumber,
        int endColumnNumber,
        StringResource messageResourceName,
        params object?[]? messageArgs)
    {
        ArgumentNullException.ThrowIfNull(messageResourceName.Name);
        string? subcategory = subcategoryResourceName is not null ? log.FormatResourceString(subcategoryResourceName) : null;
        string message = log.FormatResourceString(messageResourceName, messageArgs);

        if (MessageParser.TryParseAnyCode(message, out string? errorCode, out string? strippedMessage))
        {
            message = strippedMessage;
        }

        string? helpKeyword = log.HelpKeywordPrefix is not null ? log.HelpKeywordPrefix + messageResourceName.Name : null;
        log.LogError(
            subcategory,
            errorCode,
            helpKeyword,
            file,
            lineNumber,
            columnNumber,
            endLineNumber,
            endColumnNumber,
            message);
    }

    internal static void LogWarningFromResources(this TaskLoggingHelper log, StringResource messageResourceName, params object?[]? messageArgs)
    {
        LogWarningFromResources(log, null, null, null, null, 0, 0, 0, 0, messageResourceName, messageArgs);
    }

    internal static void LogWarningFromResources(
        this TaskLoggingHelper log,
        string? subcategoryResourceName,
        string? warningCode,
        string? helpKeyword,
        string? file,
        int lineNumber,
        int columnNumber,
        int endLineNumber,
        int endColumnNumber,
        StringResource messageResourceName,
        params object?[]? messageArgs)
    {
        ArgumentNullException.ThrowIfNull(messageResourceName.Name);
        string? subcategory = subcategoryResourceName is not null ? log.FormatResourceString(subcategoryResourceName) : null;
        string message = log.FormatResourceString(messageResourceName, messageArgs);

#if DEBUG
        Assumed.False(
            MessageParser.TryGetMSBuildCode(message, out string? code),
            $"Called {nameof(LogWarningFromResources)} instead of {nameof(LogWarningWithCodeFromResources)}, but message '{message}' does have an error code '{code}'");
#endif

        log.LogWarning(
            subcategory,
            warningCode,
            helpKeyword,
            file,
            lineNumber,
            columnNumber,
            endLineNumber,
            endColumnNumber,
            message);
    }

    internal static void LogWarningWithCodeFromResources(this TaskLoggingHelper log, StringResource messageResourceName, params object?[]? messageArgs)
    {
        LogWarningWithCodeFromResources(log, null, null, 0, 0, 0, 0, messageResourceName, messageArgs);
    }

    internal static void LogWarningWithCodeFromResources(
        this TaskLoggingHelper log,
        string? subcategoryResourceName,
        string? file,
        int lineNumber,
        int columnNumber,
        int endLineNumber,
        int endColumnNumber,
        StringResource messageResourceName,
        params object?[]? messageArgs)
    {
        ArgumentNullException.ThrowIfNull(messageResourceName.Name);
        string? subcategory = subcategoryResourceName is not null ? log.FormatResourceString(subcategoryResourceName) : null;
        string message = log.FormatResourceString(messageResourceName, messageArgs);

        if (MessageParser.TryParseAnyCode(message, out string? warningCode, out string? strippedMessage))
        {
            message = strippedMessage;
        }

        string? helpKeyword = log.HelpKeywordPrefix is not null ? log.HelpKeywordPrefix + messageResourceName.Name : null;
        log.LogWarning(
            subcategory,
            warningCode,
            helpKeyword,
            file,
            lineNumber,
            columnNumber,
            endLineNumber,
            endColumnNumber,
            message);
    }

    internal static void LogErrorWithCodeAndExceptionFromResources(
        this TaskLoggingHelper log,
        Exception exception,
        bool showStackTrace,
        bool showDetail,
        string? subcategory,
        string? file,
        int lineNumber,
        int columnNumber,
        int endLineNumber,
        int endColumnNumber,
        StringResource messageResourceName,
        params object?[]? messageArgs)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(messageResourceName.Name);

        if (exception is AggregateException aggregateException)
        {
            foreach (Exception innerException in aggregateException.Flatten().InnerExceptions)
            {
                LogErrorWithCodeAndExceptionFromResources(log, innerException, showStackTrace, showDetail, subcategory, file, lineNumber, columnNumber, endLineNumber, endColumnNumber, messageResourceName, messageArgs);
            }

            return;
        }

        string formattedResourceMessage = log.FormatResourceString(messageResourceName, messageArgs);
        MessageParser.TryGetAnyCode(formattedResourceMessage, out string? errorCode);
        string? helpKeyword = log.HelpKeywordPrefix is not null ? log.HelpKeywordPrefix + messageResourceName.Name : null;
        string message = GetFormattedExceptionDetails(exception, showStackTrace, showDetail);

        log.LogError(
            subcategory,
            errorCode,
            helpKeyword,
            file,
            lineNumber,
            columnNumber,
            endLineNumber,
            endColumnNumber,
            message);
    }

    internal static void LogErrorWithCodeAndExceptionFromResources(
        this TaskLoggingHelper log,
        Exception exception,
        bool showStackTrace,
        bool showDetail,
        StringResource messageResourceName,
        params object?[]? messageArgs)
    {
        LogErrorWithCodeAndExceptionFromResources(log, exception, showStackTrace, showDetail, null, null, 0, 0, 0, 0, messageResourceName, messageArgs);
    }

    internal static void LogErrorWithCodeAndExceptionFromResources(
        this TaskLoggingHelper log,
        Exception exception,
        bool showStackTrace,
        bool showDetail,
        string? file,
        StringResource messageResourceName,
        params object?[]? messageArgs)
    {
        LogErrorWithCodeAndExceptionFromResources(log, exception, showStackTrace, showDetail, null, file, 0, 0, 0, 0, messageResourceName, messageArgs);
    }

    // Utilities' formatter is private; mirror its exception details while using public logging for event metadata.
    private static string GetFormattedExceptionDetails(Exception exception, bool showStackTrace, bool showDetail)
    {
        bool diagnostics = Environment.GetEnvironmentVariable("MSBUILDDIAGNOSTICS") is not null;

        if (!showDetail && !diagnostics)
        {
            string message = exception.Message;
            if (showStackTrace)
            {
                message += Environment.NewLine + exception.StackTrace;
            }

            return message;
        }

        var builder = new StringBuilder(200);
        for (Exception? currentException = exception; currentException is not null; currentException = currentException.InnerException)
        {
            builder.Append(currentException.GetType().Name);
            builder.Append(": ");
            builder.AppendLine(currentException.Message);
            if (showStackTrace)
            {
                builder.AppendLine(currentException.StackTrace);
            }
        }

        return builder.ToString();
    }
}
