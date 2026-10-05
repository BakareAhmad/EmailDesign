namespace DeChimex.Contracts.Cart;

public sealed record CartItemDto(Guid Id, string LineType, Guid ReferenceId, string Name, decimal UnitPrice, string Currency, int Quantity, decimal LineTotal, string? Notes, string? ImageUrl = null);
public sealed record CartDto(Guid Id, IReadOnlyList<CartItemDto> Items, decimal Subtotal, string Currency, int ItemCount);
public sealed record AddCartItemRequest(string LineType, Guid ReferenceId, int Quantity, string? Notes);
public sealed record UpdateCartItemRequest(int Quantity);
