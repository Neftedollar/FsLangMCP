module FsLangMcp.MetadataAccessibility

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable

type internal MemberMetadata =
    { Accessibility: string
      IsAbstract: bool }

/// Normalizes the two spellings used by metadata (`Outer+Inner`, ``Type`1``)
/// and FCS (`Outer.Inner`, sometimes with the arity retained) to one lookup key.
let internal normalizeTypeName (name: string) =
    if String.IsNullOrWhiteSpace name then
        ""
    else
        name.Replace('+', '.').Split('.')
        |> Array.map (fun segment ->
            let arity = segment.LastIndexOf('`')

            if
                arity > 0
                && arity < segment.Length - 1
                && segment.Substring(arity + 1) |> Seq.forall Char.IsDigit
            then
                segment.Substring(0, arity)
            else
                segment)
        |> String.concat "."

let private methodAccessibility (attributes: MethodAttributes) =
    match attributes &&& MethodAttributes.MemberAccessMask with
    | MethodAttributes.Public -> "public"
    | MethodAttributes.FamORAssem -> "protected internal"
    | MethodAttributes.Family -> "protected"
    | MethodAttributes.Assembly -> "internal"
    | MethodAttributes.FamANDAssem -> "private protected"
    | MethodAttributes.Private
    | MethodAttributes.PrivateScope -> "private"
    | _ -> "unknown"

let private methodMetadata (attributes: MethodAttributes) =
    { Accessibility = methodAccessibility attributes
      IsAbstract = (attributes &&& MethodAttributes.Abstract) <> enum 0 }

let private aliases (metadataName: string) =
    seq {
        yield metadataName

        for prefix in [| "get_"; "set_"; "add_"; "remove_" |] do
            if metadataName.StartsWith(prefix, StringComparison.Ordinal) then
                yield metadataName.Substring(prefix.Length)
    }

/// Project-system references often point FCS at `obj/<config>/<tfm>/ref/*.dll`.
/// Reference assemblies intentionally omit internal members, while the sibling
/// implementation under `bin/<config>/<tfm>` retains their real CLR flags.
let internal candidateAssemblyPaths (assemblyPath: string) =
    seq {
        if not (String.IsNullOrWhiteSpace assemblyPath) && File.Exists assemblyPath then
            yield Path.GetFullPath assemblyPath

            try
                let referenceDirectory = DirectoryInfo(Path.GetDirectoryName assemblyPath)

                if
                    (referenceDirectory.Name.Equals("ref", StringComparison.OrdinalIgnoreCase)
                     || referenceDirectory.Name.Equals("refint", StringComparison.OrdinalIgnoreCase))
                    && not (isNull referenceDirectory.Parent)
                    && not (isNull referenceDirectory.Parent.Parent)
                    && not (isNull referenceDirectory.Parent.Parent.Parent)
                    && referenceDirectory.Parent.Parent.Parent.Name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                then
                    let targetFramework = referenceDirectory.Parent.Name
                    let configuration = referenceDirectory.Parent.Parent.Name
                    let projectDirectory = referenceDirectory.Parent.Parent.Parent.Parent

                    if not (isNull projectDirectory) then
                        let implementationPath =
                            Path.Combine(
                                projectDirectory.FullName,
                                "bin",
                                configuration,
                                targetFramework,
                                Path.GetFileName assemblyPath
                            )

                        if File.Exists implementationPath then
                            yield Path.GetFullPath implementationPath
            with _ ->
                ()

            try
                // NuGet reference packs use `<package>/ref/<tfm>/A.dll`; when the
                // package also ships `<package>/lib/<tfm>/A.dll`, the latter retains
                // non-public metadata omitted from the reference assembly.
                let targetFrameworkDirectory = DirectoryInfo(Path.GetDirectoryName assemblyPath)

                if
                    not (isNull targetFrameworkDirectory.Parent)
                    && targetFrameworkDirectory.Parent.Name.Equals("ref", StringComparison.OrdinalIgnoreCase)
                    && not (isNull targetFrameworkDirectory.Parent.Parent)
                then
                    let implementationPath =
                        Path.Combine(
                            targetFrameworkDirectory.Parent.Parent.FullName,
                            "lib",
                            targetFrameworkDirectory.Name,
                            Path.GetFileName assemblyPath
                        )

                    if File.Exists implementationPath then
                        yield Path.GetFullPath implementationPath
            with _ ->
                ()
    }
    |> Seq.distinct
    |> Seq.toArray

/// Reads only ECMA-335 metadata; it does not load the target assembly or execute
/// module initializers. A key resolves only when every matching overload/accessor
/// agrees on accessibility and abstractness. Ambiguous rows deliberately fall
/// back to FCS rather than assigning one overload's metadata to another.
let internal tryCreateMemberResolver
    (assemblyPath: string)
    : (string -> string -> MemberMetadata option) option =
    try
        if String.IsNullOrWhiteSpace assemblyPath || not (File.Exists assemblyPath) then
            None
        else
            use stream =
                new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)

            use peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata)

            if not peReader.HasMetadata then
                None
            else
                let reader = peReader.GetMetadataReader()
                let entries = Dictionary<string, HashSet<MemberMetadata>>(StringComparer.Ordinal)

                let rec fullTypeName (handle: TypeDefinitionHandle) =
                    let definition = reader.GetTypeDefinition(handle)
                    let ownName = reader.GetString(definition.Name)
                    let declaringType = definition.GetDeclaringType()

                    if declaringType.IsNil then
                        let ns = reader.GetString(definition.Namespace)
                        if String.IsNullOrWhiteSpace ns then ownName else $"%s{ns}.%s{ownName}"
                    else
                        $"%s{fullTypeName declaringType}+%s{ownName}"

                let add typeName memberName metadata =
                    let key = $"%s{normalizeTypeName typeName}\u0000%s{memberName}"

                    match entries.TryGetValue key with
                    | true, values -> values.Add(metadata) |> ignore
                    | false, _ ->
                        let values = HashSet<MemberMetadata>()
                        values.Add(metadata) |> ignore
                        entries.Add(key, values)

                for typeHandle in reader.TypeDefinitions do
                    let typeDefinition = reader.GetTypeDefinition(typeHandle)
                    let typeName = fullTypeName typeHandle

                    for methodHandle in typeDefinition.GetMethods() do
                        let methodDefinition = reader.GetMethodDefinition(methodHandle)
                        let metadataName = reader.GetString(methodDefinition.Name)
                        let metadata = methodMetadata methodDefinition.Attributes

                        for alias in aliases metadataName do
                            add typeName alias metadata

                Some(fun typeName memberName ->
                    let key = $"%s{normalizeTypeName typeName}\u0000%s{memberName}"

                    match entries.TryGetValue key with
                    | true, values when values.Count = 1 -> values |> Seq.exactlyOne |> Some
                    | _ -> None)
    with
    | :? BadImageFormatException
    | :? IOException
    | :? UnauthorizedAccessException -> None
