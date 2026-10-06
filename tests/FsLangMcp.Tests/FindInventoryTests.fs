module FsLangMcp.Tests.FindInventoryTests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.FindInventory

let private file name = Path.Combine(Path.GetTempPath(), "fslang-inventory-tests", name)

let private site name line isDefinition =
    { File = file "Shared.fs"
      StartLine = line
      StartColumn = 0
      EndLine = line
      EndColumn = 4
      IsDefinition = isDefinition
      SymbolId = name
      FullName = name }

let private collector queries definitionsOnly includeDeclaration scope =
    Collector(queries, true, definitionsOnly, includeDeclaration, scope)

let private queryRow (response: JsonObject) (index: int) = response["queries"][index]
let private number (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private boolean (node: JsonNode) (key: string) = node[key].GetValue<bool>()
let private text (node: JsonNode) (key: string) = node[key].GetValue<string>()

[<Fact>]
let ``physical duplicates are counted once before project and global totals`` () =
    let inventory = collector [| "Name" |] false true None
    let definition = site "Domain.Name" 1 true
    let reference = site "Domain.Name" 2 false

    for observed in [ definition; reference; reference; definition ] do
        inventory.ObserveSite(0, 0, observed)

    let row = queryRow (inventory.ToJson true) 0
    Assert.Equal(1, number row "definitions")
    Assert.Equal(1, number row "references")
    Assert.Equal(2, number row "uniqueSites")
    Assert.Equal(1, number row "matchedSymbolCount")
    Assert.Equal(2, number (row["perProject"][0]) "uniqueSites")

[<Fact>]
let ``file scope filters before counts and symbol identities`` () =
    let inventory = collector [| "Name" |] false true (Some(file "Wanted.fs"))
    inventory.ObserveSite(0, 0, site "Excluded.Name" 1 true)
    inventory.ObserveSite(0, 0, { site "Included.Name" 2 false with File = file "Wanted.fs" })
    let row = queryRow (inventory.ToJson true) 0
    Assert.Equal(0, number row "definitions")
    Assert.Equal(1, number row "references")
    Assert.Equal(1, number row "matchedSymbolCount")
    Assert.Equal("Included.Name", text (row["symbols"][0]) "fullName")

[<Fact>]
let ``overlapping query memberships have independent sets`` () =
    let inventory = collector [| "Name"; "Domain.Name" |] false true None
    let observed = site "Domain.Name" 1 false
    inventory.ObserveSite(0, 0, observed)
    inventory.ObserveSite(1, 0, observed)
    let response = inventory.ToJson true
    Assert.Equal("per_query", text response "aggregation")

    for index in 0..1 do
        Assert.Equal(1, number (queryRow response index) "references")

[<Fact>]
let ``symbol mode can exclude declarations and definition mode still includes them`` () =
    for definitionsOnly in [ false; true ] do
        let inventory = collector [| "Name" |] definitionsOnly false None
        inventory.ObserveSite(0, 0, site "Domain.Name" 1 true)
        inventory.ObserveSite(0, 0, site "Domain.Name" 2 false)
        let row = queryRow (inventory.ToJson true) 0
        Assert.Equal((if definitionsOnly then 1 else 0), number row "definitions")
        Assert.Equal((if definitionsOnly then 0 else 1), number row "references")

[<Fact>]
let ``linked ranges count once globally and once in each distinct project index`` () =
    // The caller's ledger can contain /left/Same.fsproj and /right/Same.fsproj;
    // only their stable indices cross this boundary, never their shared basename.
    let inventory = collector [| "Name" |] false true None
    let observed = site "Domain.Name" 3 false

    for projectIndex in [ 0; 1; 0; 1 ] do
        inventory.ObserveSite(0, projectIndex, observed)

    let row = queryRow (inventory.ToJson true) 0
    Assert.Equal(1, number row "uniqueSites")
    let projects = row["perProject"] :?> JsonArray
    Assert.Equal(2, projects.Count)
    Assert.Equal<int list>([ 0; 1 ], projects |> Seq.map (fun project -> number project "projectIndex") |> Seq.toList)
    Assert.Equal(2, projects |> Seq.sumBy (fun project -> number project "references"))

[<Fact>]
let ``linked classification conflicts preserve both categories and one physical site`` () =
    for definitionFirst in [ true; false ] do
        let inventory = collector [| "Name" |] false true None
        inventory.ObserveSite(0, 0, site "Domain.Name" 1 definitionFirst)
        inventory.ObserveSite(0, 1, site "Domain.Name" 1 (not definitionFirst))
        let row = queryRow (inventory.ToJson true) 0
        Assert.Equal(1, number row "definitions")
        Assert.Equal(1, number row "references")
        Assert.Equal(1, number row "uniqueSites")

[<Fact>]
let ``partial category counts remain lower bounds when linked classifications differ`` () =
    let inventory = collector [| "Name" |] false true None
    inventory.ObserveSite(0, 0, site "Domain.Name" 1 false)
    let partial = queryRow (inventory.ToJson false) 0
    inventory.ObserveSite(0, 1, site "Domain.Name" 1 true)
    let complete = queryRow (inventory.ToJson true) 0

    for category in [ "definitions"; "references"; "uniqueSites" ] do
        Assert.True(number partial category <= number complete category)

    Assert.True(boolean partial "countsAreLowerBounds")

[<Fact>]
let ``complete absence is not found while incomplete zero stays indeterminate`` () =
    let inventory = collector [| "Missing" |] false true None
    let complete = queryRow (inventory.ToJson true) 0
    let incomplete = queryRow (inventory.ToJson false) 0
    Assert.Equal("not_found", text complete "outcome")
    Assert.False(boolean complete "matched")
    Assert.True(boolean complete "countsComplete")
    Assert.Equal("indeterminate", text incomplete "outcome")
    Assert.Null(incomplete["matched"])
    Assert.Null(incomplete["ambiguous"])
    Assert.False(boolean incomplete "countsComplete")
    Assert.True(boolean incomplete "countsAreLowerBounds")

[<Fact>]
let ``known matches survive incomplete coverage without becoming exhaustive`` () =
    let inventory = collector [| "Name" |] false true None
    inventory.ObserveSite(0, 0, site "Domain.Name" 1 false)
    let row = queryRow (inventory.ToJson false) 0
    Assert.Equal("found", text row "outcome")
    Assert.True(boolean row "matched")
    Assert.Null(row["ambiguous"])
    Assert.False(boolean row "countsComplete")
    Assert.True(boolean row "countsAreLowerBounds")

[<Fact>]
let ``deadline inside a query loop marks partial project and later zero unknown`` () =
    let inventory = collector [| "Name"; "Domain.Name" |] false true None
    let mutable checks = 0

    let canContinue () =
        checks <- checks + 1
        checks <= 2

    let complete =
        inventory.ObserveUses(7, [| site "Domain.Name" 1 false |], (fun _ _ _ -> true), id, canContinue)

    Assert.False(complete)
    let response = inventory.ToJson true
    Assert.False(boolean response "countsComplete")
    let first = queryRow response 0
    let second = queryRow response 1
    Assert.Equal(1, number first "references")
    Assert.False(boolean (first["perProject"][0]) "countsComplete")
    Assert.Equal(7, number (first["perProject"][0]) "projectIndex")
    Assert.Equal(0, number second "references")
    Assert.Null(second["matched"])
    Assert.Equal("indeterminate", text second "outcome")

[<Fact>]
let ``a completed project retains exact local counts when a later project is partial`` () =
    let inventory = collector [| "Name" |] false true None
    let uses = [| site "Domain.Name" 1 false |]
    Assert.True(inventory.ObserveUses(0, uses, (fun _ _ _ -> true), id, (fun () -> true)))
    Assert.False(inventory.ObserveUses(1, uses, (fun _ _ _ -> true), id, (fun () -> false)))
    let row = queryRow (inventory.ToJson false) 0
    Assert.False(boolean row "countsComplete")
    Assert.True(boolean (row["perProject"][0]) "countsComplete")

[<Fact>]
let ``projection runs once per matching use despite overlapping names`` () =
    let inventory = collector [| "Name"; "Domain.Name"; "Missing" |] false true None
    let mutable projections = 0
    let uses = [| site "Domain.Name" 1 false; site "Other" 2 false |]

    let matches query _ observed = observed.FullName.EndsWith(query, StringComparison.Ordinal)

    let project observed =
        projections <- projections + 1
        observed

    Assert.True(inventory.ObserveUses(0, uses, matches, project, (fun () -> true)))
    Assert.Equal(1, projections)
    let response = inventory.ToJson true
    Assert.Equal(1, number (queryRow response 0) "references")
    Assert.Equal(1, number (queryRow response 1) "references")
    Assert.Equal("not_found", text (queryRow response 2) "outcome")

[<Fact>]
let ``symbol illustrations are bounded without losing full symbol or site counts`` () =
    let inventory = collector [| "Name" |] false true None

    for index in 0 .. MaxIllustrativeSymbols + 4 do
        let longName = String('x', MaxIllustrativeNameChars + 20) + index.ToString()
        inventory.ObserveSite(0, 0, site longName (index + 1) true)

    let row = queryRow (inventory.ToJson true) 0
    Assert.Equal(MaxIllustrativeSymbols + 5, number row "matchedSymbolCount")
    Assert.Equal(MaxIllustrativeSymbols + 5, number row "definitions")
    let symbols = row["symbols"] :?> JsonArray
    Assert.Equal(MaxIllustrativeSymbols, symbols.Count)
    Assert.True(boolean row "symbolsTruncated")
    Assert.True(boolean row "ambiguous")

    for symbol in symbols do
        Assert.Equal(MaxIllustrativeNameChars, (text symbol "fullName").Length)
        Assert.True(boolean symbol "fullNameTruncated")

[<Fact>]
let ``count invariant independently rederives physical union and project partitions`` () =
    let inventory = collector [| "Name"; "Other" |] false true None

    let observations =
        [ for queryIndex in 0..1 do
              for projectIndex in 0..2 do
                  for line in 1..7 do
                      if (queryIndex + projectIndex + line) % 3 <> 0 then
                          yield queryIndex, projectIndex, site $"Symbol{queryIndex}" line (line % 2 = 0) ]

    for queryIndex, projectIndex, observed in observations @ List.rev observations do
        inventory.ObserveSite(queryIndex, projectIndex, observed)

    let response = inventory.ToJson true

    for queryIndex in 0..1 do
        let expected = observations |> List.filter (fun (index, _, _) -> index = queryIndex)
        let union = expected |> List.map (fun (_, _, observed) -> observed.StartLine, observed.IsDefinition) |> Set.ofList
        let row = queryRow response queryIndex
        Assert.Equal(union.Count, number row "uniqueSites")
        Assert.Equal(union |> Seq.filter snd |> Seq.length, number row "definitions")
        Assert.Equal(union |> Seq.filter (snd >> not) |> Seq.length, number row "references")

        for project in row["perProject"] :?> JsonArray do
            let projectIndex = number project "projectIndex"

            let partition =
                expected
                |> List.choose (fun (_, index, observed) -> if index = projectIndex then Some observed.StartLine else None)
                |> Set.ofList

            Assert.Equal(partition.Count, number project "uniqueSites")

[<Fact>]
let ``inventory JSON contains no source snippets or site rows`` () =
    let inventory = collector [| "Name" |] false true None
    inventory.ObserveSite(0, 0, site "Domain.Name" 1 false)
    let response = inventory.ToJson true
    let rendered = response.ToJsonString()

    for forbidden in [ "lineText"; "before"; "after"; "startLine"; "startColumn"; "Shared.fs"; "\"sites\"" ] do
        Assert.DoesNotContain(forbidden, rendered)

[<Fact>]
let ``response construction cooperatively stops before serializing all project rows`` () =
    let inventory = collector [| "Name" |] false true None

    for projectIndex in 0..99 do
        inventory.ObserveSite(0, projectIndex, site "Domain.Name" 1 false)

    Assert.True(inventory.HasMatches)
    let mutable steps = 0

    let canContinue () =
        steps <- steps + 1
        steps < 5

    Assert.Throws<TimeoutException>(fun () -> inventory.ToJson(true, canContinue = canContinue) |> ignore)
    |> ignore

    Assert.Equal(5, steps)

[<Fact>]
let ``exception during matching preserves incompleteness of already observed project rows`` () =
    let inventory = collector [| "Name"; "Other" |] false true None

    let matches query _ _ =
        if query = "Other" then
            raise (InvalidOperationException("Controlled matcher failure."))

        true

    Assert.Throws<InvalidOperationException>(fun () ->
        inventory.ObserveUses(0, [| site "Name" 1 true |], matches, id, (fun () -> true)) |> ignore)
    |> ignore

    let response = inventory.ToJson true
    Assert.True(inventory.HasMatches)
    Assert.False(boolean response "countsComplete")
    Assert.False(boolean ((queryRow response 0)["perProject"][0]) "countsComplete")
