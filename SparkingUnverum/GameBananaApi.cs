using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SparkingUnverum
{
    public enum FeedFilter
    {
        Featured,
        Newest,
        Updated,
        MostDownloaded,
        MostLiked
    }
    public enum TypeFilter
    {
        Mods,
        Sounds,
        WiPs
    }

    // Every call Majin Unverum makes to GameBanana goes through here (API v11)
    public static class GameBananaApi
    {
        private const string Base = "https://gamebanana.com/apiv11";
        public static readonly HttpClient Client = CreateClient();
        private static JsonSerializerOptions Options => GameBananaJson.Options;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"MajinUnverum/{App.Version}");
            return client;
        }

        public static string ModelName(TypeFilter type) => type switch
        {
            TypeFilter.Sounds => "Sound",
            TypeFilter.WiPs => "Wip",
            _ => "Mod"
        };

        private static async Task<T> Get<T>(string url)
        {
            using var response = await Client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, Options);
        }

        public static async Task<GameBananaPage> GetFeed(TypeFilter type, FeedFilter filter, int? categoryId, int page, int perPage)
        {
            var url = $"{Base}/{ModelName(type)}/Index?_nPage={page}&_nPerpage={perPage}";
            url += categoryId != null ? $"&_aFilters[Generic_Category]={categoryId}" : $"&_aFilters[Generic_Game]={SparkingGame.GameBananaId}";
            url += filter switch
            {
                FeedFilter.Featured => "&_aFilters[Generic_WasFeatured]=true&_sSort=Generic_Newest",
                FeedFilter.Updated => "&_sSort=Generic_LatestModified",
                FeedFilter.MostDownloaded => "&_sSort=Generic_MostDownloaded",
                FeedFilter.MostLiked => "&_sSort=Generic_MostLiked",
                _ => "&_sSort=Generic_Newest"
            };
            return await Get<GameBananaPage>(url);
        }

        // Search always returns 15 records per page
        public const int SearchPerPage = 15;
        public static async Task<GameBananaPage> Search(TypeFilter type, string search, int page)
        {
            var url = $"{Base}/Util/Search/Results?_sModelName={ModelName(type)}&_idGameRow={SparkingGame.GameBananaId}" +
                $"&_sSearchString={Uri.EscapeDataString(search)}&_sOrder=best_match&_nPage={page}";
            return await Get<GameBananaPage>(url);
        }

        public static async Task<List<GameBananaCategory>> GetCategories(TypeFilter type, int? parentId = null)
        {
            var url = parentId == null
                ? $"{Base}/{ModelName(type)}/Categories?_idGameRow={SparkingGame.GameBananaId}&_sSort=a_to_z&_bShowEmpty=true"
                : $"{Base}/{ModelName(type)}/Categories?_idCategoryRow={parentId}&_sSort=a_to_z&_bShowEmpty=true";
            return await Get<List<GameBananaCategory>>(url) ?? new();
        }

        public static async Task<GameBananaSubmission> GetSubmission(string model, long id)
        {
            var submission = await Get<GameBananaSubmission>($"{Base}/{model}/{id}?_csvProperties={GameBananaSubmission.Properties}");
            if (submission != null)
            {
                submission.Model = model;
                if (submission.Id == 0)
                    submission.Id = id;
            }
            return submission;
        }

        // Fetches many submissions of the same model at once, used to check for mod updates.
        // A chunk that fails is reported in errors and the rest are still returned
        public static async Task<List<GameBananaSubmission>> GetSubmissions(string model, IEnumerable<long> ids, List<Exception> errors = null)
        {
            var result = new List<GameBananaSubmission>();
            foreach (var chunk in ids.Distinct().Chunk(40))
            {
                try
                {
                    var url = $"{Base}/{model}/Multi?_csvRowIds={string.Join(",", chunk)}&_csvProperties={GameBananaSubmission.UpdateProperties}";
                    var submissions = await Get<List<GameBananaSubmission>>(url);
                    if (submissions == null)
                        continue;
                    foreach (var submission in submissions)
                        submission.Model = model;
                    result.AddRange(submissions);
                }
                catch (Exception e) when (errors != null)
                {
                    errors.Add(e);
                }
            }
            return result;
        }

        // https://gamebanana.com/mods/123 -> ("Mod", 123)
        public static bool TryParseProfileUrl(string url, out string model, out long id)
        {
            model = null;
            id = 0;
            if (string.IsNullOrWhiteSpace(url))
                return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) && !Uri.TryCreate("https://" + url, UriKind.Absolute, out uri))
                return false;
            if (!uri.Host.Equals("gamebanana.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.gamebanana.com", StringComparison.OrdinalIgnoreCase))
                return false;
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length != 2 || !long.TryParse(segments[1], out id))
                return false;
            model = segments[0].ToLowerInvariant() switch
            {
                "mods" => "Mod",
                "sounds" => "Sound",
                "wips" => "Wip",
                _ => null
            };
            return model != null;
        }
    }
}
