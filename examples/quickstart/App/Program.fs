open Domain

let sampleOrders : Order list =
    [ { Id = 1; Customer = "Alice"; Amount = 250.00m }
      { Id = 2; Customer = "Bob"; Amount = 1500.00m } ]

[<EntryPoint>]
let main _ =
    for order in sampleOrders do
        let tag = if Order.isLargeOrder order then " [LARGE]" else ""
        printfn "%s" (Order.describe order + tag)
    0
