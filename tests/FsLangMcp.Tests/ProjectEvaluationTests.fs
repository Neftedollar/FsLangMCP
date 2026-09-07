module FsLangMcp.Tests.ProjectEvaluationTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FsLangMcp
open Ionide.ProjInfo
open Xunit

let private projectPath = Path.Combine(Path.GetTempPath(), "fslangmcp_wire_test", "Example.fsproj")

let private successfulOutput text : ProcessRunner.ProcessOutput =
    { ExitCode = 0
      StandardOutput = text
      StandardError = ""
      StandardOutputTruncated = false
      StandardErrorTruncated = false }

let private emptyResponse () = ProjectEvaluation.encodeResponse projectPath Array.empty

let private signal () = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

[<Fact>]
let ``helper requires the exact bounded parent authorization frame`` () =
    for input in
        [ ""
          "wrong\n"
          ProjectEvaluation.StartupAuthorization
          ProjectEvaluation.StartupAuthorization + "\r\n"
          ProjectEvaluation.StartupAuthorization + "extra\n" ] do
        use reader = new StringReader(input)

        let error =
            Assert.Throws<InvalidOperationException>(fun () -> ProjectEvaluation.requireStartupAuthorization reader)

        Assert.Contains("startup authorization", error.Message)

    use accepted = new StringReader(ProjectEvaluation.StartupAuthorization + "\n")
    ProjectEvaluation.requireStartupAuthorization accepted
    Assert.Equal(-1, accepted.Read())

[<Fact>]
let ``helper rejects an unbounded wrong input without reading its remainder`` () =
    let mutable charactersRead = 0

    use endless =
        { new TextReader() with
            override _.Read() =
                charactersRead <- charactersRead + 1
                int 'x' }

    Assert.Throws<InvalidOperationException>(fun () -> ProjectEvaluation.requireStartupAuthorization endless)
    |> ignore

    Assert.Equal(1, charactersRead)

[<Fact>]
let ``actual helper rejects missing authorization before inspecting project settings`` () : Task =
    task {
        let missingProject = Path.Combine(Path.GetTempPath(), "fslangmcp_no_project_" + Guid.NewGuid().ToString("N"), "Missing.fsproj")
        let assemblyPath = typeof<ProcessRunner.ProcessOutput>.Assembly.Location

        let! response =
            ProcessRunner.runAsyncWithOutputLimitAfterRequiredContainment
                (ProcessRunner.resolveDotnetHost ())
                [ assemblyPath; ProjectEvaluation.InternalArgument; missingProject ]
                "wrong-authorization"
                (TimeSpan.FromSeconds(30.0))
                CancellationToken.None
                4096

        Assert.Equal(1, response.ExitCode)
        Assert.Equal("", response.StandardOutput)
        Assert.Contains("requires parent startup authorization", response.StandardError)
        Assert.DoesNotContain(missingProject, response.StandardError)
    }

let private isOwnedProcessLive (child: Process, startedAt: DateTime) =
    if OperatingSystem.IsWindows() then
        // The Process handle is opened while the owned child is still alive.
        not child.HasExited
    else
        try
            // Query native state before StartTime: Darwin refuses StartTime
            // for a retained zombie even though its PID is still present.
            let info = ProcessStartInfo("/bin/ps")
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.ArgumentList.Add("-p")
            info.ArgumentList.Add(string child.Id)
            info.ArgumentList.Add("-o")
            info.ArgumentList.Add("stat=")
            use snapshot = Process.Start(info)

            if not (snapshot.WaitForExit(5000)) then
                snapshot.Kill()
                failwith "The native process-state query did not complete."

            let state = snapshot.StandardOutput.ReadToEnd().Trim()

            if snapshot.ExitCode <> 0 && snapshot.ExitCode <> 1 then
                failwith $"The native process-state query failed ({snapshot.ExitCode})."

            if state.Length = 0 || state[0] = 'Z' || state[0] = 'X' then
                false
            else
                // A non-child Unix zombie may report HasExited=false in .NET.
                // Read native state once; no retry or grace period hides a live
                // descendant. PID reuse is distinguished by the captured start time.
                use confirmed = Process.GetProcessById(child.Id)
                confirmed.StartTime = startedAt && not confirmed.HasExited
        with :? ArgumentException ->
            false

[<Fact>]
let ``owned process oracle rejects a live descendant and accepts an unreaped Unix zombie`` () =
    if not (OperatingSystem.IsWindows()) then
        let info = ProcessStartInfo("python3")
        info.UseShellExecute <- false
        info.RedirectStandardInput <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.ArgumentList.Add("-c")
        info.ArgumentList.Add(
            "import os,signal,sys,subprocess,time\n"
            + "pid=os.fork()\n"
            + "if pid==0:\n signal.pause(); os._exit(0)\n"
            + "print(pid,flush=True)\n"
            + "sys.stdin.read(1); os.kill(pid,signal.SIGKILL)\n"
            + "deadline=time.monotonic()+5\n"
            + "while True:\n"
            + " state=subprocess.check_output(['/bin/ps','-p',str(pid),'-o','stat='],text=True).strip()\n"
            + " if state.startswith('Z'): break\n"
            + " if time.monotonic()>deadline: raise RuntimeError('child did not terminate')\n"
            + " time.sleep(.01)\n"
            + "print('terminated-not-reaped',flush=True)\n"
            + "sys.stdin.read(1); os.waitpid(pid,0)\n"
        )

        use owner = Process.Start(info)

        try
            let pid = owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10.0)).GetAwaiter().GetResult() |> Int32.Parse
            use child = Process.GetProcessById(pid)
            let owned = child, child.StartTime
            Assert.True(isOwnedProcessLive owned, "The sleeping child must still count as live.")
            owner.StandardInput.Write("x")
            owner.StandardInput.Flush()
            let state = owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10.0)).GetAwaiter().GetResult()

            if isNull state then
                failwith $"The controlled-zombie fixture failed: {owner.StandardError.ReadToEnd()}"

            Assert.Equal("terminated-not-reaped", state)
            Assert.False(isOwnedProcessLive owned, "An unreaped zombie has already terminated.")
        finally
            if not owner.HasExited then
                owner.StandardInput.Write("xx")
                owner.StandardInput.Flush()

                if not (owner.WaitForExit(10000)) then
                    owner.Kill(true)
                    owner.WaitForExit()

let private emptyProject () : Types.ProjectOptions =
    { ProjectId = None
      ProjectFileName = projectPath
      TargetFramework = "net10.0"
      SourceFiles = []
      OtherOptions = []
      ReferencedProjects = []
      PackageReferences = []
      LoadTime = DateTime.UnixEpoch
      TargetPath = ""
      TargetRefPath = None
      ProjectOutputType = Types.ProjectOutputType.Library
      ProjectSdkInfo =
        { IsTestProject = false
          Configuration = "Debug"
          IsPackable = false
          TargetFramework = "net10.0"
          TargetFrameworkIdentifier = ".NETCoreApp"
          TargetFrameworkVersion = "v10.0"
          MSBuildAllProjects = []
          MSBuildToolsVersion = "Current"
          ProjectAssetsFile = ""
          RestoreSuccess = true
          Configurations = [ "Debug" ]
          TargetFrameworks = [ "net10.0" ]
          RunArguments = None
          RunCommand = None
          IsPublishable = None }
      Items = []
      Properties = []
      CustomProperties = []
      AllProperties = Map.empty
      AllItems = Map.empty
      Analyzers = [] }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``encoder rejects escaped oversized values and map keys before large allocations`` (largeKey: bool) =
    let oversized = String('\u0416', 3 * 1024 * 1024)
    let key, value = if largeKey then oversized, "value" else "LargeProperty", oversized
    let projects = [| { emptyProject () with AllProperties = Map.ofList [ key, Set.singleton value ] } |]
    // Warm reflection/converter caches before measuring only this synchronous
    // encode call. The input is deliberately allocated outside the measurement.
    ProjectEvaluation.encodeResponse projectPath [| emptyProject () |] |> ignore
    let before = GC.GetAllocatedBytesForCurrentThread()

    let rejected =
        try
            ProjectEvaluation.encodeResponse projectPath projects |> ignore
            false
        with :? InvalidOperationException ->
            true

    let allocated = GC.GetAllocatedBytesForCurrentThread() - before
    Assert.True(rejected, "The escaped response must exceed the wire limit.")
    Assert.True(allocated < 8L * 1024L * 1024L, $"Oversized encoding allocated {allocated} bytes before rejecting input.")

[<Fact>]
let ``encoder preserves escaping and surrogate pairs across bounded string segments`` () =
    let value = String('a', 4095) + "\U0001F642\"\\\n\u0416" + String('b', 4096)
    let row = { emptyProject () with AllProperties = Map.ofList [ "<&\"\u0416", Set.singleton value ] }
    let encoded = ProjectEvaluation.encodeResponse projectPath [| row |]
    let decoded = ProjectEvaluation.decodeResponse projectPath encoded
    Assert.True([ row ] = decoded, "Chunked escaping changed the evaluated data.")
    Assert.All(encoded, fun character -> Assert.True(int character < 128))

[<Fact>]
let ``helper envelope roundtrips an empty result and normalizes its project path`` () =
    let relativePath = Path.Combine(Path.GetDirectoryName(projectPath), ".", Path.GetFileName(projectPath))
    let encoded = ProjectEvaluation.encodeResponse relativePath Array.empty
    let projects = ProjectEvaluation.decodeResponse projectPath encoded
    Assert.Empty(projects)
    use json = JsonDocument.Parse(encoded)
    Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32())
    Assert.Equal(Path.GetFullPath(projectPath), json.RootElement.GetProperty("projectPath").GetString())

[<Theory>]
[<InlineData("")>]
[<InlineData(" ")>]
[<InlineData("null")>]
[<InlineData("[]")>]
[<InlineData("not json")>]
[<InlineData("{}")>]
let ``helper decoder rejects missing or non-envelope results`` (text: string) =
    Assert.ThrowsAny<Exception>(fun () -> ProjectEvaluation.decodeResponse projectPath text |> ignore)
    |> ignore

[<Theory>]
[<InlineData("version", "2")>]
[<InlineData("version", "null")>]
[<InlineData("version", "\"1\"")>]
[<InlineData("projectPath", "null")>]
[<InlineData("projectPath", "\"\"")>]
[<InlineData("projects", "null")>]
[<InlineData("projects", "{}")>]
[<InlineData("projects", "[null]")>]
let ``helper decoder rejects invalid field values`` (field: string) (replacement: string) =
    let envelope = JsonNode.Parse(emptyResponse ()).AsObject()
    envelope[field] <- JsonNode.Parse(replacement)

    Assert.ThrowsAny<Exception>(fun () ->
        ProjectEvaluation.decodeResponse projectPath (envelope.ToJsonString()) |> ignore)
    |> ignore

[<Fact>]
let ``helper decoder rejects duplicate unknown and cross-project envelopes`` () =
    let encodedPath = JsonSerializer.Serialize(projectPath)

    let responses =
        [ "{\"version\":1,\"version\":1,\"projectPath\":" + encodedPath + ",\"projects\":[]}"
          "{\"version\":1,\"extra\":true,\"projectPath\":" + encodedPath + ",\"projects\":[]}"
          ProjectEvaluation.encodeResponse (projectPath + ".other.fsproj") Array.empty ]

    for response in responses do
        Assert.Throws<InvalidOperationException>(fun () ->
            ProjectEvaluation.decodeResponse projectPath response |> ignore)
        |> ignore

[<Fact>]
let ``helper decoder bounds even otherwise-valid JSON responses`` () =
    let response = (emptyResponse ()).PadRight(16 * 1024 * 1024 + 1, ' ')

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            ProjectEvaluation.decodeResponse projectPath response |> ignore)

    Assert.Contains("oversized", error.Message)

[<Fact>]
let ``expired evaluation rejects work before invoking the helper runner`` () : Task =
    task {
        let expected = TimeoutException("no active waiters before launch")
        let mutable started = false

        let operation =
            ProjectEvaluation.loadProjectsWithRunner
                projectPath
                (fun () -> raise expected)
                (fun _ ->
                    started <- true
                    Task.FromResult(successfulOutput (emptyResponse ())))

        let! error = Assert.ThrowsAsync<TimeoutException>(fun () -> operation :> Task)
        Assert.Same(expected, error)
        Assert.False(started)
    }

[<Fact>]
let ``evaluation remains alive while a follower still needs its helper`` () : Task =
    task {
        let followerObserved = signal ()
        let finished = TaskCompletionSource<ProcessRunner.ProcessOutput>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable firstCallerExpired = 0
        let mutable cancellationObserved = 0

        let ensureNeeded () =
            if Volatile.Read(&firstCallerExpired) = 1 then
                // The first caller has expired, but the shared flight has a live
                // follower. Aggregate liveness therefore still permits progress.
                followerObserved.TrySetResult(()) |> ignore

        let startHelper (token: CancellationToken) =
            task {
                use _registration = token.Register(fun () -> Interlocked.Exchange(&cancellationObserved, 1) |> ignore)
                return! finished.Task
            }

        let operation = ProjectEvaluation.loadProjectsWithRunner projectPath ensureNeeded startHelper

        try
            Volatile.Write(&firstCallerExpired, 1)
            do! followerObserved.Task.WaitAsync(TimeSpan.FromSeconds(15.0))
            Assert.Equal(0, Volatile.Read(&cancellationObserved))
            Assert.False(operation.IsCompleted)
            finished.TrySetResult(successfulOutput (emptyResponse ())) |> ignore
            let! projects = operation
            Assert.Empty(projects)
        finally
            finished.TrySetResult(successfulOutput (emptyResponse ())) |> ignore
    }

[<Fact>]
let ``last waiter expiry retains ownership until helper cleanup has completed`` () : Task =
    task {
        let cleanupStarted = signal ()
        let allowCleanup = signal ()
        let expected = TimeoutException("the exact flight has no active callers")
        let mutable noWaiters = 0

        let ensureNeeded () =
            if Volatile.Read(&noWaiters) = 1 then
                raise expected

        let startHelper (token: CancellationToken) : Task<ProcessRunner.ProcessOutput> =
            task {
                use _registration = token.Register(fun () -> cleanupStarted.TrySetResult(()) |> ignore)
                do! allowCleanup.Task
                return raise (OperationCanceledException(token))
            }

        let operation = ProjectEvaluation.loadProjectsWithRunner projectPath ensureNeeded startHelper

        try
            Volatile.Write(&noWaiters, 1)
            do! cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(15.0))
            Assert.False(operation.IsCompleted, "Evaluation released ownership before its helper drained.")
            allowCleanup.TrySetResult(()) |> ignore
            let! error = Assert.ThrowsAsync<TimeoutException>(fun () -> operation :> Task)
            Assert.Same(expected, error)
        finally
            allowCleanup.TrySetResult(()) |> ignore
    }

[<Theory>]
[<InlineData(0, true)>]
[<InlineData(17, false)>]
let ``helper process failures cannot become usable project options`` (exitCode: int) (truncated: bool) : Task =
    task {
        let output =
            { successfulOutput (emptyResponse ()) with
                ExitCode = exitCode
                StandardOutputTruncated = truncated
                StandardError = "controlled child failure" }

        let operation =
            ProjectEvaluation.loadProjectsWithRunner projectPath ignore (fun _ -> Task.FromResult(output))

        let! _ = Assert.ThrowsAsync<InvalidOperationException>(fun () -> operation :> Task)
        return ()
    }

[<Fact>]
let ``actual helper preserves FSharp and CLR references compile inputs and imported settings`` () : Task =
    task {
        let fixture = Directory.CreateTempSubdirectory("fslangmcp_evaluation_graph_")
        let root = fixture.FullName
        let fsLibrary = Path.Combine(root, "FSharp Library")
        let csLibrary = Path.Combine(root, "CLR Library")
        Directory.CreateDirectory(fsLibrary) |> ignore
        Directory.CreateDirectory(csLibrary) |> ignore
        let project = Path.Combine(root, "App.fsproj")

        try
            File.WriteAllText(
                Path.Combine(fsLibrary, "Shared.fsproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Shared.fs\" /></ItemGroup></Project>"
            )

            File.WriteAllText(Path.Combine(fsLibrary, "Shared.fs"), "namespace Shared\nmodule Symbols =\n    let answer = 41\n")

            File.WriteAllText(
                Path.Combine(csLibrary, "ClrLib.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            )

            File.WriteAllText(
                Path.Combine(csLibrary, "Values.cs"),
                "namespace ClrLib; public static class Values { public static int One => 1; }"
            )

            File.WriteAllText(
                Path.Combine(root, "Custom.props"),
                "<Project><PropertyGroup><DefineConstants>HELPER_IMPORTED</DefineConstants></PropertyGroup></Project>"
            )

            File.WriteAllText(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Import Project=\"Custom.props\" /><ItemGroup><Compile Include=\"App.fs\" /><ProjectReference Include=\"FSharp Library/Shared.fsproj\" /><ProjectReference Include=\"CLR Library/ClrLib.csproj\" /></ItemGroup></Project>"
            )

            File.WriteAllText(Path.Combine(root, "App.fs"), "module App\nlet value = Shared.Symbols.answer + ClrLib.Values.One\n")

            let! built =
                ProcessRunner.runAsync
                    (ProcessRunner.resolveDotnetHost ())
                    [ "build"; project; "--nologo"; "-m:1" ]
                    (TimeSpan.FromSeconds(90.0))
                    CancellationToken.None

            Assert.True(built.ExitCode = 0, $"Fixture build failed: {built.StandardError}\n{built.StandardOutput}")

            let! projects = ProjectEvaluation.loadProjectsAsync project ignore
            Assert.Equal(3, projects.Length)
            let rootProject = projects |> List.find (fun row -> row.ProjectFileName = project)
            let mapped = FCS.mapToFSharpProjectOptions rootProject projects
            Assert.Contains(Path.Combine(root, "App.fs"), mapped.SourceFiles)
            Assert.Contains("--define:HELPER_IMPORTED", mapped.OtherOptions)
            Assert.Equal(2, mapped.ReferencedProjects.Length)

            Assert.Contains(mapped.ReferencedProjects, fun reference ->
                match reference with
                | FSharpReferencedProject.FSharpReference _ -> true
                | _ -> false)

            Assert.Contains(mapped.ReferencedProjects, fun reference ->
                match reference with
                | FSharpReferencedProject.PEReference _ -> true
                | _ -> false)

            let encoded = ProjectEvaluation.encodeResponse project (List.toArray projects)
            let decoded = ProjectEvaluation.decodeResponse project encoded
            // Never print raw evaluated properties: they can contain environment
            // values. Structural equality checks completeness without leaking them.
            Assert.True((projects = decoded), "Evaluated data changed on the private protocol roundtrip.")

            let checker = FSharpChecker.Create(keepAssemblyContents = true)
            let! checkedProject = checker.ParseAndCheckProject(mapped) |> Async.StartAsTask
            let errors = checkedProject.Diagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)
            Assert.True(errors.Length = 0, String.Join("\n", errors |> Array.map _.Message))
        finally
            Directory.Delete(root, true)
    }

[<Fact>]
let ``actual helper and its descendant exit before cancelled evaluation returns`` () : Task =
    task {
        let fixture = Directory.CreateTempSubdirectory("fslangmcp_evaluation_cancel_")
        let root = fixture.FullName
        let project = Path.Combine(root, "Blocked.fsproj")
        let marker = Path.Combine(root, "started.pids")
        let release = Path.Combine(root, "release")
        let childScript = Path.Combine(root, "child.fsx")
        let expiredError = TimeoutException("all callers expired after the evaluation started")
        let mutable expired = 0
        let ownedProcesses = ResizeArray<Process * DateTime>()
        let escaped value = System.Security.SecurityElement.Escape(value)

        try
            File.WriteAllText(Path.Combine(root, "Source.fs"), "module Source\nlet value = 1\n")
            File.WriteAllText(childScript, "System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite)\n")

            File.WriteAllText(
                project,
                $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Source.fs" /></ItemGroup>
  <UsingTask TaskName="HoldEvaluation" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
    <ParameterGroup>
      <MarkerPath ParameterType="System.String" Required="true" />
      <ReleasePath ParameterType="System.String" Required="true" />
      <ChildScript ParameterType="System.String" Required="true" />
      <DotnetHost ParameterType="System.String" Required="true" />
    </ParameterGroup>
    <Task>
      <Code Type="Fragment" Language="cs"><![CDATA[
        var start = new System.Diagnostics.ProcessStartInfo(DotnetHost);
        start.UseShellExecute = false;
        start.Arguments = "fsi --quiet --exec \"" + ChildScript.Replace("\"", "\\\"") + "\"";
        using (var child = System.Diagnostics.Process.Start(start)) {{
          System.IO.File.WriteAllText(MarkerPath + ".tmp", System.Diagnostics.Process.GetCurrentProcess().Id + "\n" + child.Id);
          System.IO.File.Move(MarkerPath + ".tmp", MarkerPath);
          while (!System.IO.File.Exists(ReleasePath)) System.Threading.Thread.Sleep(10);
          if (!child.HasExited) child.Kill();
          child.WaitForExit();
        }}
      ]]></Code>
    </Task>
  </UsingTask>
  <Target Name="HoldBeforeCompile" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
    <HoldEvaluation MarkerPath="{escaped marker}" ReleasePath="{escaped release}" ChildScript="{escaped childScript}" DotnetHost="{escaped (ProcessRunner.resolveDotnetHost ())}" />
  </Target>
</Project>"""
            )

            let! restored =
                ProcessRunner.runAsync
                    (ProcessRunner.resolveDotnetHost ())
                    [ "restore"; project; "--nologo"; "-m:1" ]
                    (TimeSpan.FromSeconds(90.0))
                    CancellationToken.None

            Assert.True(restored.ExitCode = 0, $"Fixture restore failed: {restored.StandardError}\n{restored.StandardOutput}")

            let operation =
                ProjectEvaluation.loadProjectsAsync project (fun () ->
                    if Volatile.Read(&expired) = 1 then
                        raise expiredError)

            let awaitMarker =
                task {
                    while not (File.Exists(marker)) do
                        if operation.IsCompleted then
                            try
                                let! _ = operation
                                failwith "The deliberately blocked evaluation completed without its start marker."
                            with evaluationError ->
                                // Preserve an actionable compiler diagnostic if a
                                // future SDK breaks this inline-task fixture.
                                File.WriteAllText(release, "unblock fixture diagnostics")

                                let! diagnostic =
                                    ProcessRunner.runAsync
                                        (ProcessRunner.resolveDotnetHost ())
                                        [ "msbuild"; project; "-nologo"; "-t:HoldBeforeCompile"; "-p:DesignTimeBuild=true"; "-v:minimal" ]
                                        (TimeSpan.FromSeconds(30.0))
                                        CancellationToken.None

                                failwith $"{evaluationError.Message}\nFixture diagnostics: {diagnostic.StandardError}\n{diagnostic.StandardOutput}"

                        do! Task.Delay(25)
                }

            do! awaitMarker.WaitAsync(TimeSpan.FromSeconds(60.0))

            for pid in File.ReadAllLines(marker) |> Array.map Int32.Parse do
                let childProc = Process.GetProcessById(pid)

                if OperatingSystem.IsWindows() then
                    // StartTime/HasExited alone use temporary Windows handles.
                    // Open the retained handle while alive, before cancellation.
                    Assert.False(childProc.SafeHandle.IsInvalid)

                let owned = childProc, childProc.StartTime
                ownedProcesses.Add(owned)
                Assert.True(isOwnedProcessLive owned, "The test process must be alive before cancellation.")

            Assert.Equal(2, ownedProcesses.Count)
            Volatile.Write(&expired, 1)
            let! error = Assert.ThrowsAsync<TimeoutException>(fun () -> operation.WaitAsync(TimeSpan.FromSeconds(20.0)) :> Task)
            Assert.Same(expiredError, error)

            for childProc, startedAt in ownedProcesses do
                Assert.False(
                    isOwnedProcessLive (childProc, startedAt),
                    $"Owned process {childProc.Id} survived evaluation cancellation."
                )
        finally
            // Failure cleanup is restricted to handles recorded by this fixture.
            // Releasing the task also lets the helper finish if cancellation regressed.
            File.WriteAllText(release, "release")

            for childProc, startedAt in ownedProcesses do
                try
                    if isOwnedProcessLive (childProc, startedAt) then
                        childProc.Kill(true)
                finally
                    childProc.Dispose()

            Directory.Delete(root, true)
    }
