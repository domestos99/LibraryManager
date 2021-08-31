using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Contracts;
using Microsoft.Web.LibraryManager.Contracts.Caching;
using Microsoft.Web.LibraryManager.LibraryNaming;
using Microsoft.Web.LibraryManager.Providers.Cdnjs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Web.LibraryManager.Providers.json
{
    internal class JsonCatalog : ILibraryCatalog
    {
        public const string CatalogUrl = "http://localhost:62748/libraries/catalog.json";

        private const string FileName = "cache.json";

        private readonly bool _underTest;
        private readonly string _cacheFile;
        private readonly JsonProvider _provider;
        private readonly ICacheService _cacheService;
        private readonly ILibraryNamingScheme _libraryNamingScheme;
        private IEnumerable<JsonLibraryGroup> _libraryGroups;

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonCatalog"/> class.
        /// </summary>
        /// <param name="provider">The provider.</param>
        /// <param name="underTest">if set to <c>true</c> [under test].</param>
        public JsonCatalog(JsonProvider provider, ICacheService cacheService, ILibraryNamingScheme libraryNamingScheme, bool underTest = false)
        {
            _provider = provider;
            _cacheService = cacheService;
            _cacheFile = Path.Combine(provider.CacheFolder, FileName);
            _libraryNamingScheme = libraryNamingScheme;
            _underTest = underTest;
        }

        /// <summary>
        /// Gets a list of completion spans for use in the JSON file.
        /// </summary>
        /// <param name="value">The current state of the library ID.</param>
        /// <param name="caretPosition">The caret position inside the <paramref name="value" />.</param>
        /// <returns></returns>
        public async Task<CompletionSet> GetLibraryCompletionSetAsync(string value, int caretPosition)
        {
            if (!await EnsureCatalogAsync(CancellationToken.None).ConfigureAwait(false))
            {
                return default;
            }

            var completionSet = new CompletionSet
            {
                Start = 0,
                Length = value.Length
            };

            int at = value.IndexOf('@');
            string name = at > -1 ? value.Substring(0, at) : value;

            var completions = new List<CompletionItem>();

            // Name
            if (at == -1 || caretPosition <= at)
            {
                IReadOnlyList<ILibraryGroup> result = await SearchAsync(name, int.MaxValue, CancellationToken.None).ConfigureAwait(false);

                foreach (JsonLibraryGroup group in result)
                {
                    var completion = new CompletionItem
                    {
                        DisplayText = group.DisplayName,
                        InsertionText = _libraryNamingScheme.GetLibraryId(group.DisplayName, group.Version),
                        Description = group.Description,
                    };

                    completions.Add(completion);
                }

                completionSet.CompletionType = CompletionSortOrder.AsSpecified;
            }

            // Version
            else
            {
                JsonLibraryGroup group = _libraryGroups.FirstOrDefault(g => g.DisplayName == name);

                if (group != null)
                {
                    completionSet.Start = at + 1;
                    completionSet.Length = value.Length - completionSet.Start;

                    IEnumerable<Asset> assets = await GetAssetsAsync(name, CancellationToken.None).ConfigureAwait(false);

                    foreach (string version in assets.Select(a => a.Version))
                    {
                        var completion = new CompletionItem
                        {
                            DisplayText = version,
                            InsertionText = _libraryNamingScheme.GetLibraryId(name, version),
                        };

                        completions.Add(completion);
                    }
                }

                completionSet.CompletionType = CompletionSortOrder.Version;
            }

            completionSet.Completions = completions;

            return completionSet;
        }

        /// <summary>
        /// Searches the catalog for the specified search term.
        /// </summary>
        /// <param name="term">The search term.</param>
        /// <param name="maxHits">The maximum number of results to return.</param>
        /// <param name="cancellationToken">A token that allows the search to be cancelled.</param>
        /// <returns></returns>
        public async Task<IReadOnlyList<ILibraryGroup>> SearchAsync(string term, int maxHits, CancellationToken cancellationToken)
        {
            if (!await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false))
            {
                return Enumerable.Empty<ILibraryGroup>().ToList();
            }

            IEnumerable<JsonLibraryGroup> results;

            if (string.IsNullOrEmpty(term))
            {
                results = _libraryGroups.Take(maxHits);
            }
            else
            {
                results = GetSortedSearchResult(term).Take(maxHits);
            }

            foreach (JsonLibraryGroup group in results)
            {
                string groupName = group.DisplayName;
                group.DisplayInfosTask = ct => GetLibraryVersionsAsync(groupName, ct);
            }

            return results.ToList();
        }

        /// <summary>
        /// Gets the library group from the specified <paramref name="libraryName" />.
        /// </summary>
        /// <param name="libraryName">The name of the library.</param>
        /// <param name="version">Version of the library. (Ignored for FileSystemProvider)</param>
        /// <param name="cancellationToken">A token that allows the search to be cancelled.</param>
        /// <returns>
        /// An instance of <see cref="Microsoft.Web.LibraryManager.Contracts.ILibraryGroup" /> or <code>null</code>.
        /// </returns>
        public async Task<ILibrary> GetLibraryAsync(string libraryName, string version, CancellationToken cancellationToken)
        {
            string libraryId = _libraryNamingScheme.GetLibraryId(libraryName, version);

            if (string.IsNullOrEmpty(libraryName) || string.IsNullOrEmpty(version))
            {
                throw new InvalidLibraryException(libraryId, _provider.Id);
            }

            try
            {
                IEnumerable<Asset> assets = await GetAssetsAsync(libraryName, cancellationToken).ConfigureAwait(false);
                Asset asset = assets.FirstOrDefault(a => a.Version == version);

                if (asset == null)
                {
                    throw new InvalidLibraryException(libraryId, _provider.Id);
                }

                return new CdnjsLibrary
                {
                    Version = version,
                    Files = asset.Files.ToDictionary(k => k, b => b == asset.DefaultFile),
                    Name = libraryName,
                    ProviderId = _provider.Id,
                };
            }
            catch
            {
                throw new InvalidLibraryException(libraryId, _provider.Id);
            }

        }

        /// <summary>
        /// Gets the latest version of the library.
        /// </summary>
        /// <param name="libraryId">The library identifier.</param>
        /// <param name="includePreReleases">if set to <c>true</c> includes pre-releases.</param>
        /// <param name="cancellationToken">A token that allows the search to be cancelled.</param>
        /// <returns>
        /// The library identifier of the latest released version.
        /// </returns>
        public async Task<string> GetLatestVersion(string libraryName, bool includePreReleases, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(libraryName))
            {
                return null;
            }

            if (!await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            JsonLibraryGroup group = _libraryGroups.FirstOrDefault(l => l.DisplayName == libraryName);

            if (group == null)
            {
                return null;
            }

            string first = includePreReleases
                ? (await GetLibraryVersionsAsync(group.DisplayName, cancellationToken).ConfigureAwait(false))
                                                                                      .Select(v => SemanticVersion.Parse(v))
                                                                                      .Max()
                                                                                      .ToString()
                : group.Version;

            if (!string.IsNullOrEmpty(first))
            {
                return first;
            }

            return null;
        }

        private IEnumerable<JsonLibraryGroup> GetSortedSearchResult(string term)
        {
            var list = new List<Tuple<int, JsonLibraryGroup>>();

            foreach (JsonLibraryGroup group in _libraryGroups)
            {
                string cleanName = NormalizedGroupName(group.DisplayName);

                if (cleanName.Equals(term, StringComparison.OrdinalIgnoreCase))
                    list.Add(Tuple.Create(50, group));
                else if (group.DisplayName.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                    list.Add(Tuple.Create(20 + (term.Length - cleanName.Length), group));
                else if (group.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) > -1)
                    list.Add(Tuple.Create(1, group));
            }

            return list.OrderByDescending(t => t.Item1).Select(t => t.Item2);
        }


        private static string NormalizedGroupName(string groupName)
        {
            if (groupName.EndsWith("js", StringComparison.OrdinalIgnoreCase))
            {
                groupName = groupName
                    .Substring(0, groupName.Length - 2)
                    .TrimEnd('-', '.');
            }

            return groupName;
        }

        private async Task<bool> EnsureCatalogAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                string json = await _cacheService.GetContentsFromUriWithCacheFallbackAsync(CatalogUrl, _cacheFile, cancellationToken).ConfigureAwait(false);

                _libraryGroups = ConvertToLibraryGroups(json);

                return _libraryGroups != null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Write(ex);
                return false;
            }
        }

        private async Task<IEnumerable<string>> GetLibraryVersionsAsync(string groupName, CancellationToken cancellationToken)
        {
            IEnumerable<Asset> assets = await GetAssetsAsync(groupName, cancellationToken).ConfigureAwait(false);

            return assets?.Select(a => a.Version);
        }


        private async Task<IEnumerable<Asset>> GetAssetsAsync(string groupName, CancellationToken cancellationToken)
        {
            var assets = new List<Asset>();

            if (!await EnsureCatalogAsync(CancellationToken.None).ConfigureAwait(false))
            {
                return default;
            }

            try
            {
                assets = ConvertToAssets(_libraryGroups);
            }
            catch (Exception)
            {
                throw new InvalidLibraryException(groupName, _provider.Id);
            }

            return assets;
        }

        public IEnumerable<JsonLibraryGroup> ConvertToLibraryGroups(string json)
        {
            try
            {
                string obj = ((JObject)JsonConvert.DeserializeObject(json))["results"].ToString();

                return JsonConvert.DeserializeObject<IEnumerable<JsonLibraryGroup>>(obj).ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Write(ex);

                return null;
            }
        }


        internal List<Asset> ConvertToAssets(string json)
        {
            return ConvertToAssets(ConvertToLibraryGroups(json));
        }
        internal List<Asset> ConvertToAssets(IEnumerable<JsonLibraryGroup> data)
        {
            try
            {
                var output = new List<Asset>();

                foreach (JsonLibraryGroup lib in data)
                {
                    var asset = new Asset()
                    {
                        Version = lib.Version,
                        Files = lib.Files.ToArray(),
                        DefaultFile = lib.DisplayName
                    };
                    output.Add(asset);
                }

                return output;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.Write(ex);

                return null;
            }
        }
                
    }
}
