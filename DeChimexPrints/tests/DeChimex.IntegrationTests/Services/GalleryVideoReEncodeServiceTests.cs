using DeChimex.Application.Common.Interfaces;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Host.Services;
using DeChimex.Infrastructure.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.IntegrationTests.Services;

/// <summary>
/// Deterministic tests for the gallery-video re-encode backfill. The encoder and repository are
/// faked (no ffmpeg / no database), so the test only verifies the orchestration: iterating video
/// media, resolving the public URL to a physical file, skipping images, reporting missing files,
/// and aggregating the result counts.
/// </summary>
public class GalleryVideoReEncodeServiceTests
{
    private sealed class FakeVideoEncode : IVideoEncodeService
    {
        public HashSet<string> Touched { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool PlayableResult { get; set; }

        public Task<bool> MakeBrowserPlayableAsync(string sourceFile, CancellationToken ct = default)
        {
            Touched.Add(sourceFile);
            return Task.FromResult(PlayableResult);
        }
    }

    private sealed class FakeGalleryRepo : IGalleryRepository
    {
        private readonly IReadOnlyList<GalleryMedia> _items;
        public FakeGalleryRepo(params GalleryMedia[] items) => _items = items;

        public Task<GalleryMedia?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_items.FirstOrDefault(x => x.Id == id));

        public Task<GalleryMedia?> GetWithImagesAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_items.FirstOrDefault(x => x.Id == id));

        public Task<bool> ExistsAsync(System.Linq.Expressions.Expression<Func<GalleryMedia, bool>> predicate, CancellationToken ct = default) =>
            Task.FromResult(_items.AsQueryable().Any(predicate));

        public Task<IReadOnlyList<GalleryMedia>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(_items);

        public Task AddAsync(GalleryMedia aggregate, CancellationToken ct = default) => Task.CompletedTask;
        public void Update(GalleryMedia aggregate) { }
        public void Remove(GalleryMedia aggregate) { }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static GalleryVideoReEncodeService Build(
        string contentRoot, string localRoot, string publicBase,
        IGalleryRepository repo, FakeVideoEncode encoder)
    {
        var env = new FakeWebHostEnvironment { ContentRootPath = contentRoot };
        var storage = Options.Create(new StorageOptions
        {
            LocalRootPath = localRoot,
            PublicBaseUrl = publicBase,
            Provider = "Local",
            MaxFileSizeBytes = 10 * 1024 * 1024
        });
        return new GalleryVideoReEncodeService(
            repo, encoder, env, storage, NullLogger<GalleryVideoReEncodeService>.Instance);
    }

    [Fact]
    public async Task RunAsync_Videos_AreReEncoded_ImagesSkipped()
    {
        // Simulate an uploads root with one real video file on disk.
        var contentRoot = Path.Combine(Path.GetTempPath(), "dcx_" + Guid.NewGuid().ToString("n"));
        var uploadRoot = Path.Combine(contentRoot, "wwwroot", "uploads");
        var mediaDir = Path.Combine(uploadRoot, "gallerymedia");
        Directory.CreateDirectory(mediaDir);
        var fileName = Guid.NewGuid().ToString("n") + ".mp4";
        File.WriteAllBytes(Path.Combine(mediaDir, fileName), new byte[] { 1, 2, 3, 4, 5 });

        var video = GalleryMedia.Create("Reel", $"/uploads/gallerymedia/{fileName}", GalleryMediaType.Video, "General");
        var image = GalleryMedia.Create("Notepads", "/uploads/gallerymedia/other.png", GalleryMediaType.Image, "Branding");

        var encoder = new FakeVideoEncode { PlayableResult = true };
        var sut = Build(contentRoot, Path.Combine("wwwroot", "uploads"), "/uploads",
            new FakeGalleryRepo(video, image), encoder);

        var result = await sut.RunAsync();

        result.TotalVideos.Should().Be(1);          // only the video counts as a "video"
        result.ReEncoded.Should().Be(1);
        result.AlreadyPlayable.Should().Be(0);
        result.MissingFiles.Should().Be(0);
        result.Failed.Should().Be(0);
        // Only the video's file was touched; the image file was never encoded.
        encoder.Touched.Should().ContainSingle(x => x.EndsWith(fileName));
    }

    [Fact]
    public async Task RunAsync_MissingFile_IsCounted_NotFailed()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "dcx_" + Guid.NewGuid().ToString("n"));
        var encoder = new FakeVideoEncode { PlayableResult = true };
        var video = GalleryMedia.Create("Ghost", "/uploads/gallerymedia/does-not-exist.mp4", GalleryMediaType.Video);

        var sut = Build(contentRoot, Path.Combine("wwwroot", "uploads"), "/uploads",
            new FakeGalleryRepo(video), encoder);

        var result = await sut.RunAsync();

        result.TotalVideos.Should().Be(1);
        result.MissingFiles.Should().Be(1);
        result.ReEncoded.Should().Be(0);
        result.Failed.Should().Be(0);
        encoder.Touched.Should().BeEmpty();         // the encoder is never called for a missing file
    }

    [Fact]
    public async Task RunAsync_NoVideos_ReturnsZero()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "dcx_" + Guid.NewGuid().ToString("n"));
        var encoder = new FakeVideoEncode();
        var sut = Build(contentRoot, Path.Combine("wwwroot", "uploads"), "/uploads",
            new FakeGalleryRepo(GalleryMedia.Create("Notepads", "/uploads/gallerymedia/a.png", GalleryMediaType.Image)), encoder);

        var result = await sut.RunAsync();

        result.TotalVideos.Should().Be(0);
        result.ReEncoded.Should().Be(0);
        result.MissingFiles.Should().Be(0);
        encoder.Touched.Should().BeEmpty();
    }
}
