using BirdCam.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BirdCam.Infrastructure.Persistence;

public sealed class BirdCamDbContext(DbContextOptions<BirdCamDbContext> options) : DbContext(options)
{
    private static readonly ValueConverter<DateTimeOffset, long> OffsetToTicks =
        new(value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableOffsetToTicks =
        new(value => value == null ? null : value.Value.UtcTicks,
            ticks => ticks == null ? null : new DateTimeOffset(ticks.Value, TimeSpan.Zero));

    public DbSet<MediaItem> MediaItems => Set<MediaItem>();

    public DbSet<DatasetSample> DatasetSamples => Set<DatasetSample>();

    public DbSet<DetectionRecord> Detections => Set<DetectionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaItem>(entity =>
        {
            entity.ToTable("media_items");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.FilePath).HasMaxLength(512).IsRequired();
            entity.Property(item => item.ThumbnailPath).HasMaxLength(512);
            entity.Property(item => item.Note).HasMaxLength(512);
            entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(item => item.CreatedAtUtc).HasConversion(OffsetToTicks);
            entity.Property(item => item.SentToTelegramAtUtc).HasConversion(NullableOffsetToTicks);
            entity.HasIndex(item => item.CreatedAtUtc);
        });

        modelBuilder.Entity<DatasetSample>(entity =>
        {
            entity.ToTable("dataset_samples");
            entity.HasKey(sample => sample.Id);
            entity.Property(sample => sample.ImagePath).HasMaxLength(512).IsRequired();
            entity.Property(sample => sample.LabelPath).HasMaxLength(512).IsRequired();
            entity.Property(sample => sample.CropPath).HasMaxLength(512);
            entity.Property(sample => sample.Species).HasMaxLength(128);
            entity.Property(sample => sample.Source).HasConversion<string>().HasMaxLength(16);
            entity.Property(sample => sample.CreatedAtUtc).HasConversion(OffsetToTicks);
            entity.HasIndex(sample => sample.CreatedAtUtc);
        });

        modelBuilder.Entity<DetectionRecord>(entity =>
        {
            entity.ToTable("detections");
            entity.HasKey(detection => detection.Id);
            entity.Property(detection => detection.Label).HasMaxLength(64).IsRequired();
            entity.Property(detection => detection.CreatedAtUtc).HasConversion(OffsetToTicks);
            entity.HasIndex(detection => detection.CreatedAtUtc);
        });
    }
}
