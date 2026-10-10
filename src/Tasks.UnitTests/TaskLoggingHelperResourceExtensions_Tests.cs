// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Resources;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Resources;
using Microsoft.Build.Tasks;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;
using ResourceLogging = Microsoft.Build.Tasks.TaskLoggingHelperResourceExtensions;

namespace Microsoft.Build.UnitTests;

public sealed class TaskLoggingHelperResourceExtensions_Tests(ITestOutputHelper output)
{
    private const string MessageResourceName = "Copy.CreatesDirectory";
    private const string ErrorResourceName = "General.InvalidArgument";
    private const string WarningResourceName = "Copy.Retrying";
    private const string HelpKeywordPrefix = "TEST.";
    private static readonly string s_resourceBaseName = $"{typeof(TaskLoggingHelperExtension).Assembly.GetName().Name}.Strings";
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void KnownIndexedSlotBypassesDynamicDispatcher()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        StringResourceManager manager = CreateIndexedManager(env, 1);
        ResourceManager oracle = CreatePlatformManager(env);
        RecordingResourceManager shared = TrackResources(env, new RecordingResourceManager("shared"));
        StringResource resource = new(MessageResourceName, 0, () => manager);
        var engine = new CapturingBuildEngine();
        var task = new LoggingTask { BuildEngine = engine };
        TaskLoggingHelper baseLog = new(task) { TaskResources = manager };
        TaskLoggingHelper extensionLog = new TaskLoggingHelperExtension(task, manager, shared, HelpKeywordPrefix);
        string template = oracle.GetString(MessageResourceName, CultureInfo.CurrentUICulture)!;

        string unformatted = ResourceLogging.GetResourceMessage(baseLog, resource);
        unformatted.ShouldBe(template);
        ResourceLogging.GetResourceMessage(extensionLog, resource).ShouldBeSameAs(unformatted);
        ResourceLogging.FormatResourceString(baseLog, resource, "directory")
            .ShouldBe(string.Format(CultureInfo.CurrentCulture, template, "directory"));
        ResourceLogging.FormatResourceString(extensionLog, resource, "directory")
            .ShouldBe(string.Format(CultureInfo.CurrentCulture, template, "directory"));
        shared.LookupCount.ShouldBe(0);
    }

    [Fact]
    public void FilteredMessagesDoNotLookupOrInitializeProvider()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        int dynamicLookups = 0;
        StringResourceManager manager = TrackResources(env, new StringResourceManager(
            s_resourceBaseName,
            typeof(TaskLoggingHelperExtension).Assembly,
            1,
            _ =>
            {
                dynamicLookups++;
                throw new InvalidOperationException("Filtered logging must not look up a resource.");
            }));
        var provider = new Lazy<ResourceManager>(
            static () => throw new InvalidOperationException("Filtered logging must not initialize its resource provider."));
        StringResource resource = new(MessageResourceName, 0, () => provider.Value);
        var argument = new CountingArgument("directory");
        var engine = new MockEngine(_output) { MinimumMessageImportance = MessageImportance.High };
        TaskLoggingHelper log = new(new LoggingTask { BuildEngine = engine }) { TaskResources = manager };

        ResourceLogging.LogMessageFromResources(log, MessageImportance.Low, resource, argument);
        ResourceLogging.LogMessageFromResources(log, resource, argument);

        dynamicLookups.ShouldBe(0);
        provider.IsValueCreated.ShouldBeFalse();
        argument.FormattingCalls.ShouldBe(0);
        engine.Messages.ShouldBe(0);
        engine.MessageEvents.ShouldBeEmpty();
    }

#if FEATURE_APPDOMAIN
    [Fact]
    public void RemoteLoggingUsesResourceNamesWithoutMarshallingDescriptors()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        AppDomain domain = AppDomain.CreateDomain(
            nameof(RemoteLoggingUsesResourceNamesWithoutMarshallingDescriptors),
            null,
            AppDomain.CurrentDomain.SetupInformation);
        env.WithTransientTestState(new AppDomainState(domain));
        var factory = (RemoteLoggerFactory)domain.CreateInstanceFromAndUnwrap(
            typeof(RemoteLoggerFactory).Assembly.Location,
            typeof(RemoteLoggerFactory).FullName);
        TaskLoggingHelper log = factory.CreateLogger();
        System.Runtime.Remoting.RemotingServices.IsTransparentProxy(log).ShouldBeTrue();
        StringResource resource = new(MessageResourceName, 0,
            static () => throw new InvalidOperationException("Remote logging must not invoke the local resource provider."));
        ResourceManager oracle = CreatePlatformManager(env);
        string template = oracle.GetString(MessageResourceName, CultureInfo.CurrentUICulture)!;

        ResourceLogging.GetResourceMessage(log, resource).ShouldBe(template);
        ResourceLogging.FormatResourceString(log, resource, "directory")
            .ShouldBe(string.Format(CultureInfo.CurrentCulture, template, "directory"));
    }

    private sealed class AppDomainState(AppDomain domain) : TransientTestState
    {
        public override void Revert() => AppDomain.Unload(domain);
    }

    public sealed class RemoteLoggerFactory : MarshalByRefObject
    {
        public TaskLoggingHelper CreateLogger()
        {
            var manager = new ResourceManager(s_resourceBaseName, typeof(TaskLoggingHelperExtension).Assembly);
            return new TaskLoggingHelperExtension(new LoggingTask(), manager, manager, HelpKeywordPrefix);
        }
    }
#endif

    [Fact]
    public void MessageArgumentsAreDeferredUntilMessageIsRead()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        StringResourceManager manager = CreateIndexedManager(env, 1);
        ResourceManager oracle = CreatePlatformManager(env);
        StringResource resource = new(MessageResourceName, 0, () => manager);
        var engine = new CapturingBuildEngine();
        TaskLoggingHelper log = new(new LoggingTask { BuildEngine = engine }) { TaskResources = manager };
        var argument = new CountingArgument("directory");

        ResourceLogging.LogMessageFromResources(log, MessageImportance.Low, resource, argument);

        BuildMessageEventArgs message = engine.Messages.ShouldHaveSingleItem();
        message.Importance.ShouldBe(MessageImportance.Low);
        int validationCalls = 0;
#if DEBUG
        // Public LogMessage validates composite formatting eagerly in Debug.
        validationCalls = 1;
#endif
        argument.FormattingCalls.ShouldBe(validationCalls);
        string template = oracle.GetString(MessageResourceName, CultureInfo.CurrentUICulture)!;
        string expected = string.Format(CultureInfo.CurrentCulture, template, "directory");

        message.Message.ShouldBe(expected);
        argument.FormattingCalls.ShouldBe(validationCalls + 1);
        message.Message.ShouldBe(expected);
        argument.FormattingCalls.ShouldBe(validationCalls + 1);
    }

    [Fact]
    public void DiagnosticEventsMatchStringResourceLogging()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        StringResourceManager manager = CreateIndexedManager(env, 2);
        ResourceManager oracle = CreatePlatformManager(env);
        RecordingResourceManager shared = TrackResources(env, new RecordingResourceManager(null));
        StringResource errorResource = new(ErrorResourceName, 0, () => manager);
        StringResource warningResource = new(WarningResourceName, 1, () => manager);
        var typedEngine = new CapturingBuildEngine();
        var stringEngine = new CapturingBuildEngine();
        TaskLoggingHelper typedLog = new TaskLoggingHelperExtension(
            new LoggingTask { BuildEngine = typedEngine }, manager, shared, HelpKeywordPrefix);
        TaskLoggingHelper stringLog = new TaskLoggingHelperExtension(
            new LoggingTask { BuildEngine = stringEngine }, oracle, shared, HelpKeywordPrefix);
        const string file = "input.proj";
        object?[] warningArguments = ["source", "destination", 2, 25, "detail", string.Empty];

        typedLog.HasLoggedErrors.ShouldBeFalse();
        stringLog.HasLoggedErrors.ShouldBeFalse();
        ResourceLogging.LogWarningWithCodeFromResources(typedLog, null, file, 13, 17, 19, 23, warningResource, warningArguments);
        stringLog.LogWarningWithCodeFromResources(null, file, 13, 17, 19, 23, WarningResourceName, warningArguments);
        typedLog.HasLoggedErrors.ShouldBeFalse();
        stringLog.HasLoggedErrors.ShouldBeFalse();

        BuildWarningEventArgs warning = typedEngine.Warnings.ShouldHaveSingleItem();
        BuildWarningEventArgs expectedWarning = stringEngine.Warnings.ShouldHaveSingleItem();
        warning.Code.ShouldBe("MSB3026");
        warning.Code.ShouldBe(expectedWarning.Code);
        warning.HelpKeyword.ShouldBe(HelpKeywordPrefix + WarningResourceName);
        warning.HelpKeyword.ShouldBe(expectedWarning.HelpKeyword);
        warning.File.ShouldBe(expectedWarning.File);
        warning.LineNumber.ShouldBe(expectedWarning.LineNumber);
        warning.ColumnNumber.ShouldBe(expectedWarning.ColumnNumber);
        warning.EndLineNumber.ShouldBe(expectedWarning.EndLineNumber);
        warning.EndColumnNumber.ShouldBe(expectedWarning.EndColumnNumber);
        warning.Subcategory.ShouldBe(expectedWarning.Subcategory);
        warning.SenderName.ShouldBe(expectedWarning.SenderName);
        warning.Message.ShouldBe(expectedWarning.Message);

        ResourceLogging.LogErrorWithCodeFromResources(typedLog, null, file, 29, 31, 37, 41, errorResource, "bad argument");
        stringLog.LogErrorWithCodeFromResources(null, file, 29, 31, 37, 41, ErrorResourceName, "bad argument");

        BuildErrorEventArgs error = typedEngine.Errors.ShouldHaveSingleItem();
        BuildErrorEventArgs expectedError = stringEngine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("MSB3095");
        error.Code.ShouldBe(expectedError.Code);
        error.HelpKeyword.ShouldBe(HelpKeywordPrefix + ErrorResourceName);
        error.HelpKeyword.ShouldBe(expectedError.HelpKeyword);
        error.File.ShouldBe(expectedError.File);
        error.LineNumber.ShouldBe(expectedError.LineNumber);
        error.ColumnNumber.ShouldBe(expectedError.ColumnNumber);
        error.EndLineNumber.ShouldBe(expectedError.EndLineNumber);
        error.EndColumnNumber.ShouldBe(expectedError.EndColumnNumber);
        error.Subcategory.ShouldBe(expectedError.Subcategory);
        error.SenderName.ShouldBe(expectedError.SenderName);
        error.Message.ShouldBe(expectedError.Message);
        typedLog.HasLoggedErrors.ShouldBeTrue();
        typedLog.HasLoggedErrors.ShouldBe(stringLog.HasLoggedErrors);
        shared.LookupCount.ShouldBe(0);
    }

    [Fact]
    public void ConvenienceDiagnosticOverloadsKeepDescriptorNames()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        StringResourceManager manager = CreateIndexedManager(env, 3);
        ResourceManager oracle = CreatePlatformManager(env);
        StringResource messageResource = new(MessageResourceName, 0, () => manager);
        StringResource errorResource = new(ErrorResourceName, 1, () => manager);
        StringResource warningResource = new(WarningResourceName, 2, () => manager);
        var engine = new CapturingBuildEngine();
        TaskLoggingHelper log = new(new LoggingTask { BuildEngine = engine })
        {
            TaskResources = manager,
            HelpKeywordPrefix = HelpKeywordPrefix
        };
        object?[] warningArguments = ["source", "destination", 2, 25, "detail", string.Empty];
        string messageTemplate = oracle.GetString(MessageResourceName, CultureInfo.CurrentUICulture)!;
        string warningTemplate = oracle.GetString(WarningResourceName, CultureInfo.CurrentUICulture)!;
        string errorTemplate = oracle.GetString(ErrorResourceName, CultureInfo.CurrentUICulture)!;
        log.ExtractMessageCode(log.FormatString(warningTemplate, warningArguments), out string expectedWarning).ShouldBe("MSB3026");
        log.ExtractMessageCode(log.FormatString(errorTemplate, "bad argument"), out string expectedError).ShouldBe("MSB3095");

        ResourceLogging.LogWarningFromResources(log, messageResource, "warning directory");
        ResourceLogging.LogWarningWithCodeFromResources(log, warningResource, warningArguments);

        log.HasLoggedErrors.ShouldBeFalse();
        engine.Warnings.Count.ShouldBe(2);
        engine.Warnings[0].Code.ShouldBeNull();
        engine.Warnings[0].HelpKeyword.ShouldBeNull();
        engine.Warnings[0].Message.ShouldBe(log.FormatString(messageTemplate, "warning directory"));
        engine.Warnings[1].Code.ShouldBe("MSB3026");
        engine.Warnings[1].HelpKeyword.ShouldBe(HelpKeywordPrefix + WarningResourceName);
        engine.Warnings[1].Message.ShouldBe(expectedWarning);

        ResourceLogging.LogErrorFromResources(log, messageResource, "error directory");
        ResourceLogging.LogErrorWithCodeFromResources(log, errorResource, "bad argument");

        log.HasLoggedErrors.ShouldBeTrue();
        engine.Errors.Count.ShouldBe(2);
        engine.Errors[0].Code.ShouldBeNull();
        engine.Errors[0].HelpKeyword.ShouldBeNull();
        engine.Errors[0].Message.ShouldBe(log.FormatString(messageTemplate, "error directory"));
        engine.Errors[1].Code.ShouldBe("MSB3095");
        engine.Errors[1].HelpKeyword.ShouldBe(HelpKeywordPrefix + ErrorResourceName);
        engine.Errors[1].Message.ShouldBe(expectedError);
    }

    [Fact]
    public void BaseTypedFullFormCallsNeedNamedDescriptorBinding()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        int subcategoryLookups = 0;
        StringResourceManager manager = TrackResources(env, new StringResourceManager(
            s_resourceBaseName,
            typeof(TaskLoggingHelperExtension).Assembly,
            3,
            name =>
            {
                if (name != MessageResourceName)
                {
                    throw new InvalidOperationException("Only the string subcategory may use dynamic resource dispatch.");
                }

                subcategoryLookups++;
                return 0;
            }));
        ResourceManager oracle = CreatePlatformManager(env);
        StringResource errorResource = new(ErrorResourceName, 1, () => manager);
        StringResource warningResource = new(WarningResourceName, 2, () => manager);
        var engine = new CapturingBuildEngine();
        TaskLoggingHelper log = new(new LoggingTask { BuildEngine = engine })
        {
            TaskResources = manager,
            HelpKeywordPrefix = HelpKeywordPrefix
        };
        const string file = "input.proj";
        object?[] warningArguments = ["source", "destination", 2, 25, "detail", string.Empty];

        VerifyStringConvenienceBinding(
            () => log.LogErrorWithCodeFromResources(null, file, 13, 17, 19, 23, errorResource, "bad argument"));
        VerifyStringConvenienceBinding(
            () => log.LogWarningWithCodeFromResources(MessageResourceName, file, 29, 31, 37, 41, warningResource, warningArguments));
        subcategoryLookups.ShouldBe(0);
        engine.Errors.ShouldBeEmpty();
        engine.Warnings.ShouldBeEmpty();

        log.LogErrorWithCodeFromResources(
            null, file, 13, 17, 19, 23, messageResourceName: errorResource, messageArgs: ["bad argument"]);

        BuildErrorEventArgs error = engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("MSB3095");
        error.HelpKeyword.ShouldBe(HelpKeywordPrefix + ErrorResourceName);
        error.Subcategory.ShouldBeNull();
        error.File.ShouldBe(file);
        error.LineNumber.ShouldBe(13);
        error.ColumnNumber.ShouldBe(17);
        error.EndLineNumber.ShouldBe(19);
        error.EndColumnNumber.ShouldBe(23);
        string errorTemplate = oracle.GetString(ErrorResourceName, CultureInfo.CurrentUICulture)!;
        log.ExtractMessageCode(log.FormatString(errorTemplate, "bad argument"), out string expectedError).ShouldBe("MSB3095");
        error.Message.ShouldBe(expectedError);
        log.HasLoggedErrors.ShouldBeTrue();
        subcategoryLookups.ShouldBe(0);

        log.LogWarningWithCodeFromResources(
            MessageResourceName, file, 29, 31, 37, 41, messageResourceName: warningResource, messageArgs: warningArguments);

        BuildWarningEventArgs warning = engine.Warnings.ShouldHaveSingleItem();
        warning.Code.ShouldBe("MSB3026");
        warning.HelpKeyword.ShouldBe(HelpKeywordPrefix + WarningResourceName);
        warning.Subcategory.ShouldBe(oracle.GetString(MessageResourceName, CultureInfo.CurrentUICulture));
        warning.File.ShouldBe(file);
        warning.LineNumber.ShouldBe(29);
        warning.ColumnNumber.ShouldBe(31);
        warning.EndLineNumber.ShouldBe(37);
        warning.EndColumnNumber.ShouldBe(41);
        string warningTemplate = oracle.GetString(WarningResourceName, CultureInfo.CurrentUICulture)!;
        log.ExtractMessageCode(log.FormatString(warningTemplate, warningArguments), out string expectedWarning).ShouldBe("MSB3026");
        warning.Message.ShouldBe(expectedWarning);
        subcategoryLookups.ShouldBe(1);
    }

    [Fact]
    public void ExceptionOverloadsPreserveDescriptorAndAggregateLocation()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("MSBUILDDIAGNOSTICS", null);
        StringResourceManager manager = CreateIndexedManager(env, 1);
        StringResource resource = new(ErrorResourceName, 0, () => manager);
        var engine = new CapturingBuildEngine();
        TaskLoggingHelper log = new(new LoggingTask { BuildEngine = engine })
        {
            TaskResources = manager,
            HelpKeywordPrefix = HelpKeywordPrefix
        };
        var exception = new InvalidOperationException("exception message");

        ResourceLogging.LogErrorWithCodeAndExceptionFromResources(log, exception, false, false, resource, "bad argument");

        BuildErrorEventArgs error = engine.Errors.ShouldHaveSingleItem();
        error.Message.ShouldBe(exception.Message);
        error.File.ShouldBe(engine.ProjectFileOfTaskNode);
        const string file = "exception.proj";
        ResourceLogging.LogErrorWithCodeAndExceptionFromResources(log, exception, false, false, file, resource, "bad argument");
        engine.Errors.Count.ShouldBe(2);
        engine.Errors[1].Message.ShouldBe(exception.Message);
        engine.Errors[1].File.ShouldBe(file);

        var first = new InvalidOperationException("first exception");
        var second = new ArgumentException("second exception");
        var aggregate = new AggregateException([first, new AggregateException([second])]);
        ResourceLogging.LogErrorWithCodeAndExceptionFromResources(
            log, aggregate, false, false, "subcategory", file, 13, 17, 19, 23, resource, "bad argument");

        engine.Errors.Count.ShouldBe(4);
        engine.Errors[2].Message.ShouldBe(first.Message);
        engine.Errors[3].Message.ShouldBe(second.Message);
        for (int i = 2; i < engine.Errors.Count; i++)
        {
            BuildErrorEventArgs innerError = engine.Errors[i];
            innerError.Subcategory.ShouldBe("subcategory");
            innerError.File.ShouldBe(file);
            innerError.LineNumber.ShouldBe(13);
            innerError.ColumnNumber.ShouldBe(17);
            innerError.EndLineNumber.ShouldBe(19);
            innerError.EndColumnNumber.ShouldBe(23);
        }

        foreach (BuildErrorEventArgs loggedError in engine.Errors)
        {
            loggedError.Code.ShouldBe("MSB3095");
            loggedError.HelpKeyword.ShouldBe(HelpKeywordPrefix + ErrorResourceName);
        }

        log.HasLoggedErrors.ShouldBeTrue();
    }

    [Fact]
    public void ReplacedAndCustomResourceManagersAreHonored()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        StringResourceManager owner = CreateIndexedManager(env, 1);
        StringResourceManager replacement = CreateIndexedManager(env, 1);
        ResourceManager oracle = CreatePlatformManager(env);
        RecordingResourceManager shared = TrackResources(env, new RecordingResourceManager("shared {0}"));
        int providerCalls = 0;
        StringResource resource = new(MessageResourceName, 0, () =>
        {
            providerCalls++;
            return owner;
        });
        TaskLoggingHelperExtension log = new(new LoggingTask(), owner, shared, HelpKeywordPrefix)
        {
            TaskResources = replacement
        };
        string template = oracle.GetString(MessageResourceName, CultureInfo.CurrentUICulture)!;
        string otherTemplate = oracle.GetString(ErrorResourceName, CultureInfo.CurrentUICulture)!;
        replacement.GetString(0, ErrorResourceName, CultureInfo.CurrentUICulture).ShouldBe(otherTemplate);

        ResourceLogging.GetResourceMessage(log, resource).ShouldBe(template);
        replacement.GetString(0, ErrorResourceName, CultureInfo.CurrentUICulture).ShouldBe(otherTemplate);
        providerCalls.ShouldBe(1);

        log.TaskResources = oracle;
        ResourceLogging.GetResourceMessage(log, resource).ShouldBe(template);
        providerCalls.ShouldBe(1);

        RecordingResourceManager custom = TrackResources(env, new RecordingResourceManager("custom {0}"));
        log.TaskResources = custom;
        ResourceLogging.FormatResourceString(log, resource, "value").ShouldBe("custom value");
        custom.LastName.ShouldBe(MessageResourceName);
        custom.LastCulture.ShouldBeSameAs(CultureInfo.CurrentUICulture);
        custom.LookupCount.ShouldBe(1);
        shared.LookupCount.ShouldBe(0);
        providerCalls.ShouldBe(1);

        custom.Value = null;
        ResourceLogging.FormatResourceString(log, resource, "value").ShouldBe("shared value");
        custom.LookupCount.ShouldBe(2);
        shared.LookupCount.ShouldBe(1);
        shared.LastName.ShouldBe(MessageResourceName);
        shared.LastCulture.ShouldBeSameAs(CultureInfo.CurrentUICulture);
        providerCalls.ShouldBe(1);
    }

    [Fact]
    public void DerivedLoggersKeepPublicResourceOverrides()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        RecordingResourceManager manager = TrackResources(env, new RecordingResourceManager(null));
        var provider = new Lazy<ResourceManager>(
            static () => throw new InvalidOperationException("Custom logging hooks must not initialize resource providers."));
        StringResource resource = new(MessageResourceName, 0, () => provider.Value);
        var baseEngine = new CapturingBuildEngine();
        var baseHooks = new ResourceOverrides();
        TaskLoggingHelper baseLog = new DerivedLoggingHelper(new LoggingTask { BuildEngine = baseEngine }, baseHooks)
        {
            TaskResources = manager
        };
        var extensionEngine = new CapturingBuildEngine();
        var extensionHooks = new ResourceOverrides();
        TaskLoggingHelper extensionLog = new DerivedLoggingHelperExtension(
            new LoggingTask { BuildEngine = extensionEngine }, manager, extensionHooks);

        VerifyPublicResourceOverrides(baseLog, resource, baseHooks, baseEngine);
        VerifyPublicResourceOverrides(extensionLog, resource, extensionHooks, extensionEngine);

        manager.LookupCount.ShouldBe(0);
        provider.IsValueCreated.ShouldBeFalse();
    }

    private static StringResourceManager CreateIndexedManager(TestEnvironment env, int resourceCount)
        => TrackResources(env, new StringResourceManager(
            s_resourceBaseName,
            typeof(TaskLoggingHelperExtension).Assembly,
            resourceCount,
            static _ => throw new InvalidOperationException("Static resource logging must not invoke dynamic key dispatch.")));

    private static ResourceManager CreatePlatformManager(TestEnvironment env)
        => TrackResources(env, new ResourceManager(s_resourceBaseName, typeof(TaskLoggingHelperExtension).Assembly));

    private static T TrackResources<T>(TestEnvironment env, T manager) where T : ResourceManager
    {
        env.WithTransientTestState(new ResourceManagerState(manager));
        return manager;
    }

    private static void VerifyStringConvenienceBinding(Expression<Action> expression)
    {
        expression.Body.NodeType.ShouldBe(ExpressionType.Call);
        var call = (MethodCallExpression)expression.Body;
        call.Method.DeclaringType.ShouldBe(typeof(TaskLoggingHelper));
        call.Method.IsStatic.ShouldBeFalse();
        var parameters = call.Method.GetParameters();
        parameters.Length.ShouldBe(2);
        parameters[0].Name.ShouldBe("messageResourceName");
        parameters[0].ParameterType.ShouldBe(typeof(string));
        parameters[1].ParameterType.ShouldBe(typeof(object[]));
        call.Arguments.Count.ShouldBe(2);
        call.Arguments[1].NodeType.ShouldBe(ExpressionType.NewArrayInit);
        var packedArguments = (NewArrayExpression)call.Arguments[1];
        packedArguments.Expressions.Count.ShouldBe(7);
        packedArguments.Expressions[5].NodeType.ShouldBe(ExpressionType.Convert);
        ((UnaryExpression)packedArguments.Expressions[5]).Operand.Type.ShouldBe(typeof(StringResource));
    }

    private static void VerifyPublicResourceOverrides(
        TaskLoggingHelper log, StringResource resource, ResourceOverrides hooks, CapturingBuildEngine engine)
    {
        ResourceLogging.FormatResourceString(log, resource, "argument").ShouldBe("CUSTOM42: overridden argument");
        hooks.FormattingCalls.ShouldBe(1);
        hooks.FormattedResourceName.ShouldBe(resource.Name);
        ResourceLogging.GetResourceMessage(log, resource).ShouldBe("overridden message {0}");
        hooks.MessageCalls.ShouldBe(1);
        ResourceLogging.LogMessageFromResources(log, MessageImportance.Low, resource, "argument");
        hooks.MessageCalls.ShouldBe(2);
        hooks.MessageResourceName.ShouldBe(resource.Name);
        hooks.FormattingCalls.ShouldBe(1);
        engine.Messages.ShouldHaveSingleItem().Message.ShouldBe("overridden message argument");
    }

    private sealed class ResourceManagerState(ResourceManager manager) : TransientTestState
    {
        public override void Revert() => manager.ReleaseAllResources();
    }

    private sealed class LoggingTask : Task
    {
        public override bool Execute() => true;
    }

    private sealed class CapturingBuildEngine : IBuildEngine
    {
        internal List<BuildMessageEventArgs> Messages { get; } = [];
        internal List<BuildWarningEventArgs> Warnings { get; } = [];
        internal List<BuildErrorEventArgs> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "logging.proj";

        public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e);
        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);
        public void LogCustomEvent(CustomBuildEventArgs e) => throw new NotSupportedException();

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
            => throw new NotSupportedException();
    }

    private sealed class CountingArgument(string value)
    {
        internal int FormattingCalls { get; private set; }

        public override string ToString()
        {
            FormattingCalls++;
            return value;
        }
    }

    private sealed class RecordingResourceManager(string? value) : ResourceManager
    {
        internal string? Value { get; set; } = value;
        internal int LookupCount { get; private set; }
        internal string? LastName { get; private set; }
        internal CultureInfo? LastCulture { get; private set; }

        public override string? GetString(string name, CultureInfo? culture)
        {
            LookupCount++;
            LastName = name;
            LastCulture = culture;
            return Value;
        }

        public override void ReleaseAllResources()
        {
            // This stub owns no BCL resource sets.
        }
    }

    private sealed class ResourceOverrides
    {
        internal int FormattingCalls { get; private set; }
        internal int MessageCalls { get; private set; }
        internal string? FormattedResourceName { get; private set; }
        internal string? MessageResourceName { get; private set; }

        internal string Format(TaskLoggingHelper log, string resourceName, object?[]? args)
        {
            FormattingCalls++;
            FormattedResourceName = resourceName;
            return log.FormatString("CUSTOM42: overridden {0}", args);
        }

        internal string GetMessage(string resourceName)
        {
            MessageCalls++;
            MessageResourceName = resourceName;
            return "overridden message {0}";
        }
    }

    private sealed class DerivedLoggingHelper(ITask task, ResourceOverrides hooks) : TaskLoggingHelper(task)
    {
        public override string FormatResourceString(string resourceName, params object?[]? args) => hooks.Format(this, resourceName, args);
        public override string GetResourceMessage(string resourceName) => hooks.GetMessage(resourceName);
    }

    private sealed class DerivedLoggingHelperExtension(ITask task, ResourceManager resources, ResourceOverrides hooks)
        : TaskLoggingHelperExtension(task, resources, resources, TaskLoggingHelperResourceExtensions_Tests.HelpKeywordPrefix)
    {
        public override string FormatResourceString(string resourceName, params object?[]? args) => hooks.Format(this, resourceName, args);
        public override string GetResourceMessage(string resourceName) => hooks.GetMessage(resourceName);
    }
}
