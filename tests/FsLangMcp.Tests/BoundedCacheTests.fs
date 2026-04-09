module FsLangMcp.Tests.BoundedCacheTests

open FsLangMcp.BoundedCache
open Xunit
open System.Threading.Tasks

[<Fact>]
let ``TryGet on empty cache returns None`` () =
    let cache = BoundedCache<string, int>(3)
    Assert.Equal(None, cache.TryGet("x"))

[<Fact>]
let ``Set and TryGet round-trips value`` () =
    let cache = BoundedCache<string, int>(3)
    cache.Set("a", 1)
    Assert.Equal(Some 1, cache.TryGet("a"))

[<Fact>]
let ``FIFO eviction at capacity`` () =
    let cache = BoundedCache<string, int>(2)
    cache.Set("a", 1)
    cache.Set("b", 2)
    cache.Set("c", 3)   // evicts "a"
    Assert.Equal(None,   cache.TryGet("a"))
    Assert.Equal(Some 2, cache.TryGet("b"))
    Assert.Equal(Some 3, cache.TryGet("c"))

[<Fact>]
let ``Update existing key preserves value`` () =
    let cache = BoundedCache<string, int>(3)
    cache.Set("a", 1)
    cache.Set("a", 99)
    Assert.Equal(Some 99, cache.TryGet("a"))

[<Fact>]
let ``Update existing key preserves original eviction order`` () =
    // capacity 2: a→b inserted, a updated, then c inserted → evicts a (oldest insertion)
    let cache = BoundedCache<string, int>(2)
    cache.Set("a", 1)
    cache.Set("b", 2)
    cache.Set("a", 99)   // update value; eviction order must NOT change
    cache.Set("c", 3)    // triggers eviction — must evict "a", not "b"
    Assert.Equal(None,   cache.TryGet("a"))   // evicted (was oldest)
    Assert.Equal(Some 2, cache.TryGet("b"))   // retained
    Assert.Equal(Some 3, cache.TryGet("c"))   // just inserted

[<Fact>]
let ``Clear empties the cache`` () =
    let cache = BoundedCache<string, int>(3)
    cache.Set("a", 1)
    cache.Set("b", 2)
    cache.Clear()
    Assert.Equal(None, cache.TryGet("a"))
    Assert.Equal(None, cache.TryGet("b"))

[<Fact>]
let ``Capacity-1 cache evicts on each new key`` () =
    let cache = BoundedCache<int, string>(1)
    cache.Set(1, "one")
    cache.Set(2, "two")
    Assert.Equal(None,        cache.TryGet(1))
    Assert.Equal(Some "two",  cache.TryGet(2))

[<Fact>]
let ``Concurrent Set and TryGet does not throw`` () : Task =
    task {
        let cache = BoundedCache<int, int>(50)
        let writers =
            Array.init 10 (fun i ->
                Task.Run(fun () ->
                    for j in 0 .. 99 do cache.Set(i * 100 + j, j)))
        let readers =
            Array.init 5 (fun _ ->
                Task.Run(fun () ->
                    for k in 0 .. 499 do cache.TryGet(k) |> ignore))
        do! Task.WhenAll(Array.append writers readers)
        // No exception = pass
    }
