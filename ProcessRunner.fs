module FsLangMcp.ProcessRunner

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks

type internal ProcessOutput =
    { ExitCode: int
      StandardOutput: string
      StandardError: string
      StandardOutputTruncated: bool
      StandardErrorTruncated: bool }

let private cleanupTimeout = TimeSpan.FromSeconds(5.0)
let private defaultOutputLimitCharacters = 4 * 1024 * 1024

let private outputLimitFromEnvironment () =
    match Environment.GetEnvironmentVariable("FSLANGMCP_PROCESS_OUTPUT_LIMIT_CHARS") with
    | value when not (String.IsNullOrWhiteSpace value) ->
        match Int32.TryParse value with
        | true, parsed when parsed > 0 -> parsed
        | _ -> defaultOutputLimitCharacters
    | _ -> defaultOutputLimitCharacters

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private JobObjectBasicLimitInformation =
    val mutable PerProcessUserTimeLimit: int64
    val mutable PerJobUserTimeLimit: int64
    val mutable LimitFlags: uint32
    val mutable MinimumWorkingSetSize: unativeint
    val mutable MaximumWorkingSetSize: unativeint
    val mutable ActiveProcessLimit: uint32
    val mutable Affinity: unativeint
    val mutable PriorityClass: uint32
    val mutable SchedulingClass: uint32

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private IoCounters =
    val mutable ReadOperationCount: uint64
    val mutable WriteOperationCount: uint64
    val mutable OtherOperationCount: uint64
    val mutable ReadTransferCount: uint64
    val mutable WriteTransferCount: uint64
    val mutable OtherTransferCount: uint64

[<Struct; StructLayout(LayoutKind.Sequential)>]
type private JobObjectExtendedLimitInformation =
    val mutable BasicLimitInformation: JobObjectBasicLimitInformation
    val mutable IoInfo: IoCounters
    val mutable ProcessMemoryLimit: unativeint
    val mutable JobMemoryLimit: unativeint
    val mutable PeakProcessMemoryUsed: unativeint
    val mutable PeakJobMemoryUsed: unativeint

[<Literal>]
let private JobObjectExtendedLimitInformationClass = 9

[<Literal>]
let private JobObjectLimitKillOnJobClose = 0x00002000u

[<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
extern nativeint private CreateJobObject(nativeint jobAttributes, string name)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private SetInformationJobObject(
    nativeint job,
    int informationClass,
    JobObjectExtendedLimitInformation& information,
    uint32 informationLength
)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private AssignProcessToJobObject(nativeint job, nativeint childProcessHandle)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private TerminateJobObject(nativeint job, uint32 exitCode)

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private CloseHandle(nativeint handle)

[<DllImport("libc", SetLastError = true)>]
extern int private setsid()

[<DllImport("libc", SetLastError = true)>]
extern int private kill(int pid, int signal)

[<Literal>]
let internal InternalProcessSessionWrapperArgument = "--internal-process-session-wrapper"

type internal UnixSessionWrapperPreference =
    | Automatic
    | Managed

let private executableExists (fileName: string) =
    if Path.IsPathRooted fileName || fileName.Contains(Path.DirectorySeparatorChar) then
        File.Exists fileName
    else
        Environment.GetEnvironmentVariable("PATH")
        |> Option.ofObj
        |> Option.defaultValue ""
        |> fun value -> value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.exists (fun directory -> File.Exists(Path.Combine(directory, fileName)))

let private resolveDotnetHost () =
    let existingEnvironmentPath name =
        Environment.GetEnvironmentVariable(name)
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.filter File.Exists

    let dotnetRootHost =
        Environment.GetEnvironmentVariable("DOTNET_ROOT")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (fun root -> Path.Combine(root, "dotnet"))
        |> Option.filter File.Exists

    existingEnvironmentPath "DOTNET_HOST_PATH"
    |> Option.orElse dotnetRootHost
    |> Option.defaultValue "dotnet"

let private configureManagedUnixSessionWrapper (startInfo: ProcessStartInfo) =
    let assemblyPath = typeof<ProcessOutput>.Assembly.Location

    if String.IsNullOrWhiteSpace assemblyPath || not (File.Exists assemblyPath) then
        invalidOp "Unable to locate FsLangMcp.dll for the managed Unix session wrapper."

    let command = startInfo.FileName
    let arguments = startInfo.ArgumentList |> Seq.toArray
    startInfo.FileName <- resolveDotnetHost ()
    startInfo.ArgumentList.Clear()
    startInfo.ArgumentList.Add(assemblyPath)
    startInfo.ArgumentList.Add(InternalProcessSessionWrapperArgument)
    startInfo.ArgumentList.Add(command)

    for argument in arguments do
        startInfo.ArgumentList.Add(argument)

let private configureUnixSessionWrapper
    (preference: UnixSessionWrapperPreference)
    (startInfo: ProcessStartInfo)
    =
    if OperatingSystem.IsWindows() || not (executableExists startInfo.FileName) then
        false
    else
        let externalSetsid =
            match preference with
            | Managed -> None
            | Automatic -> [| "/usr/bin/setsid"; "/bin/setsid" |] |> Array.tryFind File.Exists

        match externalSetsid with
        | None ->
            configureManagedUnixSessionWrapper startInfo
            true
        | Some wrapper ->
            let command = startInfo.FileName
            let arguments = startInfo.ArgumentList |> Seq.toArray
            startInfo.FileName <- wrapper
            startInfo.ArgumentList.Clear()
            startInfo.ArgumentList.Add("--")
            startInfo.ArgumentList.Add(command)

            for argument in arguments do
                startInfo.ArgumentList.Add(argument)

            true

let private unixProcessGroupHasLiveMembers groupId =
    if OperatingSystem.IsLinux() then
        try
            Directory.EnumerateDirectories("/proc")
            |> Seq.exists (fun processDirectory ->
                let mutable pid = 0
                let name = Path.GetFileName processDirectory

                if not (Int32.TryParse(name, &pid)) then
                    false
                else
                    try
                        let stat = File.ReadAllText(Path.Combine(processDirectory, "stat"))
                        let commandEnd = stat.LastIndexOf(')')

                        if commandEnd < 0 || commandEnd + 2 >= stat.Length then
                            false
                        else
                            let fields =
                                stat.Substring(commandEnd + 2).Split(
                                    ' ',
                                    StringSplitOptions.RemoveEmptyEntries
                                )

                            if fields.Length < 3 then
                                false
                            else
                                let mutable processGroup = 0
                                let state = fields[0]

                                Int32.TryParse(fields[2], &processGroup)
                                && processGroup = groupId
                                && state <> "Z"
                                && state <> "X"
                    with _ ->
                        false)
        with _ ->
            // Fall back to the portable process-group existence probe. EPERM still
            // means the group exists; only ESRCH (3) proves it is gone.
            let result = kill(-groupId, 0)
            result = 0 || Marshal.GetLastPInvokeError() <> 3
    else
        let result = kill(-groupId, 0)
        result = 0 || Marshal.GetLastPInvokeError() <> 3

type internal ProcessContainment private (childProcess: Process, unixProcessGroup: int option, windowsJob: nativeint option) =
    let mutable disposed = false

    member _.Process = childProcess

    member _.Terminate() =
        match unixProcessGroup with
        | Some groupId ->
            // Negative pid addresses the process group, including descendants that
            // outlived the direct child and can no longer be found via Process.Kill(true).
            kill (-groupId, 9) |> ignore
        | None -> ()

        match windowsJob with
        | Some handle -> TerminateJobObject(handle, 1u) |> ignore
        | None -> ()

        try
            if not childProcess.HasExited then
                childProcess.Kill(true)
        with _ ->
            ()

    member _.WaitForTerminationAsync(timeout: TimeSpan) : Task =
        (task {
            let elapsed = Stopwatch.StartNew()

            try
                do! childProcess.WaitForExitAsync().WaitAsync(timeout)
            with _ ->
                ()

            match unixProcessGroup with
            | Some groupId ->
                // kill(2) only queues SIGKILL; under scheduler pressure a descendant
                // can remain live briefly after the direct child is reaped. Wait for
                // the group to contain no runnable/sleeping members. Linux zombies are
                // already terminated and deliberately do not count as live containment
                // leaks while their new parent catches up with wait(2).
                let mutable live = unixProcessGroupHasLiveMembers groupId

                while live && elapsed.Elapsed < timeout do
                    do! Task.Delay(10)
                    live <- unixProcessGroupHasLiveMembers groupId
            | None -> ()
         }
         :> Task)

    interface IDisposable with
        member this.Dispose() =
            if not disposed then
                disposed <- true
                this.Terminate()

                match windowsJob with
                | Some handle -> CloseHandle(handle) |> ignore
                | None -> ()

                childProcess.Dispose()

    static member Start(startInfo: ProcessStartInfo, preference: UnixSessionWrapperPreference) =
        let wrappedInUnixSession = configureUnixSessionWrapper preference startInfo
        let childProcess = new Process(StartInfo = startInfo)

        if not (childProcess.Start()) then
            childProcess.Dispose()
            invalidOp $"Unable to start process: %s{startInfo.FileName}"

        let unixProcessGroup =
            if OperatingSystem.IsWindows() then
                None
            elif wrappedInUnixSession then
                Some childProcess.Id
            else
                None

        let windowsJob =
            if not (OperatingSystem.IsWindows()) then
                None
            else
                let handle = CreateJobObject(0n, null)

                if handle = 0n then
                    None
                else
                    let mutable info = Unchecked.defaultof<JobObjectExtendedLimitInformation>
                    info.BasicLimitInformation.LimitFlags <- JobObjectLimitKillOnJobClose

                    let configured =
                        SetInformationJobObject(
                            handle,
                            JobObjectExtendedLimitInformationClass,
                            &info,
                            uint32 (Marshal.SizeOf<JobObjectExtendedLimitInformation>())
                        )

                    if configured && AssignProcessToJobObject(handle, childProcess.Handle) then
                        Some handle
                    else
                        CloseHandle(handle) |> ignore
                        None

        new ProcessContainment(childProcess, unixProcessGroup, windowsJob)

let internal startContainedProcess (startInfo: ProcessStartInfo) =
    ProcessContainment.Start(startInfo, UnixSessionWrapperPreference.Automatic)

let private startContainedProcessWithPreference preference (startInfo: ProcessStartInfo) =
    ProcessContainment.Start(startInfo, preference)

let internal terminateContainedProcess (containment: ProcessContainment) = containment.Terminate()

let private observeFault (operation: Task) =
    operation.ContinueWith(
        (fun (faulted: Task) -> faulted.Exception |> ignore),
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted ||| TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default
    )
    |> ignore

let private readBoundedAsync (reader: TextReader) (limitCharacters: int) =
    task {
        let buffer = Array.zeroCreate<char> 8192
        let retained = StringBuilder(min limitCharacters buffer.Length)
        let mutable truncated = false
        let mutable reading = true

        while reading do
            let! count = reader.ReadAsync(buffer, 0, buffer.Length)

            if count = 0 then
                reading <- false
            else
                let remaining = max 0 (limitCharacters - retained.Length)
                let keep = min remaining count

                if keep > 0 then
                    retained.Append(buffer, 0, keep) |> ignore

                if keep < count then
                    truncated <- true

        if truncated then
            retained.Append($"\n[output truncated at {limitCharacters} characters]") |> ignore

        return retained.ToString(), truncated
    }

let private terminate
    (containment: ProcessContainment)
    (stdoutTask: Task<string * bool>)
    (stderrTask: Task<string * bool>)
    =
    task {
        let proc = containment.Process
        containment.Terminate()
        do! containment.WaitForTerminationAsync(cleanupTimeout)

        // A direct child can exit after spawning a descendant that inherited one of
        // the redirected pipe handles. In that case WaitForExitAsync has completed,
        // Kill(entireProcessTree=true) can no longer walk the exited parent, and a
        // ReadToEndAsync would otherwise wait forever for EOF. Close our readers to
        // make pipe cleanup bounded even when the descendant outlives its parent.
        try
            proc.StandardOutput.Close()
        with _ ->
            ()

        try
            proc.StandardError.Close()
        with _ ->
            ()

        // Observe both tasks after closing the process so pipe failures do not become
        // unobserved exceptions during timeout cleanup.
        let pipesTask = Task.WhenAll(stdoutTask, stderrTask)
        observeFault pipesTask

        try
            let! _ = pipesTask.WaitAsync(cleanupTimeout)
            ()
        with _ ->
            ()
    }

let private runAsyncWithOutputLimitCore
    (fileName: string)
    (args: string seq)
    (timeout: TimeSpan)
    (cancellationToken: CancellationToken)
    (outputLimitCharacters: int)
    (unixSessionWrapperPreference: UnixSessionWrapperPreference)
    : Task<ProcessOutput> =
    task {
        if String.IsNullOrWhiteSpace(fileName) then
            invalidArg (nameof fileName) "Process file name must not be blank."

        if timeout <= TimeSpan.Zero then
            invalidArg (nameof timeout) "Process timeout must be positive."

        if outputLimitCharacters <= 0 then
            invalidArg (nameof outputLimitCharacters) "Process output limit must be positive."

        let psi = ProcessStartInfo()
        psi.FileName <- fileName
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.CreateNoWindow <- true

        for arg in args do
            psi.ArgumentList.Add(arg)

        // Reject cancellation at the last boundary we control before Process.Start.
        // Cancellation that races after this check can still observe child side effects;
        // the containment cleanup below bounds that case but cannot make launch atomic.
        cancellationToken.ThrowIfCancellationRequested()

        use containment = startContainedProcessWithPreference unixSessionWrapperPreference psi
        let proc = containment.Process

        // Start both reads before waiting. Reading either stream synchronously first can
        // deadlock when the child fills the other OS pipe buffer.
        let stdoutTask = readBoundedAsync proc.StandardOutput outputLimitCharacters
        let stderrTask = readBoundedAsync proc.StandardError outputLimitCharacters
        use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        timeoutCts.CancelAfter(timeout)

        try
            // The same linked token and CancelAfter deadline covers process exit AND
            // both redirected-pipe drains. Waiting for the parent alone is insufficient:
            // a grandchild may keep an inherited stdout/stderr handle open after the
            // parent exits (#164).
            do! proc.WaitForExitAsync(timeoutCts.Token)
            let! stdout, stdoutTruncated = stdoutTask.WaitAsync(timeoutCts.Token)
            let! stderr, stderrTruncated = stderrTask.WaitAsync(timeoutCts.Token)

            return
                { ExitCode = proc.ExitCode
                  StandardOutput = stdout
                  StandardError = stderr
                  StandardOutputTruncated = stdoutTruncated
                  StandardErrorTruncated = stderrTruncated }
        with :? OperationCanceledException ->
            do! terminate containment stdoutTask stderrTask

            if cancellationToken.IsCancellationRequested then
                return raise (OperationCanceledException(cancellationToken))
            else
                return raise (
                    TimeoutException($"Process '%s{fileName}' timed out after %d{int64 timeout.TotalMilliseconds}ms.")
                )
    }

let internal runAsyncWithOutputLimit
    (fileName: string)
    (args: string seq)
    (timeout: TimeSpan)
    (cancellationToken: CancellationToken)
    (outputLimitCharacters: int)
    : Task<ProcessOutput> =
    runAsyncWithOutputLimitCore
        fileName
        args
        timeout
        cancellationToken
        outputLimitCharacters
        UnixSessionWrapperPreference.Automatic

let internal runAsyncWithManagedUnixSessionWrapper
    (fileName: string)
    (args: string seq)
    (timeout: TimeSpan)
    (cancellationToken: CancellationToken)
    : Task<ProcessOutput> =
    runAsyncWithOutputLimitCore
        fileName
        args
        timeout
        cancellationToken
        (outputLimitFromEnvironment ())
        UnixSessionWrapperPreference.Managed

let internal runUnixSessionWrapper (fileName: string) (args: string array) =
    if OperatingSystem.IsWindows() then
        Console.Error.WriteLine("The internal process-session wrapper is only supported on Unix.")
        64
    elif String.IsNullOrWhiteSpace fileName then
        Console.Error.WriteLine("The internal process-session wrapper requires a command.")
        64
    elif setsid () < 0 then
        Console.Error.WriteLine(
            $"Unable to create a Unix process session (errno %d{Marshal.GetLastPInvokeError()})."
        )

        125
    else
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- fileName
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true

        for arg in args do
            startInfo.ArgumentList.Add(arg)

        try
            use target = new Process(StartInfo = startInfo)

            if not (target.Start()) then
                Console.Error.WriteLine($"Unable to start process: %s{fileName}")
                127
            else
                target.WaitForExit()
                target.ExitCode
        with ex ->
            Console.Error.WriteLine($"Unable to start process '%s{fileName}': %s{ex.Message}")
            127

let internal runAsync
    (fileName: string)
    (args: string seq)
    (timeout: TimeSpan)
    (cancellationToken: CancellationToken)
    : Task<ProcessOutput> =
    runAsyncWithOutputLimit fileName args timeout cancellationToken (outputLimitFromEnvironment ())

let internal run (fileName: string) (args: string seq) (timeout: TimeSpan) : ProcessOutput =
    runAsync fileName args timeout CancellationToken.None
    |> fun operation -> operation.GetAwaiter().GetResult()
