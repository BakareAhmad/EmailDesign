namespace DeChimex.Application.Common.Models;

/// <summary>Reusable pagination request with search &amp; sort metadata.</summary>
public class PagedRequest
{
    private const int MaxPageSize = 100;
    private int _pageSize = 20;
    private int _page = 1;

    public int Page
    {
        get => _page;
        set => _page = value < 1 ? 1 : value;
    }

    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = value switch { < 1 => 20, > MaxPageSize => MaxPageSize, _ => value };
    }

    public string? Search { get; set; }
    public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
}

/// <summary>A page of results plus pagination metadata.</summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int TotalRecords { get; }
    public int TotalPages { get; }
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    public PagedResult(IReadOnlyList<T> items, int totalRecords, int page, int pageSize)
    {
        Items = items;
        TotalRecords = totalRecords;
        Page = page;
        PageSize = pageSize;
        TotalPages = pageSize == 0 ? 0 : (int)Math.Ceiling(totalRecords / (double)pageSize);
    }

    public static PagedResult<T> Empty(int page, int pageSize) => new(Array.Empty<T>(), 0, page, pageSize);
}
