using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Gallery;

namespace DeChimex.Application.Features.Gallery.Queries;

public sealed record GetGalleryQuery(bool IncludeArchived, string? Category) : IQuery<IReadOnlyList<GalleryMediaDto>>;
public sealed class GetGalleryHandler(IGalleryQueries q) : IQueryHandler<GetGalleryQuery, IReadOnlyList<GalleryMediaDto>>
{
    public Task<IReadOnlyList<GalleryMediaDto>> Handle(GetGalleryQuery query, CancellationToken ct)
        => q.GetAllAsync(query.IncludeArchived, query.Category, ct);
}
