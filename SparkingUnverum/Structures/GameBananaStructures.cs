using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SparkingUnverum
{
    // Shapes returned by https://gamebanana.com/apiv11
    public static class GameBananaTime
    {
        private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static DateTime FromUnix(long seconds) => Epoch.AddSeconds(seconds);
        public static Uri ToUri(string url) => !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }
    public class GameBananaImage
    {
        [JsonPropertyName("_sType")]
        public string Type { get; set; }
        [JsonPropertyName("_sBaseUrl")]
        public string Base { get; set; }
        [JsonPropertyName("_sFile")]
        public string File { get; set; }
        [JsonPropertyName("_sFile530")]
        public string File530 { get; set; }
        [JsonPropertyName("_sCaption")]
        public string Caption { get; set; }
        [JsonIgnore]
        public Uri Url => GameBananaTime.ToUri($"{Base}/{File}");
        [JsonIgnore]
        public Uri ThumbnailUrl => GameBananaTime.ToUri($"{Base}/{File530 ?? File}");
    }
    public class GameBananaPreviewMetadata
    {
        [JsonPropertyName("_sAudioUrl")]
        public string AudioUrl { get; set; }
    }
    public class GameBananaPreviewMedia
    {
        [JsonPropertyName("_aImages")]
        public List<GameBananaImage> Images { get; set; }
        [JsonPropertyName("_aMetadata")]
        public GameBananaPreviewMetadata Metadata { get; set; }
    }
    public class GameBananaMember
    {
        [JsonPropertyName("_idRow")]
        public long Id { get; set; }
        [JsonPropertyName("_sName")]
        public string Name { get; set; }
        [JsonPropertyName("_sAvatarUrl")]
        public string AvatarUrl { get; set; }
        [JsonPropertyName("_sUpicUrl")]
        public string UpicUrl { get; set; }
        [JsonIgnore]
        public Uri Avatar => GameBananaTime.ToUri(AvatarUrl);
        [JsonIgnore]
        public Uri Upic => GameBananaTime.ToUri(UpicUrl);
        [JsonIgnore]
        public bool HasUpic => Upic != null;
    }
    public class GameBananaCategory
    {
        [JsonPropertyName("_idRow")]
        public int? ID { get; set; }
        [JsonPropertyName("_sName")]
        public string Name { get; set; }
        [JsonPropertyName("_sIconUrl")]
        public string IconUrl { get; set; }
        [JsonPropertyName("_sProfileUrl")]
        public string ProfileUrl { get; set; }
        [JsonPropertyName("_sUrl")]
        public string Url { get; set; }
        [JsonPropertyName("_nCategoryCount")]
        public int ChildCount { get; set; }
        [JsonPropertyName("_nItemCount")]
        public int ItemCount { get; set; }
        [JsonIgnore]
        public Uri Icon => GameBananaTime.ToUri(IconUrl);
        [JsonIgnore]
        public bool HasIcon => Icon != null;
        // Index records only give the url of the root category, the id is at the end of it
        [JsonIgnore]
        public int? ResolvedID
        {
            get
            {
                if (ID != null)
                    return ID;
                var match = Regex.Match(ProfileUrl ?? Url ?? string.Empty, @"/cats/(\d+)");
                return match.Success ? int.Parse(match.Groups[1].Value) : null;
            }
        }
    }
    public class GameBananaItemFile
    {
        [JsonPropertyName("_idRow")]
        public long Id { get; set; }
        [JsonPropertyName("_sFile")]
        public string FileName { get; set; }
        [JsonPropertyName("_nFilesize")]
        public long Filesize { get; set; }
        [JsonPropertyName("_sDownloadUrl")]
        public string DownloadUrl { get; set; }
        [JsonPropertyName("_sDescription")]
        public string Description { get; set; }
        [JsonPropertyName("_nDownloadCount")]
        public int Downloads { get; set; }
        [JsonPropertyName("_tsDateAdded")]
        public long DateAddedLong { get; set; }
        [JsonPropertyName("_sMd5Checksum")]
        public string Md5 { get; set; }
        [JsonIgnore]
        public string ConvertedFileSize => StringConverters.FormatSize(Filesize);
        [JsonIgnore]
        public string DownloadString => StringConverters.FormatNumber(Downloads);
        [JsonIgnore]
        public DateTime DateAdded => GameBananaTime.FromUnix(DateAddedLong);
        [JsonIgnore]
        public string TimeSinceUpload => StringConverters.FormatTimeAgo(DateTime.UtcNow - DateAdded);
        [JsonIgnore]
        public string Details => Loc.T("Files.Details", ConvertedFileSize, TimeSinceUpload, DownloadString);
        [JsonIgnore]
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    }
    public class GameBananaItemUpdate
    {
        [JsonPropertyName("_sTitle")]
        public string Title { get; set; }
        [JsonPropertyName("_sVersion")]
        public string Version { get; set; }
        [JsonPropertyName("_aChangeLog")]
        public List<GameBananaItemUpdateChange> Changes { get; set; }
        [JsonPropertyName("_sText")]
        public string Text { get; set; }
        [JsonPropertyName("_tsDateAdded")]
        public long DateAddedLong { get; set; }
        [JsonIgnore]
        public DateTime DateAdded => GameBananaTime.FromUnix(DateAddedLong);
    }
    public class GameBananaItemUpdateChange
    {
        [JsonPropertyName("cat")]
        public string Category { get; set; }
        [JsonPropertyName("text")]
        public string Text { get; set; }
    }
    public class GameBananaAlternateFileSource
    {
        [JsonPropertyName("url")]
        public Uri Url { get; set; }
        [JsonPropertyName("description")]
        public string Description { get; set; } = "Mirror";
    }
    public class GameBananaGame
    {
        [JsonPropertyName("_idRow")]
        public long Id { get; set; }
        [JsonPropertyName("_sName")]
        public string Name { get; set; }
    }

    // Full information about a mod, sound or WiP (Model/{id}?_csvProperties=... or Model/Multi)
    public class GameBananaSubmission
    {
        // Everything needed to update a mod and fill its mod.json
        public const string UpdateProperties = "_idRow,_sName,_sProfileUrl,_aSubmitter,_aGame,_aCategory,_aSuperCategory,_aPreviewMedia," +
            "_sDescription,_aFiles,_aAlternateFileSources,_tsDateAdded,_tsDateUpdated,_bHasUpdates,_aLatestUpdates,_bIsNsfw," +
            "_nLikeCount,_nViewCount,_nDownloadCount,_bIsTrashed,_bIsWithheld,_bIsPrivate,_nStatus";
        // Plus the full description shown in the mod store
        public const string Properties = UpdateProperties + ",_sText";
        [JsonPropertyName("_idRow")]
        public long Id { get; set; }
        [JsonIgnore]
        public string Model { get; set; } = "Mod";
        [JsonPropertyName("_sName")]
        public string Title { get; set; }
        [JsonPropertyName("_sProfileUrl")]
        public string ProfileUrl { get; set; }
        [JsonIgnore]
        public Uri Link => GameBananaTime.ToUri(ProfileUrl);
        [JsonPropertyName("_aSubmitter")]
        public GameBananaMember Owner { get; set; }
        [JsonPropertyName("_aGame")]
        public GameBananaGame Game { get; set; }
        [JsonPropertyName("_aCategory")]
        public GameBananaCategory Category { get; set; }
        [JsonPropertyName("_aSuperCategory")]
        public GameBananaCategory RootCategory { get; set; }
        [JsonPropertyName("_aPreviewMedia")]
        public GameBananaPreviewMedia Media { get; set; }
        [JsonPropertyName("_sDescription")]
        public string Description { get; set; }
        [JsonPropertyName("_sText")]
        public string Text { get; set; }
        [JsonPropertyName("_aFiles")]
        public List<GameBananaItemFile> Files { get; set; }
        [JsonPropertyName("_aAlternateFileSources")]
        public List<GameBananaAlternateFileSource> AlternateFileSources { get; set; }
        [JsonPropertyName("_tsDateAdded")]
        public long DateAddedLong { get; set; }
        [JsonPropertyName("_tsDateUpdated")]
        public long? DateUpdatedLong { get; set; }
        [JsonPropertyName("_bHasUpdates")]
        public bool? HasUpdates { get; set; }
        [JsonPropertyName("_aLatestUpdates")]
        public List<GameBananaItemUpdate> Updates { get; set; }
        [JsonPropertyName("_bIsNsfw")]
        public bool IsNsfw { get; set; }
        [JsonPropertyName("_nLikeCount")]
        public int Likes { get; set; }
        [JsonPropertyName("_nViewCount")]
        public int Views { get; set; }
        [JsonPropertyName("_nDownloadCount")]
        public int Downloads { get; set; }
        // Deleted by its author
        [JsonPropertyName("_bIsTrashed")]
        public bool IsTrashed { get; set; }
        // Hidden by the GameBanana moderators
        [JsonPropertyName("_bIsWithheld")]
        public bool IsWithheld { get; set; }
        [JsonPropertyName("_bIsPrivate")]
        public bool IsPrivate { get; set; }
        // 0 = visible, 3 = deleted, 6 = withheld. Batch requests don't always set the flags above, but they do send the status
        [JsonPropertyName("_nStatus")]
        public int Status { get; set; }
        // Every visible submission has a publication date, deleted and withheld ones come without it
        [JsonIgnore]
        public bool IsUnavailable => IsTrashed || IsWithheld || IsPrivate || Status != 0 || DateAddedLong == 0;

        [JsonIgnore]
        public List<GameBananaImage> Images => Media?.Images?.Where(x => x.Url != null).ToList() ?? new();
        [JsonIgnore]
        public Uri Image => Images.Count > 0 ? Images[0].Url : null;
        [JsonIgnore]
        public List<GameBananaItemFile> AllFiles => Files ?? new();
        [JsonIgnore]
        public bool HasFiles => AllFiles.Count > 0;
        [JsonIgnore]
        public bool HasAltLinks => AlternateFileSources != null && AlternateFileSources.Count > 0;
        [JsonIgnore]
        public DateTime? DateUpdated => DateUpdatedLong != null && DateUpdatedLong > 0 ? GameBananaTime.FromUnix((long)DateUpdatedLong) : null;
        [JsonIgnore]
        public string CategoryName => Category == null ? string.Empty
            : RootCategory == null || RootCategory.Name == Category.Name ? Category.Name : $"{RootCategory.Name} / {Category.Name}";
        [JsonIgnore]
        public string ConvertedText => StringConverters.ConvertHtmlToText(Text ?? Description ?? string.Empty);
        // Date of the newest update, used to detect mod updates
        [JsonIgnore]
        public DateTime? LatestUpdateDate => Updates != null && Updates.Count > 0 ? Updates[0].DateAdded : null;
    }

    // One entry of Model/Index or Util/Search/Results, the details are fetched when needed
    public class GameBananaRecord
    {
        [JsonPropertyName("_idRow")]
        public long Id { get; set; }
        [JsonPropertyName("_sModelName")]
        public string Model { get; set; }
        [JsonPropertyName("_sName")]
        public string Title { get; set; }
        [JsonPropertyName("_sProfileUrl")]
        public string ProfileUrl { get; set; }
        [JsonIgnore]
        public Uri Link => GameBananaTime.ToUri(ProfileUrl);
        [JsonPropertyName("_tsDateAdded")]
        public long DateAddedLong { get; set; }
        [JsonPropertyName("_tsDateModified")]
        public long DateModifiedLong { get; set; }
        [JsonPropertyName("_tsDateUpdated")]
        public long? DateUpdatedLong { get; set; }
        [JsonPropertyName("_bHasFiles")]
        public bool HasFiles { get; set; }
        [JsonPropertyName("_aPreviewMedia")]
        public GameBananaPreviewMedia Media { get; set; }
        [JsonPropertyName("_aSubmitter")]
        public GameBananaMember Owner { get; set; }
        [JsonPropertyName("_aRootCategory")]
        public GameBananaCategory Category { get; set; }
        [JsonPropertyName("_sVersion")]
        public string Version { get; set; }
        [JsonPropertyName("_bHasContentRatings")]
        public bool IsNsfw { get; set; }
        [JsonPropertyName("_nLikeCount")]
        public int Likes { get; set; }
        [JsonPropertyName("_nPostCount")]
        public int Posts { get; set; }
        [JsonPropertyName("_nViewCount")]
        public int Views { get; set; }
        [JsonPropertyName("_bWasFeatured")]
        public bool WasFeatured { get; set; }

        [JsonIgnore]
        public bool IsSpoiler => Title != null && Title.ToUpperInvariant().StartsWith("(SPOILER)");
        [JsonIgnore]
        public Uri Image => Media?.Images?.FirstOrDefault()?.ThumbnailUrl ?? GameBananaTime.ToUri("https://images.gamebanana.com/static/img/DefaultEmbeddables/Sound.jpg");
        [JsonIgnore]
        public string CategoryName => Category?.Name ?? string.Empty;
        [JsonIgnore]
        public bool Compatible => HasFiles;
        [JsonIgnore]
        public DateTime DateAdded => GameBananaTime.FromUnix(DateAddedLong);
        [JsonIgnore]
        public DateTime DateUpdated => DateUpdatedLong != null && DateUpdatedLong > 0 ? GameBananaTime.FromUnix((long)DateUpdatedLong) : DateAdded;
        [JsonIgnore]
        public bool HasUpdates => DateUpdated > DateAdded;
        [JsonIgnore]
        public string DateAddedFormatted => Loc.T("Browser.Added", StringConverters.FormatTimeAgo(DateTime.UtcNow - DateAdded));
        [JsonIgnore]
        public string DateUpdatedAgo => Loc.T("Browser.Updated", StringConverters.FormatTimeAgo(DateTime.UtcNow - DateUpdated));
        [JsonIgnore]
        public string DateAddedTooltip => Loc.T("Browser.AddedOn", DateAdded.ToLocalTime().ToString("g"));
        [JsonIgnore]
        public string DateUpdatedTooltip => Loc.T("Browser.UpdatedOn", DateUpdated.ToLocalTime().ToString("g"));
        [JsonIgnore]
        public string LikeString => StringConverters.FormatNumber(Likes);
        [JsonIgnore]
        public string ViewString => StringConverters.FormatNumber(Views);
        [JsonIgnore]
        public string PostString => StringConverters.FormatNumber(Posts);
        [JsonIgnore]
        public string StatsTooltip => Loc.T("Browser.StatsTooltip", Likes.ToString("N0"), Views.ToString("N0"), Posts.ToString("N0"));
        [JsonIgnore]
        public string VersionString => string.IsNullOrWhiteSpace(Version) ? string.Empty : $"v{Version.TrimStart('v', 'V')}";
    }

    public class GameBananaPageMetadata
    {
        [JsonPropertyName("_nRecordCount")]
        public int RecordCount { get; set; }
        [JsonPropertyName("_nPerpage")]
        public int PerPage { get; set; }
        [JsonPropertyName("_bIsComplete")]
        public bool IsComplete { get; set; }
    }
    public class GameBananaPage
    {
        [JsonPropertyName("_aMetadata")]
        public GameBananaPageMetadata Metadata { get; set; }
        [JsonPropertyName("_aRecords")]
        public List<GameBananaRecord> Records { get; set; }
    }
}
