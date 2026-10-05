using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Messaging;
using DeChimex.Contracts.Gallery;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using FluentValidation;
using MediatR;

namespace DeChimex.Application.Features.Gallery.Commands;

public sealed record CreateGalleryMediaCommand(CreateGalleryMediaRequest Request) : ICommand<GalleryMediaDto>;

public sealed class CreateGalleryMediaValidator : AbstractValidator<CreateGalleryMediaCommand>
{
    public CreateGalleryMediaValidator()
    {
        RuleFor(x => x.Request.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.Url).NotEmpty();
        RuleFor(x => x.Request.Type).Must(t => Enum.TryParse<GalleryMediaType>(t, true, out _)).WithMessage("Invalid media type.");
    }
}

public sealed class CreateGalleryMediaHandler(IGalleryRepository gallery, IUnitOfWork uow)
    : ICommandHandler<CreateGalleryMediaCommand, GalleryMediaDto>
{
    public async Task<GalleryMediaDto> Handle(CreateGalleryMediaCommand c, CancellationToken ct)
    {
        var r = c.Request;
        var type = Enum.Parse<GalleryMediaType>(r.Type, true);
        var media = GalleryMedia.Create(r.Title, r.Url, type, r.Category, r.ThumbnailUrl);
        media.Update(r.Title, r.Category, r.IsFeatured, r.DisplayOrder);

        // Carousel post: the extra selected images become ordered children of this post.
        // AddImage enforces the video-stays-single-media rule.
        if (r.AdditionalImageUrls is { Count: > 0 })
            foreach (var url in r.AdditionalImageUrls)
                media.AddImage(url);

        await gallery.AddAsync(media, ct);
        await uow.SaveChangesAsync(ct);
        return Map(media);
    }

    internal static GalleryMediaDto Map(GalleryMedia m) => new(m.Id, m.Title, m.Url, m.ThumbnailUrl, m.Type.ToString(),
        m.Category, m.IsFeatured, m.IsArchived, m.DisplayOrder,
        new List<string> { m.Url }.Concat(m.Images.OrderBy(i => i.DisplayOrder).Select(i => i.Url)).ToList());
}

/// <summary>
/// Replaces a gallery post's ordered image list (add / remove / reorder / change the
/// thumbnail through ordering). The first url becomes the post media, the rest its children.
/// </summary>
public sealed record SetGalleryImagesCommand(Guid Id, IReadOnlyList<string> OrderedImageUrls) : ICommand<Unit>;

public sealed class SetGalleryImagesValidator : AbstractValidator<SetGalleryImagesCommand>
{
    public SetGalleryImagesValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.OrderedImageUrls).NotEmpty().WithMessage("An image post needs at least one image.");
        RuleForEach(x => x.OrderedImageUrls).NotEmpty().WithMessage("Image URLs must not be empty.");
        RuleFor(x => x.OrderedImageUrls.Count).LessThanOrEqualTo(30).WithMessage("Too many images in one post.");
    }
}

public sealed class SetGalleryImagesHandler(IGalleryRepository gallery, IUnitOfWork uow)
    : ICommandHandler<SetGalleryImagesCommand, Unit>
{
    public async Task<Unit> Handle(SetGalleryImagesCommand c, CancellationToken ct)
    {
        var media = await gallery.GetWithImagesAsync(c.Id, ct) ?? throw new NotFoundException("GalleryMedia", c.Id);
        // Explicitly delete the replaced child rows. Severing them from the collection
        // alone is not enough: with a nullable FK the tracker would otherwise orphan
        // them into parent-less posts (they would show up in the grid as their own posts).
        foreach (var child in media.Images.ToList())
            gallery.Remove(child);
        media.SetImages(c.OrderedImageUrls);
        gallery.Update(media);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record UpdateGalleryMediaCommand(Guid Id, UpdateGalleryMediaRequest Request) : ICommand<Unit>;
public sealed class UpdateGalleryMediaHandler(IGalleryRepository gallery, IUnitOfWork uow)
    : ICommandHandler<UpdateGalleryMediaCommand, Unit>
{
    public async Task<Unit> Handle(UpdateGalleryMediaCommand c, CancellationToken ct)
    {
        var media = await gallery.GetByIdAsync(c.Id, ct) ?? throw new NotFoundException("GalleryMedia", c.Id);
        var r = c.Request;
        var type = string.IsNullOrWhiteSpace(r.Type)
            ? null
            : (DeChimex.Domain.Enums.GalleryMediaType?)Enum.Parse(typeof(DeChimex.Domain.Enums.GalleryMediaType), r.Type!, true);
        media.Update(r.Title, r.Category, r.IsFeatured, r.DisplayOrder,
            r.Url, r.ThumbnailUrl, type);
        gallery.Update(media);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record ArchiveGalleryMediaCommand(Guid Id, bool Archive) : ICommand<Unit>;
public sealed class ArchiveGalleryMediaHandler(IGalleryRepository gallery, IUnitOfWork uow)
    : ICommandHandler<ArchiveGalleryMediaCommand, Unit>
{
    public async Task<Unit> Handle(ArchiveGalleryMediaCommand c, CancellationToken ct)
    {
        var media = await gallery.GetByIdAsync(c.Id, ct) ?? throw new NotFoundException("GalleryMedia", c.Id);
        if (c.Archive) media.Archive(); else media.Unarchive();
        gallery.Update(media);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

public sealed record DeleteGalleryMediaCommand(Guid Id) : ICommand<Unit>;
public sealed class DeleteGalleryMediaHandler(IGalleryRepository gallery, IUnitOfWork uow)
    : ICommandHandler<DeleteGalleryMediaCommand, Unit>
{
    public async Task<Unit> Handle(DeleteGalleryMediaCommand c, CancellationToken ct)
    {
        var media = await gallery.GetWithImagesAsync(c.Id, ct) ?? throw new NotFoundException("GalleryMedia", c.Id);
        // The self-referencing FK is ON DELETE NO ACTION (SQL Server forbids CASCADE on
        // it, error 1785), so carousel children must be removed explicitly or the root
        // delete would violate FK_GalleryMedia_GalleryMedia_ParentId.
        foreach (var child in media.Images.ToList())
            gallery.Remove(child);
        gallery.Remove(media);
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
