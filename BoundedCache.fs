module FsLangMcp.BoundedCache

/// Thread-safe FIFO-eviction bounded cache.
/// When maxSize is reached, the oldest inserted key is evicted.
type internal BoundedCache<'K, 'V when 'K: equality>(maxSize: int) =
    do
        if maxSize <= 0 then
            invalidArg (nameof maxSize) "Cache capacity must be positive."

    let dict = System.Collections.Generic.Dictionary<'K, 'V>()
    let order = System.Collections.Generic.LinkedList<'K>()

    let nodes =
        System.Collections.Generic.Dictionary<'K, System.Collections.Generic.LinkedListNode<'K>>()

    let lockObj = obj ()

    member _.TryGet(key: 'K) : 'V option =
        lock lockObj (fun () ->
            match dict.TryGetValue(key) with
            | true, v -> Some v
            | _ -> None)

    member _.Set(key: 'K, value: 'V) =
        lock lockObj (fun () ->
            if not (dict.ContainsKey(key)) then
                if dict.Count >= maxSize then
                    let oldest = order.First.Value
                    order.RemoveFirst()
                    dict.Remove(oldest) |> ignore
                    nodes.Remove(oldest) |> ignore

                nodes[key] <- order.AddLast(key)

            dict[key] <- value)

    member _.Clear() =
        lock lockObj (fun () ->
            dict.Clear()
            order.Clear()
            nodes.Clear())

    /// Remove a single key. Returns true if the key existed; false otherwise.
    member _.TryRemove(key: 'K) : bool =
        lock lockObj (fun () ->
            if dict.Remove(key) then
                match nodes.TryGetValue(key) with
                | true, node ->
                    order.Remove(node)
                    nodes.Remove(key) |> ignore
                | _ -> ()

                true
            else
                false)

    member _.Count = lock lockObj (fun () -> dict.Count)
