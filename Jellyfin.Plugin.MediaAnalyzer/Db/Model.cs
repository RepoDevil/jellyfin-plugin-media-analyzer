using System;
using Jellyfin.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.MediaAnalyzer;

/// <summary>
/// Plugin database.
/// </summary>
public class MediaAnalyzerDbContext : DbContext
{
    private string dbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaAnalyzerDbContext"/> class.
    /// </summary>
    /// <param name="path">Path to db.</param>
    public MediaAnalyzerDbContext(string path)
    {
        dbPath = path;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaAnalyzerDbContext"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    public MediaAnalyzerDbContext(DbContextOptions options) : base(options)
    {
        var folder = Environment.SpecialFolder.LocalApplicationData;
        var path = Environment.GetFolderPath(folder);
        dbPath = System.IO.Path.Join(path, "jfpmediaanalyzer.db");
    }

    /// <summary>
    /// Gets the <see cref="DbSet{TEntity}"/> containing the blacklisted segments.
    /// </summary>
    public DbSet<BlacklistSegment> BlacklistSegment => Set<BlacklistSegment>();

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlite($"Data Source={dbPath}");

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BlacklistSegment>()
        .HasKey(s => new { s.ItemId, s.Type });
    }

    /// <summary>
    /// Apply migrations. Needs to be called before any actions are executed.
    /// </summary>
    public void ApplyMigrations()
    {
        this.Database.Migrate();
    }
}

/// <summary>
/// A segment that is blacklisted for future analysis runs.
/// This happens, when a media has been analyzed but no segment was returned.
/// </summary>
public class BlacklistSegment
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BlacklistSegment"/> class.
    /// </summary>
    /// <param name="intro">intro.</param>
    /// <param name="mode">mode.</param>
    public BlacklistSegment(Segment intro, AnalysisMode mode)
    {
        ItemId = intro.ItemId;
        Type = mode == AnalysisMode.Introduction ? MediaSegmentType.Intro : MediaSegmentType.Outro;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BlacklistSegment"/> class.
    /// </summary>
    public BlacklistSegment()
    {
    }

    /// <summary>
    /// Gets or sets the segment name used for better log messages.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item ID of db.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the segment type.
    /// </summary>
    public MediaSegmentType Type { get; set; }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return (obj as BlacklistSegment)?.Type == this.Type && (obj as BlacklistSegment)?.ItemId == this.ItemId;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return Type.GetHashCode() + ItemId.GetHashCode();
    }
}
