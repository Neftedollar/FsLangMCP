namespace Domain

/// An order placed by a customer.
type Order = {
    Id: int
    Customer: string
    Amount: decimal
}

module Order =

    /// Returns a human-readable description of the order.
    let describe (order: Order) : string =
        $"Order #{order.Id} for {order.Customer}: ${order.Amount}"

    /// Returns true when the order amount exceeds the large-order threshold.
    let isLargeOrder (order: Order) : bool =
        order.Amount > 1000m
