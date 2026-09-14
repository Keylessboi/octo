using Octo.Models.Settings;
using IOFile = System.IO.File;

namespace Octo.Services.Common;

/// <summary>
/// Helper class for path building and sanitization.
/// Provides utilities for creating safe file and folder paths for downloaded music files.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Where a track goes for a given <see cref="FolderStructure"/>.
    ///
    /// Every download path routes through here so one setting decides the layout for all
    /// of them. It used to be decided by a separate switch per source - the Soulseek move,
    /// the YouTube download and the Lidarr import - and two of those carried a silent
    /// default branch, so adding a layout would have left them quietly filing into the old
    /// one while only the third obeyed the setting.
    ///
    /// The switch is exhaustive on purpose: a new layout should fail to compile here rather
    /// than resolve to whatever the default arm happened to be.
    /// </summary>
    public static string BuildLayoutPath(FolderStructure structure, string downloadPath,
        string artist, string album, string title, int? trackNumber, string extension)
    {
        var safeArtist = SanitizeFolderName(artist);
        var safeTitle = SanitizeFileName(title);

        // A track with no album falls back to its own title as the folder. Before albums
        // existed the Organized layout always used the TRACK title, scattering an album's
        // tracks into a folder each; the routing carries the album now, and this keeps that
        // old shape only for a track that genuinely has none. The rule lives here so every
        // caller gets it instead of each one remembering to apply it.
        var effectiveAlbum = string.IsNullOrWhiteSpace(album) ? title : album;

        return structure switch
        {
            FolderStructure.Flat =>
                Path.Combine(downloadPath, $"{safeArtist} - {safeTitle}{extension}"),

            // No album folder: the album stays in the tags, which is what the server reads.
            FolderStructure.ByArtist =>
                Path.Combine(downloadPath, safeArtist, $"{safeTitle}{extension}"),

            FolderStructure.Organized =>
                BuildTrackPath(downloadPath, artist, effectiveAlbum, title, trackNumber, extension),

            _ => throw new ArgumentOutOfRangeException(nameof(structure), structure,
                "Unhandled folder layout."),
        };
    }
    /// <summary>
    /// Gets the cache directory path for temporary file storage.
    /// Uses system temp directory combined with octo-cache subfolder.
    /// Respects TMPDIR environment variable on Linux/macOS.
    /// </summary>
    /// <returns>Full path to the cache directory.</returns>
    public static string GetCachePath()
    {
        return Path.Combine(Path.GetTempPath(), "octo-cache");
    }
    
    /// <summary>
    /// Builds the output path for a downloaded track following the Artist/Album/Track structure.
    /// </summary>
    /// <param name="downloadPath">Base download directory path.</param>
    /// <param name="artist">Artist name (will be sanitized).</param>
    /// <param name="album">Album name (will be sanitized).</param>
    /// <param name="title">Track title (will be sanitized).</param>
    /// <param name="trackNumber">Optional track number for prefix.</param>
    /// <param name="extension">File extension (e.g., ".flac", ".mp3").</param>
    /// <returns>Full path for the track file.</returns>
    public static string BuildTrackPath(string downloadPath, string artist, string album, string title, int? trackNumber, string extension)
    {
        var safeArtist = SanitizeFolderName(artist);
        var safeAlbum = SanitizeFolderName(album);
        var safeTitle = SanitizeFileName(title);
        
        var artistFolder = Path.Combine(downloadPath, safeArtist);
        var albumFolder = Path.Combine(artistFolder, safeAlbum);
        
        var trackPrefix = trackNumber.HasValue ? $"{trackNumber:D2} - " : "";
        var fileName = $"{trackPrefix}{safeTitle}{extension}";
        
        return Path.Combine(albumFolder, fileName);
    }

    /// <summary>
    /// Sanitizes a file name by removing invalid characters.
    /// </summary>
    /// <param name="fileName">Original file name.</param>
    /// <returns>Sanitized file name safe for all file systems.</returns>
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "Unknown";
        }
        
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());
        
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100];
        }
        
        return sanitized.Trim();
    }

    /// <summary>
    /// Sanitizes a folder name by removing invalid path characters.
    /// </summary>
    /// <param name="folderName">Original folder name.</param>
    /// <returns>Sanitized folder name safe for all file systems.</returns>
    public static string SanitizeFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "Unknown";
        }
        
        var invalidChars = Path.GetInvalidFileNameChars()
            .Concat(Path.GetInvalidPathChars())
            .Distinct()
            .ToArray();
            
        var sanitized = new string(folderName
            .Select(c => invalidChars.Contains(c) ? '_' : c)
            .ToArray());
        
        // Remove leading/trailing dots and spaces (Windows folder restrictions)
        sanitized = sanitized.Trim().TrimEnd('.');
        
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100].TrimEnd('.');
        }
        
        // Ensure we have a valid name
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "Unknown";
        }
        
        return sanitized;
    }

    /// <summary>
    /// True when the path starts with a Windows drive letter ("E:\" or "E:/").
    /// On a non-Windows host such a path is not a location: the filesystem
    /// treats it as a literal directory name.
    /// </summary>
    public static bool LooksLikeWindowsDrivePath(string? path)
    {
        return !string.IsNullOrEmpty(path)
            && path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    /// <summary>
    /// Resolves a unique file path by appending a counter if the file already exists.
    /// </summary>
    /// <param name="basePath">Desired file path.</param>
    /// <returns>Unique file path that does not exist yet.</returns>
    public static string ResolveUniquePath(string basePath)
    {
        if (!IOFile.Exists(basePath))
        {
            return basePath;
        }
        
        var directory = Path.GetDirectoryName(basePath)!;
        var extension = Path.GetExtension(basePath);
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(basePath);
        
        var counter = 1;
        string uniquePath;
        do
        {
            uniquePath = Path.Combine(directory, $"{fileNameWithoutExt} ({counter}){extension}");
            counter++;
        } while (IOFile.Exists(uniquePath));
        
        return uniquePath;
    }
}
