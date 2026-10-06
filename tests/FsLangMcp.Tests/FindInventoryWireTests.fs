module FsLangMcp.Tests.FindInventoryWireTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit

let private executablePath () =
    Path.Combine(
        AppContext.BaseDirectory,
        if OperatingSystem.IsWindows() then "FsLangMcp.exe" else "FsLangMcp"
    )

let private startServer () =
    let startInfo = ProcessStartInfo(executablePath ())
    startInfo.WorkingDirectory <- Path.GetTempPath()
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardInput <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.Environment.Remove("FSA_PROJECT_PATH") |> ignore
    startInfo.Environment.Remove("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE") |> ignore
    startInfo.Environment.Remove("DOTNET_USE_POLLING_FILE_WATCHER") |> ignore
    Process.Start(startInfo)

let private sendRequest (server: Process) (request: string) =
    task {
        do! server.StandardInput.WriteLineAsync(request)
        do! server.StandardInput.FlushAsync()

        let! response =
            server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60.0))

        Assert.False(String.IsNullOrWhiteSpace(response), "Server returned EOF before the MCP response.")
        use document = JsonDocument.Parse(response)
        return document.RootElement.Clone()
    }

let private tryProperty (name: string) (element: JsonElement) =
    element.EnumerateObject()
    |> Seq.tryPick (fun property ->
        if property.NameEquals(name) then Some property.Value else None)

type private ToolCallResult =
    { IsError: bool
      Text: string }

let private callFind (server: Process) (id: int) (arguments: string) =
    task {
        use argumentDocument = JsonDocument.Parse(arguments)
        let request =
            JsonSerializer.Serialize(
                {| jsonrpc = "2.0"
                   id = id
                   method = "tools/call"
                   ``params`` = {| name = "find"; arguments = argumentDocument.RootElement |} |})

        let! response = sendRequest server request
        Assert.Equal(id, response.GetProperty("id").GetInt32())
        let result = response.GetProperty("result")

        let isError =
            result
            |> tryProperty "isError"
            |> Option.map _.GetBoolean()
            |> Option.defaultValue false

        let content = result.GetProperty("content").EnumerateArray() |> Seq.toArray
        Assert.NotEmpty(content)
        Assert.Equal("text", content[0].GetProperty("type").GetString())

        let text =
            content[0].GetProperty("text").GetString()
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "tools/call returned null text content.")

        return { IsError = isError; Text = text }
    }

let private parseBody (call: ToolCallResult) =
    Assert.False(call.IsError, call.Text)
    use document = JsonDocument.Parse(call.Text)
    document.RootElement.Clone()

let private phaseNames =
    [| "admission"
       "positionResolution"
       "targetDiscovery"
       "projectOptions"
       "snapshot"
       "fcsSweep"
       "classification"
       "fsacFallback"
       "responseConstruction"
       "unattributed" |]

let private assertCompleteTelemetry (body: JsonElement) =
    let timings = body.GetProperty("phaseTimingsMs")
    let properties = timings.EnumerateObject() |> Seq.toArray
    Assert.Equal(phaseNames.Length, properties.Length)

    let actualNames = properties |> Array.map _.Name |> Set.ofArray
    Assert.Equal<Set<string>>(Set.ofArray phaseNames, actualNames)

    let total =
        properties
        |> Array.sumBy (fun property ->
            let milliseconds = property.Value.GetInt32()
            Assert.True(milliseconds >= 0, property.Name)
            milliseconds)

    Assert.Equal(body.GetProperty("elapsedMs").GetInt32(), total)

    let cacheState = body.GetProperty("cacheState")

    for cacheName in [ "projectOptions"; "projectUses" ] do
        let cache = cacheState.GetProperty(cacheName)
        Assert.False(String.IsNullOrWhiteSpace(cache.GetProperty("state").GetString()))
        Assert.True(cache.GetProperty("hits").GetInt32() >= 0)
        Assert.True(cache.GetProperty("misses").GetInt32() >= 0)
        Assert.True(cache.GetProperty("incomplete").GetInt32() >= 0)

let private assertInvalidInventoryArgs (body: JsonElement) =
    Assert.Equal("invalid_args", body.GetProperty("status").GetString())
    Assert.Equal("invalid_find_inventory_args", body.GetProperty("errorKind").GetString())
    assertCompleteTelemetry body

let private assertSchemaType (expected: string) (schema: JsonElement) =
    let declaredType = schema.GetProperty("type")
    let types =
        match declaredType.ValueKind with
        | JsonValueKind.String -> [| declaredType.GetString() |]
        | JsonValueKind.Array -> declaredType.EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
        | _ -> failwith $"Unexpected schema type declaration: {declaredType}"
    Assert.Contains<string>(expected, types)
    Assert.All(types, fun actual -> Assert.True(actual = expected || actual = "null", $"Unexpected schema type: {actual}"))

[<Fact>]
let ``public find wire contract advertises inventory and rejects unsafe shapes before work`` () : Task =
    task {
        use server = startServer ()

        try
            let initialize =
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"find-inventory-wire-test","version":"1.0"}}}"""

            let! initialized = sendRequest server initialize
            Assert.Equal(1, initialized.GetProperty("id").GetInt32())
            Assert.Equal("fsharp-fsautocomplete", initialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString())

            do!
                server.StandardInput.WriteLineAsync(
                    """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}"""
                )

            do! server.StandardInput.FlushAsync()

            let! listed =
                sendRequest server """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""

            let findTool =
                listed.GetProperty("result").GetProperty("tools").EnumerateArray()
                |> Seq.find (fun tool -> tool.GetProperty("name").GetString() = "find")

            let schema = findTool.GetProperty("inputSchema")
            let schemaProperties = schema.GetProperty("properties")
            assertSchemaType "string" (schemaProperties.GetProperty("query"))
            assertSchemaType "array" (schemaProperties.GetProperty("queries"))
            assertSchemaType "string" (schemaProperties.GetProperty("queries").GetProperty("items"))
            assertSchemaType "boolean" (schemaProperties.GetProperty("countsOnly"))

            match tryProperty "required" schema with
            | Some required ->
                Assert.DoesNotContain(
                    "query",
                    required.EnumerateArray()
                    |> Seq.map _.GetString()
                    |> Seq.choose Option.ofObj
                )
            | None -> ()

            let! classicCall =
                callFind
                    server
                    3
                    """{"query":"A","projectPath":"/not-existing/Context.fsproj","timeoutMs":0}"""

            let classic = parseBody classicCall
            Assert.Equal("unknown", classic.GetProperty("status").GetString())
            Assert.Equal("fcs_admission_timeout", classic.GetProperty("errorKind").GetString())
            Assert.False(classic.GetProperty("resultSetComplete").GetBoolean())
            Assert.Equal(0, classic.GetProperty("sites").GetArrayLength())
            assertCompleteTelemetry classic

            let! inventoryCall =
                callFind
                    server
                    4
                    """{"queries":["A","B"],"countsOnly":true,"projectPath":"/not-existing/Context.fsproj","timeoutMs":0}"""

            let inventory = parseBody inventoryCall
            Assert.Equal("unknown", inventory.GetProperty("status").GetString())
            Assert.Equal("fcs_admission_timeout", inventory.GetProperty("errorKind").GetString())
            Assert.True(inventory.GetProperty("countsOnly").GetBoolean())
            Assert.False(inventory.GetProperty("countsReturned").GetBoolean())
            Assert.False(inventory.GetProperty("countsComplete").GetBoolean())
            Assert.Equal(2, inventory.GetProperty("queriesRequested").GetInt32())
            Assert.Equal(0, inventory.GetProperty("queriesReturned").GetInt32())
            Assert.True(tryProperty "queries" inventory |> Option.isNone)
            assertCompleteTelemetry inventory

            let! bothAlternatives =
                callFind
                    server
                    5
                    """{"query":"A","queries":["B"],"projectPath":"/not-existing/Context.fsproj","timeoutMs":0}"""

            assertInvalidInventoryArgs (parseBody bothAlternatives)

            let! snippetsRequested =
                callFind
                    server
                    6
                    """{"queries":["A"],"countsOnly":false,"projectPath":"/not-existing/Context.fsproj","timeoutMs":0}"""

            assertInvalidInventoryArgs (parseBody snippetsRequested)

            let! nullName = callFind server 12 """{"queries":[null]}"""
            assertInvalidInventoryArgs (parseBody nullName)

            for id, malformed in
                [ 7, """{"queries":"A"}"""
                  8, """{"queries":["A",1]}""" ] do
                let! rejected = callFind server id malformed
                Assert.True(rejected.IsError, rejected.Text)
                Assert.Equal("Invalid tool arguments.", rejected.Text)

            let! invalidTimeout =
                callFind
                    server
                    9
                    """{"query":"A","projectPath":"/not-existing/Context.fsproj","timeoutMs":-1}"""

            let invalidTimeoutBody = parseBody invalidTimeout
            Assert.Equal("invalid_args", invalidTimeoutBody.GetProperty("status").GetString())
            Assert.Equal("invalid_timeout", invalidTimeoutBody.GetProperty("errorKind").GetString())
            Assert.Equal(-1, invalidTimeoutBody.GetProperty("timeoutMs").GetInt32())
            assertCompleteTelemetry invalidTimeoutBody

            // Exercise the actual public adapter + dispatcher + FCS backend, not
            // only schema/error paths or an in-process bridge.
            use fixture = new FsLangMcp.Tests.FindTests.FindFixture()
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let batchArguments = JsonSerializer.Serialize(
                {| queries = [| "TraderRole"; "MissingWireInventoryName" |]
                   scope = "project"; projectPath = fixture.DomainFsproj; timeoutMs = 45_000 |})
            let! batchCall = callFind server 10 batchArguments
            let batch = parseBody batchCall
            Assert.True(batch.GetProperty("countsComplete").GetBoolean(), batchCall.Text)
            Assert.Equal(2, batch.GetProperty("queries").GetArrayLength())
            Assert.True((batch.GetProperty("queries")[0]).GetProperty("uniqueSites").GetInt32() > 0)
            Assert.Equal("not_found", (batch.GetProperty("queries")[1]).GetProperty("outcome").GetString())
            assertCompleteTelemetry batch

            let! warmCall = callFind server 11 batchArguments
            let warm = parseBody warmCall
            Assert.True(warm.GetProperty("countsComplete").GetBoolean(), warmCall.Text)
            Assert.Equal(1, warm.GetProperty("cacheState").GetProperty("projectUses").GetProperty("hits").GetInt32())
            Assert.Equal(0, warm.GetProperty("cacheState").GetProperty("projectUses").GetProperty("misses").GetInt32())
            Assert.Equal(batch.GetProperty("queries").GetRawText(), warm.GetProperty("queries").GetRawText())
        finally
            try
                server.StandardInput.Close()
            with :? IOException ->
                ()

            if not server.HasExited && not (server.WaitForExit(1000)) then
                server.Kill(true)
                server.WaitForExit()
    }
