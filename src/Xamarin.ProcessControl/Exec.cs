//
// Author:
//   Aaron Bockover <abock@xamarin.com>
//
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace Xamarin.ProcessControl
{
  using static ExecFlags;

    public sealed class Exec
    {
        static readonly bool isWindows = RuntimeInformation.IsOSPlatform (OSPlatform.Windows);

        [DllImport ("libc")]
        static extern uint geteuid ();

        public delegate Task<int> ProcessRunnerHandler (ProcessArguments arguments, Process process);

        public static ProcessRunnerHandler GlobalProcessRunner { get; set; }

        public static event EventHandler<ExecStatusEventArgs> Monitor;

        static volatile int lastId;

        public int Id { get; }
        public ProcessArguments Arguments { get; }
        public ConsoleRedirection OutputRedirection { get; }
        public Action<StreamWriter> InputHandler { get; }
        public ExecFlags Flags { get; }
        public string WorkingDirectory { get; }
        public bool Elevated { get; }
        public ProcessRunnerHandler ProcessRunner { get; }

        public Exec (
            ProcessArguments arguments,
            ExecFlags flags = None,
            ConsoleRedirection outputRedirection = null,
            Action<StreamWriter> inputHandler = null,
            string workingDirectory = null,
            ProcessRunnerHandler processRunner = null)
        {
            Arguments = arguments;

            if (Arguments.Count < 1)
                throw new ArgumentOutOfRangeException (
                    nameof (arguments),
                    "must have at least one argument (the file name to execute)");

            Id = lastId++;
            Flags = flags;
            Elevated = flags.HasFlag (Elevate);
            InputHandler = inputHandler;
            OutputRedirection = outputRedirection;

            if (isWindows) {
                // Ignore elevation flag if we are already running in the Administrator role
                var identity = WindowsIdentity.GetCurrent ();
                if (identity != null && new WindowsPrincipal (identity).IsInRole (WindowsBuiltInRole.Administrator))
                    Elevated = false;
            } else {
                if (Path.GetExtension (arguments [0]) == ".exe")
                    Arguments = Arguments.Insert (0, "mono");

                if (Elevated) {
                    if (geteuid () == 0)
                        Elevated = false;
                    else
                        Arguments = Arguments.Insert (0, "/usr/bin/sudo");
                }
            }

            if (Flags.HasFlag (RedirectStdin) && InputHandler == null)
                throw new ArgumentException (
                    $"{nameof (RedirectStdin)} was specified " +
                    $"but {nameof (InputHandler)} is null",
                    nameof (flags));

            if (Flags.HasFlag (RedirectStdout) && OutputRedirection == null)
                throw new ArgumentException (
                    $"{nameof (RedirectStdout)} was specified " +
                    $"but {nameof (OutputRedirection)} is null",
                    nameof (flags));

            if (Flags.HasFlag (RedirectStderr) && OutputRedirection == null)
                throw new ArgumentException (
                    $"{nameof (RedirectStderr)} was specified " +
                    $"but {nameof (OutputRedirection)} is null",
                    nameof (flags));

            WorkingDirectory = workingDirectory;
            ProcessRunner = processRunner
                ?? GlobalProcessRunner
                ?? DefaultProcessRunner;
        }

        public sealed class ExitException : Exception
        {
            public ExecStatusEventArgs Event { get; }

            internal ExitException (ExecStatusEventArgs @event)
                : base ($"{@event.Exec.Arguments [0]} terminated with exit code {@event.ExitCode}")
                => Event = @event;
        }

        public async Task<ExecStatusEventArgs> RunAsync ()
        {
            var proc = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = Arguments [0],
                    Arguments = string.Join (
                        " ",
                        Arguments.Skip (1).Select (ProcessArguments.Quote)),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = WorkingDirectory
                }
            };

            if (isWindows && Elevated) {
                proc.StartInfo.UseShellExecute = true;
                proc.StartInfo.Verb = "runas";
            } else {
                proc.StartInfo.RedirectStandardInput = Flags.HasFlag (RedirectStdin);
                proc.StartInfo.RedirectStandardOutput = Flags.HasFlag (RedirectStdout);
                proc.StartInfo.RedirectStandardError = Flags.HasFlag (RedirectStderr);
            }

            if (proc.StartInfo.RedirectStandardOutput)
                proc.OutputDataReceived += (sender, e)
                    => WriteOutput (e.Data, OutputRedirection.StandardOutput);

            if (proc.StartInfo.RedirectStandardError)
                proc.ErrorDataReceived += (sender, e)
                    => WriteOutput (e.Data, OutputRedirection.StandardError);

            void WriteOutput (string data, TextWriter writer)
            {
                if (string.IsNullOrEmpty (data))
                    return;

                data += Environment.NewLine;

                if (Flags.HasFlag (OutputOnSynchronizationContext) &&
                    SynchronizationContext.Current != null)
                    SynchronizationContext.Current.Post (writer.Write, data);
                else
                    writer.Write (data);
            }

            var eventArgs = new ExecStatusEventArgs (this);
            Monitor?.Invoke (null, eventArgs);
            var exitCode = await ProcessRunner (Arguments, proc).ConfigureAwait (false);
            eventArgs = eventArgs.WithProcessEnded (exitCode);
            Monitor?.Invoke (null, eventArgs);

            if (exitCode != 0)
                throw new ExitException (eventArgs);

            return eventArgs;
        }

        Task<int> DefaultProcessRunner (ProcessArguments arguments, Process proc)
        {
            var tcs = new TaskCompletionSource<int> ();

            Task.Run (() => {
                try {
                    proc.Start ();

                    if (proc.StartInfo.RedirectStandardOutput)
                        proc.BeginOutputReadLine ();

                    if (proc.StartInfo.RedirectStandardError)
                        proc.BeginErrorReadLine ();

                    InputHandler?.Invoke (proc.StandardInput);

                    proc.WaitForExit ();

                    tcs.SetResult (proc.ExitCode);
                } catch (Exception e) {
                    tcs.SetException (e);
                } finally {
                    proc.Close ();
                }
            });

            return tcs.Task;
        }

        #region Convenience Methods

        public static Task<ExecStatusEventArgs> RunAsync (
            Action<ConsoleRedirection.Segment> outputHandler,
            string command,
            params string [] arguments)
            => RunAsync (Default, outputHandler, command, arguments);

        public static Task<ExecStatusEventArgs> RunAsync (
            ExecFlags flags,
            Action<ConsoleRedirection.Segment> outputHandler,
            string command,
            params string [] arguments)
            => new Exec (
                ProcessArguments.FromCommandAndArguments (command, arguments),
                flags | RedirectStdout | RedirectStderr,
                new ConsoleRedirection (outputHandler)).RunAsync ();

        public static IReadOnlyList<string> Run (
            string command,
            params string [] arguments)
            => Run (Default, command, arguments);

        public static IReadOnlyList<string> Run (
            ExecFlags flags,
            string command,
            params string [] arguments)
        {
            var lines = new List<string> ();

            new Exec (
                ProcessArguments.FromCommandAndArguments (command, arguments),
                flags | RedirectStdout | RedirectStderr,
                new ConsoleRedirection (segment => lines.Add (segment.Data.TrimEnd ('\r', '\n'))))
                .RunAsync ()
                .GetAwaiter ()
                .GetResult ();

            return lines;
        }

        #endregion
    }
}