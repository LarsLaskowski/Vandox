using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Vandox.Backend;

/// <summary>
/// The entry point of vandoxd. It only passes the process boundaries (arguments, environment, standard streams, the stop signal)
/// to <see cref="BackendApp"/>, which carries the logic and is tested.
/// </summary>
[ExcludeFromCodeCoverage]
public static class Program
{
    #region Methods

    /// <summary>
    /// Runs vandoxd.
    /// </summary>
    /// <param name="args">The command-line arguments</param>
    /// <returns>The exit code: 0 on success, 1 on a runtime or start-up failure, 2 on a usage error</returns>
    public static async Task<int> Main(string[] args)
    {
        var environment = Environment.GetEnvironmentVariables()
                                     .Cast<System.Collections.DictionaryEntry>()
                                     .Select(entry => new KeyValuePair<string, string>((string)entry.Key, (string?)entry.Value ?? string.Empty))
                                     .ToList();

        using var stop = new CancellationTokenSource();
        var registrations = new List<PosixSignalRegistration>();

        // After the first signal the default handling returns, so a second signal ends the process at once.
        void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            stop.Cancel();
            registrations.ForEach(registration => registration.Dispose());
        }

        registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal));
        registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal));

        return await BackendApp.RunAsync(args, environment, Console.Out, Console.Error, new ServeHooks(), stop.Token).ConfigureAwait(false);
    }

    #endregion // Methods
}