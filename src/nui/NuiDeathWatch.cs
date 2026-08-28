using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Overscan
{
    /// <summary>
    /// Makes the end of the process say its own name.
    ///
    /// Issue #20's report from `build-c0cd5ab` is the first one where the heartbeat
    /// survived to the end of a run, and it rules out the two explanations that were
    /// on the table. The trail ends like this:
    ///
    /// <code>
    ///   15:24:42  memory: 89 MB resident (peak 91)
    ///   15:24:47  memory: 89 MB resident (peak 91)
    ///   15:24:52  memory: 64 MB resident (peak 91)
    /// </code>
    ///
    /// Flat at ninety megabytes and then gone, so the low-memory killer is not what
    /// takes this app away — an eviction is a slope and there is no slope. And the
    /// three lines <see cref="NuiProgram"/> would have written are all absent: no
    /// `app loop returned`, no `FATAL in Main`, so the main loop neither exited nor
    /// threw. Something ended the process without unwinding it.
    ///
    /// That leaves three possibilities and no way to tell them apart, which is what
    /// this class is for. Each one leaves a different line here:
    ///
    /// <list type="number">
    ///   <item><b>The platform closed us.</b> Tizen asks an app to go away with
    ///   SIGTERM, and closes one it has paused without asking. Either way there is a
    ///   line — the signal, or <c>NuiBrowserApp.OnPause</c>/<c>OnTerminate</c>.</item>
    ///   <item><b>Our own code threw</b> somewhere <see cref="NuiProgram"/>'s try
    ///   cannot see it. A managed exception raised inside a native callback — a
    ///   timer tick, an engine event — does not come back out through
    ///   <c>Application.Run</c>; it goes to
    ///   <see cref="AppDomain.UnhandledException"/> and then the process dies.</item>
    ///   <item><b>A hard native crash</b> inside the engine. Nothing here fires,
    ///   because SIGSEGV cannot be observed from managed code without taking the
    ///   runtime's own handler away from it — which is not worth doing, since the
    ///   absence of every other line is the answer by elimination.</item>
    /// </list>
    ///
    /// The <c>Cancel</c> flag is never set on a signal: this only writes down what
    /// arrived. An app that argues with the platform about being closed is a worse
    /// app than one that closes.
    /// </summary>
    internal static class NuiDeathWatch
    {
        /// <summary>
        /// The registrations are held here for the same reason the heartbeat is held
        /// in a field: a <see cref="PosixSignalRegistration"/> unregisters itself when
        /// it is collected, so a run of them created and dropped in a method arms
        /// nothing at all past the first garbage collection. That mistake has already
        /// cost this issue three builds' worth of missing heartbeat lines.
        /// </summary>
        private static readonly List<PosixSignalRegistration> Held = new List<PosixSignalRegistration>();

        private static bool _armed;

        /// <summary>
        /// What arrived last, for the report — though the trail is where it matters,
        /// since the run that gets a signal is not the run anybody reads.
        /// </summary>
        public static string LastWord { get; private set; } = "(nothing yet)";

        /// <summary>
        /// Arms everything. Called before the app loop starts and safe to call twice.
        /// Best-effort in the usual sense: a platform that will not let us watch for
        /// a signal leaves the app exactly as it was, with the reason on the trail.
        /// </summary>
        public static void Arm()
        {
            if (_armed)
            {
                return;
            }

            _armed = true;

            try
            {
                AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
                {
                    var ex = e.ExceptionObject as Exception;
                    Say(ex == null
                        ? "unhandled managed exception: " + e.ExceptionObject
                        : "unhandled managed exception: " + ex.GetType().Name + ": " + ex.Message);

                    if (ex != null && ex.StackTrace != null)
                    {
                        Breadcrumbs.DropToTrail("    at " + ex.StackTrace.Replace("\n", "\n    at "));
                    }
                };

                AppDomain.CurrentDomain.ProcessExit += delegate
                {
                    Say("process exiting");
                };
            }
            catch (Exception ex)
            {
                Breadcrumbs.DropToTrail("death watch: could not hook the app domain: " + ex.Message);
            }

            Watch(PosixSignal.SIGTERM);
            Watch(PosixSignal.SIGINT);
            Watch(PosixSignal.SIGQUIT);
            Watch(PosixSignal.SIGHUP);

            Breadcrumbs.DropToTrail("death watch armed (" + Held.Count + " signals)");
        }

        /// <summary>
        /// One signal. Named on the trail when it arrives and then allowed to do
        /// exactly what it would have done.
        /// </summary>
        private static void Watch(PosixSignal signal)
        {
            try
            {
                Held.Add(PosixSignalRegistration.Create(signal, delegate (PosixSignalContext context)
                {
                    Say("signal " + context.Signal + " — the platform is closing this app");
                }));
            }
            catch (Exception ex)
            {
                Breadcrumbs.DropToTrail("death watch: " + signal + " not watchable: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// Trail first, then the field. A handler running while the process is being
        /// taken apart has no guarantee of a next line, so the write to disk goes
        /// before anything that could be considered tidy.
        /// </summary>
        private static void Say(string what)
        {
            Breadcrumbs.DropToTrail(what);
            LastWord = what;
        }
    }
}
