using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Cli;

internal static class CliProgram
{
    internal const int SuccessExitCode = 0;
    internal const int OperationFailedExitCode = 1;
    internal const int UsageExitCode = 2;
    internal const int InfrastructureExitCode = 3;
    internal const int CancelledExitCode = 130;

    private const string DefaultPipeName =
        "TwinCatAgentGateway";
    private static readonly JsonSerializerOptions JsonOptions =
        GatewayJson.CreateSerializerOptions();

    private static readonly string Usage = """
        Usage:
          twincat-gateway [--pipe NAME] status
          twincat-gateway [--pipe NAME] diagnostics [options]
          twincat-gateway [--pipe NAME] xae-messages --profile NAME
          twincat-gateway [--pipe NAME] build --profile NAME [options]
          twincat-gateway [--pipe NAME] activate --profile NAME [options]
          twincat-gateway [--pipe NAME] resource --uri URI [options]

        build options:
          --action build|rebuild|clean
          --scope plc|solution
          --project LOGICAL_NAME
          --changed PATH              Repeat for multiple changed paths.
          --detail compact|full

        activate options:
          --final-target-mode run|unchanged
          --verification none|tcunit
          --timeout SECONDS

        resource options:
          --offset NUMBER
          --max-characters NUMBER
        """;

    public static async Task<int> RunAsync(
        string[] args,
        Func<string, ITwinCatGatewayClient> clientFactory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            GlobalArguments global = ParseGlobalArguments(args);
            if (global.ShowHelp)
            {
                await output.WriteLineAsync(Usage)
                    .ConfigureAwait(false);
                return SuccessExitCode;
            }

            ITwinCatGatewayClient client =
                clientFactory(global.PipeName)
                ?? throw new InvalidOperationException(
                    "The CLI client factory returned null.");
            return await ExecuteAsync(
                    global.CommandArguments,
                    client,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CliUsageException exception)
        {
            await error.WriteLineAsync(exception.Message)
                .ConfigureAwait(false);
            await error.WriteLineAsync(Usage)
                .ConfigureAwait(false);
            return UsageExitCode;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Operation cancelled.")
                .ConfigureAwait(false);
            return CancelledExitCode;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(
                    $"{exception.GetType().Name}: "
                    + exception.Message)
                .ConfigureAwait(false);
            return InfrastructureExitCode;
        }
    }

    private static Task<int> ExecuteAsync(
        string[] args,
        ITwinCatGatewayClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new CliUsageException(
                "A command is required.");
        }

        string command = args[0];
        string[] commandArguments = args.Skip(1).ToArray();
        return command switch
        {
            "status" => ExecuteStatusAsync(
                commandArguments,
                client,
                output,
                cancellationToken),
            "diagnostics" => ExecuteDiagnosticsAsync(
                commandArguments,
                client,
                output,
                cancellationToken),
            "xae-messages" => ExecuteXaeMessagesAsync(
                commandArguments,
                client,
                output,
                cancellationToken),
            "build" => ExecuteBuildAsync(
                commandArguments,
                client,
                output,
                cancellationToken),
            "activate" => ExecuteActivationAsync(
                commandArguments,
                client,
                output,
                cancellationToken),
            "resource" => ExecuteResourceAsync(
                commandArguments,
                client,
                output,
                cancellationToken),
            _ => throw new CliUsageException(
                $"Unknown command '{command}'."),
        };
    }

    private static async Task<int> ExecuteStatusAsync(
        string[] args,
        ITwinCatGatewayClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OptionBag.Parse(args);
        GatewayStateSnapshot response =
            await client.GetGatewayStateAsync(cancellationToken)
                .ConfigureAwait(false);
        await WriteJsonAsync(output, response)
            .ConfigureAwait(false);
        return SuccessExitCode;
    }

    private static async Task<int> ExecuteDiagnosticsAsync(
        string[] args,
        ITwinCatGatewayClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OptionBag.Parse(args);
        ResourceContent response =
            await client.GetResourceAsync(
                    "twincat-gateway://diagnostics",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        await WriteJsonAsync(output, response)
            .ConfigureAwait(false);
        return SuccessExitCode;
    }

    private static async Task<int> ExecuteXaeMessagesAsync(
        string[] args,
        ITwinCatGatewayClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OptionBag options = OptionBag.Parse(args, "--profile");
        string profile = Uri.EscapeDataString(options.GetRequired("--profile"));
        ResourceContent response =
            await client.GetResourceAsync(
                    $"twincat-xae://profile/{profile}/messages/current",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        await WriteJsonAsync(output, response)
            .ConfigureAwait(false);
        return SuccessExitCode;
    }

    private static async Task<int> ExecuteBuildAsync(
        string[] args,
        ITwinCatGatewayClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OptionBag options = OptionBag.Parse(
            args,
            "--profile",
            "--action",
            "--scope",
            "--project",
            "--changed",
            "--detail");
        XaeBuildParameters parameters = new()
        {
            Profile = options.GetRequired("--profile"),
            Project = options.GetOptional("--project"),
            ChangedPaths = options.GetMany("--changed"),
        };
        if (options.GetOptional("--action") is string action)
        {
            parameters.Action = ParseEnum<BuildAction>(
                action,
                "--action");
        }

        if (options.GetOptional("--scope") is string scope)
        {
            parameters.Scope = ParseEnum<XaeBuildScope>(
                scope,
                "--scope");
        }

        if (options.GetOptional("--detail") is string detail)
        {
            parameters.Detail = ParseEnum<DetailLevel>(
                detail,
                "--detail");
        }

        OperationResult<XaeBuildResult> completed =
            await client.BuildXaeAsync(
                    parameters,
                    cancellationToken)
                .ConfigureAwait(false);
        await WriteJsonAsync(output, completed)
            .ConfigureAwait(false);
        return OperationExitCode(completed);
    }

    private static async Task<int> ExecuteActivationAsync(
        string[] args,
        ITwinCatGatewayClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OptionBag options = OptionBag.Parse(
            args,
            "--profile",
            "--final-target-mode",
            "--verification",
            "--timeout");
        ActivateParameters parameters = new()
        {
            Profile = options.GetRequired("--profile"),
        };
        if (options.GetOptional("--final-target-mode")
            is string finalTargetMode)
        {
            parameters.FinalTargetMode =
                ParseEnum<ActivationFinalTargetMode>(
                    finalTargetMode,
                    "--final-target-mode");
        }

        if (options.GetOptional("--verification")
            is string verification)
        {
            parameters.Verification =
                ParseEnum<VerificationMode>(
                    verification,
                    "--verification");
        }

        if (options.GetOptional("--timeout") is string timeout)
        {
            parameters.TimeoutSeconds = ParsePositiveInt(
                timeout,
                "--timeout");
        }

        OperationResult<ActivationResult> completed =
            await client.ActivateXaeAsync(
                    parameters,
                    cancellationToken)
                .ConfigureAwait(false);
        await WriteJsonAsync(output, completed)
            .ConfigureAwait(false);
        return OperationExitCode(completed);
    }

    private static async Task<int> ExecuteResourceAsync(
        string[] args,
        ITwinCatGatewayClient client,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        OptionBag options = OptionBag.Parse(
            args,
            "--uri",
            "--offset",
            "--max-characters");
        long offset = options.GetOptional("--offset")
            is string rawOffset
                ? ParseNonNegativeLong(
                    rawOffset,
                    "--offset")
                : 0;
        int maximumCharacters =
            options.GetOptional("--max-characters")
                is string rawMaximum
                    ? ParsePositiveInt(
                        rawMaximum,
                        "--max-characters")
                    : 64 * 1024;
        ResourceContent response =
            await client.GetResourceAsync(
                    options.GetRequired("--uri"),
                    maximumCharacters,
                    offset,
                    cancellationToken)
                .ConfigureAwait(false);
        await WriteJsonAsync(output, response)
            .ConfigureAwait(false);
        return SuccessExitCode;
    }

    private static GlobalArguments ParseGlobalArguments(
        string[] args)
    {
        if (args.Length == 0)
        {
            return new GlobalArguments(
                DefaultPipeName,
                Array.Empty<string>(),
                showHelp: false);
        }

        if (args.Length == 1
            && (args[0] == "--help"
                || args[0] == "-h"))
        {
            return new GlobalArguments(
                DefaultPipeName,
                Array.Empty<string>(),
                showHelp: true);
        }

        if (args[0] != "--pipe")
        {
            return new GlobalArguments(
                DefaultPipeName,
                args,
                showHelp: false);
        }

        if (args.Length < 3
            || string.IsNullOrWhiteSpace(args[1]))
        {
            throw new CliUsageException(
                "--pipe requires a value followed by a command.");
        }

        return new GlobalArguments(
            args[1],
            args.Skip(2).ToArray(),
            showHelp: false);
    }

    private static int OperationExitCode<TResult>(
        OperationResult<TResult> response)
    {
        return response.Ok
            && response.Completion == OperationCompletion.Succeeded
                ? SuccessExitCode
                : OperationFailedExitCode;
    }

    private static Task WriteJsonAsync<T>(
        TextWriter output,
        T value)
    {
        return output.WriteLineAsync(
            JsonSerializer.Serialize(value, JsonOptions));
    }

    private static TEnum ParseEnum<TEnum>(
        string value,
        string option)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse(value, ignoreCase: true, out TEnum result)
            && Enum.IsDefined(result))
        {
            return result;
        }

        throw new CliUsageException(
            $"Invalid value '{value}' for {option}.");
    }

    private static bool? ParseOptionalBoolean(
        string value,
        string option)
    {
        if (string.Equals(
            value,
            "auto",
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (bool.TryParse(value, out bool result))
        {
            return result;
        }

        throw new CliUsageException(
            $"Invalid value '{value}' for {option}; "
            + "expected auto, true, or false.");
    }

    private static bool ParseBoolean(
        string value,
        string option)
    {
        if (bool.TryParse(value, out bool result))
        {
            return result;
        }

        throw new CliUsageException(
            $"Invalid value '{value}' for {option}; "
            + "expected true or false.");
    }

    private static int ParsePositiveInt(
        string value,
        string option)
    {
        if (int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int result)
            && result > 0)
        {
            return result;
        }

        throw new CliUsageException(
            $"{option} requires a positive integer.");
    }

    private static long ParseNonNegativeLong(
        string value,
        string option)
    {
        if (long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long result)
            && result >= 0)
        {
            return result;
        }

        throw new CliUsageException(
            $"{option} requires a non-negative integer.");
    }

    private sealed class OptionBag
    {
        private readonly Dictionary<
            string,
            List<string>> _values;

        private OptionBag(
            Dictionary<string, List<string>> values)
        {
            _values = values;
        }

        public static OptionBag Parse(
            string[] args,
            params string[] allowedOptions)
        {
            HashSet<string> allowed = new(
                allowedOptions,
                StringComparer.Ordinal);
            Dictionary<string, List<string>> values =
                new(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                if (!allowed.Contains(option))
                {
                    throw new CliUsageException(
                        $"Unknown option or argument '{option}'.");
                }

                if (index + 1 >= args.Length
                    || args[index + 1].StartsWith(
                        "--",
                        StringComparison.Ordinal))
                {
                    throw new CliUsageException(
                        $"{option} requires a value.");
                }

                string value = args[++index];
                if (!values.TryGetValue(
                    option,
                    out List<string>? optionValues))
                {
                    optionValues = new List<string>();
                    values.Add(option, optionValues);
                }

                optionValues.Add(value);
            }

            return new OptionBag(values);
        }

        public string GetRequired(string option)
        {
            return GetOptional(option)
                ?? throw new CliUsageException(
                    $"{option} is required.");
        }

        public string? GetOptional(string option)
        {
            if (!_values.TryGetValue(
                    option,
                    out List<string>? values))
            {
                return null;
            }

            if (values.Count != 1)
            {
                throw new CliUsageException(
                    $"{option} may be specified only once.");
            }

            return values[0];
        }

        public List<string> GetMany(string option)
        {
            return _values.TryGetValue(
                option,
                out List<string>? values)
                    ? new List<string>(values)
                    : new List<string>();
        }
    }

    private sealed class CliUsageException : Exception
    {
        public CliUsageException(string message)
            : base(message)
        {
        }
    }

    private sealed class GlobalArguments
    {
        public GlobalArguments(
            string pipeName,
            string[] commandArguments,
            bool showHelp)
        {
            PipeName = pipeName;
            CommandArguments = commandArguments;
            ShowHelp = showHelp;
        }

        public string PipeName { get; }

        public string[] CommandArguments { get; }

        public bool ShowHelp { get; }
    }
}
