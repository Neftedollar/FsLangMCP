module internal FsLangMcp.ProjectEvaluation

open System
open System.Buffers
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Ionide.ProjInfo

// This private CLI protocol deliberately transports ProjInfo's data, not FCS's
// executable PE-reader delegates. The existing mapper still runs in the parent.
[<Literal>]
let InternalArgument = "--internal-project-evaluation-v1"

[<Literal>]
let private protocolVersion = 1

[<Literal>]
let private maximumResponseCharacters = 16 * 1024 * 1024

let private responseTooLarge () =
    invalidOp "The evaluated project-options response exceeded the 16 Mi-character protocol limit."

// The default JSON encoder emits ASCII, so wire bytes and UTF-16 characters
// coincide. Check the escaped size before the JSON writer rents token buffers.
let private ensureStringFits (writer: Utf8JsonWriter) (value: string) =
    let mutable remaining = int64 maximumResponseCharacters - writer.BytesCommitted - int64 writer.BytesPending - 2L

    if int64 value.Length > remaining then
        responseTooLarge ()

    for character in value do
        let size =
            match character with
            | '\\' | '\b' | '\f' | '\n' | '\r' | '\t' -> 2L
            | _ when Char.IsSurrogate(character) -> 6L
            | _ when JavaScriptEncoder.Default.WillEncode(int character) -> 6L
            | _ -> 1L

        remaining <- remaining - size

        if remaining < 0L then
            responseTooLarge ()

type private BoundedStringConverter() =
    inherit JsonConverter<string>()

    override _.Read(reader, _, _) = reader.GetString()

    override _.Write(writer, value, _) =
        ensureStringFits writer value

        if value.Length = 0 then
            writer.WriteStringValue("")
        else
            // Segments also bound the writer's escaping/transcoding scratch space.
            // .NET carries a split surrogate pair across segment boundaries.
            let mutable offset = 0

            while offset < value.Length do
                let count = min 4096 (value.Length - offset)
                writer.WriteStringValueSegment(value.AsSpan(offset, count), offset + count = value.Length)
                writer.Flush()
                offset <- offset + count

type private BoundedResponseBuffer() =
    let output = new MemoryStream()
    let mutable scratch = Array.empty<byte>

    let prepare sizeHint =
        // Every data string (including map keys) is segmented to 4096 chars.
        // Account for escaping (6x) and the writer's UTF-8 reservation (3x).
        let maximumScratchBytes = 4096 * 6 * 3 + 1024
        let required = max 8192 sizeHint

        if required > maximumScratchBytes then
            responseTooLarge ()

        if scratch.Length < required then
            scratch <- Array.zeroCreate required

    member _.Response = Encoding.UTF8.GetString(output.GetBuffer(), 0, int output.Length)

    interface IBufferWriter<byte> with
        member _.Advance(count) =
            if count < 0 || count > scratch.Length then
                invalidArg (nameof count) "The JSON writer advanced outside its output buffer."

            if int64 count > int64 maximumResponseCharacters - output.Length then
                responseTooLarge ()

            output.Write(scratch.AsSpan(0, count))

        member _.GetMemory(sizeHint) =
            prepare sizeHint
            scratch.AsMemory()

        member _.GetSpan(sizeHint) =
            prepare sizeHint
            scratch.AsSpan()

    interface IDisposable with
        member _.Dispose() = output.Dispose()

let private jsonOptions =
    let options = JsonSerializerOptions(MaxDepth = 64)
    options.Converters.Add(BoundedStringConverter())
    // Object-form map keys bypass custom string converters in the pinned F#
    // serializer. Pair arrays preserve their data while applying the same bounded
    // conversion to keys and values; this is an unreleased private wire format.
    options.Converters.Add(JsonFSharpConverter(JsonFSharpOptions.Default().WithMapFormat(MapFormat.ArrayOfPairs)))
    options

let private samePath left right =
    let comparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison)

let internal encodeResponse projectPath (projects: Types.ProjectOptions array) =
    use output = new BoundedResponseBuffer()
    use writer = new Utf8JsonWriter(output, JsonWriterOptions(MaxDepth = 64))
    writer.WriteStartObject()
    writer.WriteNumber("version", protocolVersion)
    writer.WritePropertyName("projectPath")
    JsonSerializer.Serialize(writer, Path.GetFullPath(projectPath), jsonOptions)
    writer.WritePropertyName("projects")
    JsonSerializer.Serialize(writer, projects, jsonOptions)
    writer.WriteEndObject()
    writer.Flush()
    output.Response

let internal decodeResponse projectPath (text: string) =
    if String.IsNullOrWhiteSpace(text) || text.Length > maximumResponseCharacters then
        invalidOp "The project-evaluation helper returned an empty or oversized response."

    use document = JsonDocument.Parse(text, JsonDocumentOptions(MaxDepth = 64))
    let root = document.RootElement

    if root.ValueKind <> JsonValueKind.Object then
        invalidOp "The project-evaluation helper returned an invalid envelope."

    let names = root.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Seq.toArray

    if names.Length <> 3 || Set.ofArray names <> set [ "version"; "projectPath"; "projects" ] then
        invalidOp "The project-evaluation helper returned an unknown or duplicate envelope field."

    if root.GetProperty("version").GetInt32() <> protocolVersion then
        invalidOp "The project-evaluation helper protocol version does not match this host."

    let returnedPath = root.GetProperty("projectPath").GetString()

    if String.IsNullOrWhiteSpace(returnedPath) || not (samePath projectPath returnedPath) then
        invalidOp "The project-evaluation helper returned another project."

    let rows = root.GetProperty("projects")

    if rows.ValueKind <> JsonValueKind.Array then
        invalidOp "The project-evaluation helper returned invalid project rows."

    let projects = rows.Deserialize<Types.ProjectOptions array>(jsonOptions)

    if isNull projects || (projects |> Array.exists (fun project -> isNull (box project))) then
        invalidOp "The project-evaluation helper returned null project rows."

    if projects.Length > 0 && not (samePath projectPath projects[0].ProjectFileName) then
        invalidOp "The project-evaluation helper returned another root project."

    projects |> Array.toList

// Only the short-lived child executes this function. It never starts the MCP
// host or FSAC. Redirect incidental build output away from the protocol stream.
let internal runHelper projectPath =
    let protocolOutput = Console.Out
    Console.SetOut(Console.Error)

    try
        try
            let fullPath = Path.GetFullPath(projectPath)
            let directory = Path.GetDirectoryName(fullPath)
            SdkPreflight.ensure [ directory ]
            InstallationHealth.ensureCurrent ()
            let toolsPath = Init.init (DirectoryInfo(directory)) None
            let projects = WorkspaceLoader.Create(toolsPath, []).LoadProjects([ fullPath ]) |> Seq.toArray

            if projects.Length = 0 then
                invalidOp "MSBuild did not return evaluated settings for the requested project."

            let response = encodeResponse fullPath projects
            protocolOutput.Write(response)
            protocolOutput.Flush()
            0
        with ex ->
            Console.Error.WriteLine($"Project evaluation failed: {ex.Message}")
            1
    finally
        Console.SetOut(protocolOutput)

let private evaluationTimeout () =
    match Environment.GetEnvironmentVariable("FSLANGMCP_PROJ_INFO_TIMEOUT_MS") |> Int32.TryParse with
    | true, milliseconds when milliseconds > 0 -> TimeSpan.FromMilliseconds(float milliseconds)
    | _ -> TimeSpan.FromMinutes(2.0)

// A per-call runner seam makes ownership/cancellation testable without mutating
// global state or relying on the speed of SDK/MSBuild startup.
let internal loadProjectsWithRunner
    (projectPath: string)
    (ensureCanContinue: unit -> unit)
    (startHelper: CancellationToken -> Task<ProcessRunner.ProcessOutput>)
    : Task<Types.ProjectOptions list> =
    task {
        ensureCanContinue ()
        use cancellation = new CancellationTokenSource()
        let helper = startHelper cancellation.Token

        try
            // The callback represents ALL waiters of the exact retained flight.
            // Expiring the first caller must not kill work needed by a follower.
            // Once nobody needs it, ProcessRunner kills and drains the child tree
            // before this actual worker releases its admission slot.
            while not helper.IsCompleted do
                let! _ = Task.WhenAny(helper :> Task, Task.Delay(25))
                ensureCanContinue ()

            let! output = helper
            ensureCanContinue ()

            if output.StandardOutputTruncated then
                invalidOp "The project-evaluation helper response exceeded its size limit."

            if output.ExitCode <> 0 then
                let diagnostic =
                    if output.StandardError.Length > 4096 then
                        output.StandardError.Substring(0, 4096)
                    else
                        output.StandardError

                invalidOp $"The project-evaluation helper exited with code {output.ExitCode}: {diagnostic}"

            let projects = decodeResponse projectPath output.StandardOutput
            ensureCanContinue ()
            return projects
        with ex ->
            cancellation.Cancel()

            try
                do! helper :> Task
            with _ ->
                // Observe the helper task after bounded process-tree cleanup;
                // preserve the original caller/decode/process failure below.
                ()

            return raise ex
    }

let internal loadProjectsAsync (projectPath: string) (ensureCanContinue: unit -> unit) =
    let startHelper cancellationToken =
        let assemblyPath = typeof<ProcessRunner.ProcessOutput>.Assembly.Location

        if String.IsNullOrWhiteSpace(assemblyPath) || not (File.Exists(assemblyPath)) then
            invalidOp "Unable to locate the project-evaluation helper assembly."

        ProcessRunner.runAsyncWithOutputLimit
            (ProcessRunner.resolveDotnetHost ())
            [ assemblyPath; InternalArgument; Path.GetFullPath(projectPath) ]
            (evaluationTimeout ())
            cancellationToken
            maximumResponseCharacters

    loadProjectsWithRunner projectPath ensureCanContinue startHelper
