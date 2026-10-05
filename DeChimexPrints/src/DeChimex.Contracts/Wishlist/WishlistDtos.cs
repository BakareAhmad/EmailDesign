namespace DeChimex.Contracts.Wishlist;

public sealed record WishlistItemDto(Guid Id, Guid ProductId, string ProductName, decimal SellingPrice, string Currency, string? PrimaryImageUrl);
public sealed record AddWishlistItemRequest(Guid ProductId);
