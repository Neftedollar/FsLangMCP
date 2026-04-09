module FsLangMcp.BoundedCache

/// Thread-safe FIFO-eviction bounded cache.
/// When maxSize is reached, the oldest inserted key is evicted.
type internal BoundedCache<'K, 'V when 'K : equality>(maxSize: int) =
    let dict = System.Collections.Generic.Dictionary<'K, 'V>()
    let order = System.Collections.Generic.Queue<'K>()
    let lockObj = obj()

    member _.TryGet(key: 'K) : 'V option =
        lock lockObj (fun () ->
            match dict.TryGetValue(key) with
            | true, v -> Some v
            | _ -> None)

    member _.Set(key: 'K, value: 'V) =
        lock lockObj (fun () ->
            if not (dict.ContainsKey(key)) then
                if dict.Count >= maxSize then
                    let oldest = order.Dequeue()
                    dict.Remove(oldest) |> ignore
                order.Enqueue(key)
            dict[key] <- value)

    member _.Clear() =
        lock lockObj (fun () ->
            dict.Clear()
            order.Clear())
